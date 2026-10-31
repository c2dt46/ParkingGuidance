using System.Diagnostics;
using System.Text.RegularExpressions;
using OpenCvSharp;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Persistence;
using ParkingGuidance.Web.Services.Settings;
using Tesseract;

namespace ParkingGuidance.Web.Services.Anpr;

/// <summary>
/// Quy trình đọc biển số: (1) tìm vùng biển — bằng model ONNX dò biển số nếu có, ngược lại/khi không thấy thì dùng PlateLocator
/// (xử lý ảnh); (2) đọc chữ — bằng model OCR ONNX nếu có, bổ sung/dự phòng bằng Tesseract; (3) chuẩn hoá theo định dạng biển VN
/// và bỏ phiếu giữa các lần đọc. Việc bỏ phiếu GIỮA CÁC KHUNG HÌNH nằm ở <c>ParkingService</c> (PlateTracker).
/// </summary>
public class TesseractPlateRecognitionService : IPlateRecognitionService, ICharModelReloader, IOcrModelReloader, IPlateDetectorModel, IDisposable
{
    private readonly TesseractEngine? _engine;
    private readonly PlateTextReader? _reader;
    private readonly OnnxPlateDetector? _plateDetector;
    private OnnxPlateDetector? _customPlateDetector;           // bo do bien da train them (tab Train bien so) — uu tien truoc bo do mac dinh; doi duoc luc dang chay: xem Reload(out string)
    private readonly string _customPlatePath;                  // {DataDirectory}/plate-train/active/plate.onnx
    private readonly string? _baseOnnxPath;                    // plate_detector.onnx goc (neu co) — moc so sanh khi train
    private string? _plateLoadError;
    private OnnxPlateReader? _onnxReader;                       // CRNN+CTC doc ca chuoi — doi duoc luc dang chay: xem ReloadOcr
    private string? _ocrPath;
    private readonly IConfiguration _config;
    private readonly DarknetPlateDetector? _darknetDetector;   // YOLOv3-tiny do bien (repo License-Plate-Recognition)
    private YoloCharReader? _charDet;                          // (TUY CHON) bo do ky tu YOLO11 — uu tien truoc CNN phan loai; tu nap lai khi file doi
    private string _charDetPath = "";                          // file dang nap
    private DateTime _charDetStamp;                            // gio sua file dang nap
    private DateTime _charDetChecked = DateTime.MinValue;      // lan kiem tra file gan nhat
    private readonly string _charDetActivePath;                // {DataDirectory}/char-det/char_det.onnx (do tools/train_chars.py xuat) — uu tien hon file trong models/
    private readonly string? _charDetBasePath;                 // Anpr:CharDetModelPath
    private CnnPlateReader? _cnnReader;                          // CNN phan loai tung ky tu (thay Tesseract) — doi duoc luc dang chay: xem Reload
    private string? _cnnPath;                                    // file model ky tu dang nap
    private readonly string? _debugDir;
    private readonly bool _debugAll;
    private bool _warnedOnnx;
    private readonly int _maxRecognizeMs;                      // tran thoi gian cho 1 lan doc binh thuong (Anpr:MaxRecognizeMs, 0 = khong gioi han)
    private readonly int _quickMaxMs;                          // tran thoi gian cho lan do nhanh tren ca khung hinh (Anpr:QuickMaxMs)
    // Cach tinh khung bien sau khi doc, doi duoc luc dang chay (Cau hinh chung > PlateBoxMode): 0 = giu khung bo do bien so ("off"), 1 = theo dai ky tu ("chars")
    private bool CharsBoxMode => (_settings.Current.PlateBoxMode ?? 0) == 1;
    private readonly bool _shapeFilter;                         // loc khung bien theo hinh hoc (Anpr:PlateShapeFilter, mac dinh bat): xem PlateBoxFilter
    private readonly bool _classicalFallback;                   // cho phep PlateLocator (xu ly anh) chay du phong KHI DA CO bo do neural (Anpr:ClassicalPlateFallback, mac dinh tat)
    private readonly bool _relaxedSearch;                       // lan do noi long khi lan thuong khong ra bien doc duoc (Anpr:RelaxedSearch, mac dinh bat)
    // TesseractEngine KHONG an toan da luong: API demo va dich vu camera nen co the goi dong thoi
    private readonly object _ocrLock = new();
    private readonly ILogger<TesseractPlateRecognitionService> _logger;
    private readonly ISettingsService _settings;                // nguong tin cay / chat luong anh: doi trong "Cau hinh chung" co hieu luc ngay

    private const float TrainSuggestConf = 0.30f;          // = PlateTrainApiController.SuggestConfidence

    private float PlateBoxConf => Math.Clamp(_settings.Current.PlateBoxConfidence ?? 0.4f, 0.05f, 0.95f);

    /// <summary>Điểm bỏ phiếu đủ cao (≈ 2 lần đọc trùng nhau) để dừng sớm cho nhanh.</summary>
    private const double EarlyStopScore = 3.2;
    private const int MaxCandidates = 5;

    /// <summary>true (mặc định): khi kết quả mới chỉ có 1 bộ đọc đồng ý thì nhờ thêm Tesseract đọc độc lập (Anpr:RequireSecondOpinion).</summary>
    private readonly bool _requireSecondOpinion;
    /// <summary>true (mặc định): xác định màu nền biển và áp cú pháp theo màu (Anpr:UseColorRules).</summary>
    private readonly bool _useColorRules;
    /// <summary>Thư mục ghi log từng lần đọc (JSON lines) để đo bộ đọc nào sai ở đâu; null = tắt (Anpr:ReadLog = false).</summary>
    private readonly string? _readLogDir;
    /// <summary>Công cụ chạy thử hàng loạt đặt tên ảnh vào đây trước khi gọi đọc, để script thống kê ghép được với đáp án đúng.</summary>
    public static readonly AsyncLocal<string?> LogTag = new();

    /// <summary>true (mặc định): tìm thấy khung biển KHÔNG dừng lại — tìm tiếp trong khu vực đó, so sánh các vị trí rồi lấy vị trí đọc tốt nhất (Anpr:RefineAfterFound).</summary>
    private readonly bool _refineAfterFound;
    /// <summary>Số khung tối đa thử thêm ở bước tìm tiếp (Anpr:RefineMaxRects).</summary>
    private readonly int _refineMaxRects;
    private const double RefineTargetWidth = 640;      // vùng quanh biển nhỏ hơn mức này thì phóng to trước khi dò lại
    private const double RefineDupIou = 0.9;           // khung trùng ≥ 90% với khung đã thử thì bỏ
    private const double RefineImproveFactor = 1.05;   // vị trí mới phải hơn vị trí hiện tại ≥ 5% mới được thay (tránh đổi qua lại vì nhiễu)

    public bool IsReady => _engine is not null || _onnxReader is not null || _cnnReader is not null || _charDet is not null;

    private bool HasCharReader => _cnnReader is not null || _charDet is not null;

    /// <summary>Lần đọc ONNX có độ tin cậy từ mức này là đủ tin để khỏi chạy thêm Tesseract.</summary>
    private const float OnnxTrustedConfidence = 0.8f;

    public TesseractPlateRecognitionService(
        IConfiguration config, IPersistenceService persistence, ILogger<TesseractPlateRecognitionService> logger, ISettingsService settings)
    {
        _logger = logger;
        _config = config;
        _settings = settings;
        _customPlatePath = Path.Combine(persistence.DataDirectory, "plate-train", "active", "plate.onnx");

        // Mac dinh chi nhan bien 5 so (NNL-NNN.NN). Bien o to doi cu 4 so rat hiem va de bi nhan nham
        // khi OCR roi mat 1 chu so, nen tat mac dinh; bat lai bang Anpr:AllowLegacy4DigitPlates = true.
        VietnamPlateFormatter.AllowLegacyFourDigit = config.GetValue("Anpr:AllowLegacy4DigitPlates", false);
        PlateSyntax.AcceptUnknown = config.GetValue("Anpr:AcceptUnknownPlates", true);   // false = loại hẳn biển không khớp cấu trúc nào
        _requireSecondOpinion = config.GetValue("Anpr:RequireSecondOpinion", true);
        _useColorRules = config.GetValue("Anpr:UseColorRules", true);
        PlateNormalizer.PruneMaxScore = config.GetValue("Anpr:RepairMaxScore", 1.0f);
        PlateSyntax.Configure(config);                     // Anpr:BlueSeries, Anpr:PlateRules
        _refineAfterFound = config.GetValue("Anpr:RefineAfterFound", true);
        _refineMaxRects = Math.Clamp(config.GetValue("Anpr:RefineMaxRects", 6), 0, 12);
        _readLogDir = config.GetValue("Anpr:ReadLog", true) ? Path.Combine(persistence.DataDirectory, "anpr-log") : null;

        _relaxedSearch = config.GetValue("Anpr:RelaxedSearch", true);
        _shapeFilter = config.GetValue("Anpr:PlateShapeFilter", true);
        _classicalFallback = config.GetValue("Anpr:ClassicalPlateFallback", false);
        _maxRecognizeMs = Math.Max(0, config.GetValue("Anpr:MaxRecognizeMs", 8000));
        _quickMaxMs = Math.Max(0, config.GetValue("Anpr:QuickMaxMs", 2500));

        var configuredDir = config["Anpr:TessDataPath"] ?? "tessdata";

        // Thu tim thu muc tessdata o nhieu vi tri kha di vi thu muc lam viec co the khac tuy cach chay ung dung
        var candidates = new[]
        {
            configuredDir,
            Path.Combine(AppContext.BaseDirectory, configuredDir),
            Path.Combine(AppContext.BaseDirectory, "tessdata"),
            Path.Combine(Directory.GetCurrentDirectory(), "tessdata"),
        }
        .Select(Path.GetFullPath)
        .Distinct()
        .ToList();

        var found = candidates.FirstOrDefault(d => Directory.Exists(d) && Directory.GetFiles(d, "*.traineddata").Length > 0);

        if (found is not null)
        {
            try
            {
                // Tat tu dien he thong/tan suat: bien so la ma so, khong phai tu co nghia, tu dien lam OCR "sua" sai
                var initialOptions = new Dictionary<string, object>
                {
                    ["load_system_dawg"] = false,
                    ["load_freq_dawg"] = false,
                };

                _engine = new TesseractEngine(found, "eng", EngineMode.Default,
                    Enumerable.Empty<string>(), initialOptions, false);
                _engine.SetVariable("tessedit_char_whitelist", "ABCDEFGHKLMNPRSTUVXYZ0123456789.-");
                _reader = new PlateTextReader(_engine);
                _logger.LogInformation("Da khoi tao Tesseract OCR voi tessdata: {Path}", found);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Khong the khoi tao Tesseract. Tinh nang ANPR se bi tat.");
            }
        }
        else
        {
            _logger.LogWarning(
                "Khong tim thay 'eng.traineddata' o bat ky vi tri nao da thu:\n - {Paths}\n" +
                "Tai file nay va dat vao 1 trong cac thu muc tren de bat tinh nang doc bien so.",
                string.Join("\n - ", candidates));
        }

        // --- (Tuy chon) model ONNX: do bien so + doc ky tu. Khong co file thi bo qua, dung quy trinh cu. ---
        var detPath = ResolveModel(config["Anpr:DetectorModelPath"] ?? "models/plate_detector.onnx");
        if (detPath is not null)
        {
            _plateDetector = OnnxPlateDetector.TryCreate(detPath, _logger);
            if (_plateDetector is not null) _baseOnnxPath = detPath;
        }
        // Bo do bien da train them o tab Train bien so (neu co file) — uu tien truoc bo do mac dinh
        Reload(out _);
        var ocrPath = ResolveModel(config["Anpr:OcrModelPath"] ?? "models/plate_ocr.onnx");
        if (ocrPath is not null)
        {
            _onnxReader = OnnxPlateReader.TryCreate(ocrPath, config, _logger);
            if (_onnxReader is not null) _ocrPath = ocrPath;
        }

        // --- (Tuy chon) CNN doc ky tu + YOLOv3-tiny do bien so (model cua repo License-Plate-Recognition).
        //     Co file thi uu tien dung; Tesseract chi con la du phong khi CNN khong doc duoc gi. Tat: Anpr:UseCnn = false. ---
        if (config.GetValue("Anpr:UseCnn", true))
        {
            var cnnPath = ResolveModel(config["Anpr:CharCnnModelPath"] ?? "models/char_cnn.onnx");
            if (cnnPath is not null)
            {
                _cnnReader = CnnPlateReader.TryCreate(cnnPath, _logger);
                if (_cnnReader is not null) _cnnPath = cnnPath;
            }
            var cfgPath = ResolveModel(config["Anpr:PlateCfgPath"] ?? "models/plate/yolov3-tiny.cfg");
            var wPath = ResolveModel(config["Anpr:PlateWeightsPath"] ?? "models/plate/yolov3-tiny_15000.weights");
            if (cfgPath is not null && wPath is not null) _darknetDetector = DarknetPlateDetector.TryCreate(cfgPath, wPath, _logger);
        }

        // --- (Tuy chon) bo do ky tu YOLO11: doc thang tren anh bien, khong nhi phan hoa/tach ky tu. Co file thi uu tien dung truoc CNN. ---
        _charDetActivePath = Path.Combine(persistence.DataDirectory, "char-det", "char_det.onnx");
        _charDetBasePath = ResolveModel(config["Anpr:CharDetModelPath"] ?? "models/char_det.onnx");
        RefreshCharDetector(force: true);

        // --- (Tuy chon) luu anh trung gian de do loi o buoc nao: Anpr:DebugDir = thu muc luu ---
        var dbg = config["Anpr:DebugDir"];
        if (!string.IsNullOrWhiteSpace(dbg))
        {
            try
            {
                _debugDir = Path.GetFullPath(dbg);
                Directory.CreateDirectory(_debugDir);
                _debugAll = config.GetValue("Anpr:DebugAll", false);
                _logger.LogInformation("ANPR debug: luu anh {Mode} vao {Dir}", _debugAll ? "MOI LAN DOC" : "KHI DOC SAI/KHONG DOC DUOC", _debugDir);
            }
            catch (Exception ex)
            {
                _debugDir = null;
                _logger.LogWarning(ex, "Khong tao duoc thu muc Anpr:DebugDir.");
            }
        }
    }

    public string? CurrentModelPath => _cnnPath;

    // ===================================================================== IOcrModelReloader (tab Train > Train doc chuoi OCR)
    public string? CurrentOcrModelPath => _ocrPath;

    /// <summary>Nạp lại model OCR khi đang chạy. Đổi dưới <c>_ocrLock</c> như <see cref="Reload(string, out string)"/> của model ký tự,
    /// nên huỷ model cũ ngay sau khi đổi là an toàn.</summary>
    public bool ReloadOcr(string modelPath, out string message)
    {
        var fresh = OnnxPlateReader.TryCreate(modelPath, _config, _logger);
        if (fresh is null)
        {
            message = "Không nạp được model OCR mới (file hỏng hoặc sai định dạng) — giữ nguyên model cũ.";
            return false;
        }

        OnnxPlateReader? old;
        lock (_ocrLock)
        {
            old = _onnxReader;
            _onnxReader = fresh;
            _ocrPath = modelPath;
        }
        old?.Dispose();
        message = "Đã nạp lại model OCR, áp dụng ngay.";
        return true;
    }

    /// <summary>Nạp lại model ký tự khi đang chạy. Đổi dưới <c>_ocrLock</c> — lúc đó không có lần đọc nào đang dùng model cũ,
    /// nên huỷ model cũ ngay sau khi đổi là an toàn.</summary>
    public bool Reload(string modelPath, out string message)
    {
        var fresh = CnnPlateReader.TryCreate(modelPath, _logger);
        if (fresh is null)
        {
            message = "Không nạp được model mới (file hỏng hoặc sai định dạng) — giữ nguyên model cũ.";
            return false;
        }

        CnnPlateReader? old;
        lock (_ocrLock)
        {
            old = _cnnReader;
            _cnnReader = fresh;
            _cnnPath = modelPath;
        }
        old?.Dispose();
        message = "Đã nạp lại model ký tự, áp dụng ngay.";
        return true;
    }

    // ===================================================================== IPlateDetectorModel (tab Train bien so)
    // IsReady cua IPlateRecognitionService = doc duoc chu; IsReady cua IPlateDetectorModel = co bo do bien. Hai y nghia khac nhau nen cai dat tuong minh.
    bool IPlateDetectorModel.IsReady => _customPlateDetector is not null || _plateDetector is not null || _darknetDetector is not null;

    public string CustomModelPath => _customPlatePath;
    public string? BaseModelPath => _baseOnnxPath;
    public string? LoadError => _plateLoadError;
    public bool UsingCustomModel => _customPlateDetector is not null;

    public string ModelInfo
    {
        get
        {
            var c = _customPlateDetector;
            if (c is not null)
            {
                string stamp = "";
                try { stamp = ", cập nhật " + File.GetLastWriteTime(_customPlatePath).ToString("dd/MM/yyyy HH:mm"); } catch { /* bo qua */ }
                return $"model biển số đã train thêm (plate.onnx, ảnh vào {c.InputSizeText}{stamp}) — ưu tiên trước bộ dò mặc định";
            }
            if (_darknetDetector is not null) return "bộ dò mặc định YOLOv3-tiny (Darknet) — chưa train thêm";
            if (_plateDetector is not null) return "bộ dò mặc định plate_detector.onnx — chưa train thêm";
            return "chưa có bộ dò biển (đang dùng xử lý ảnh PlateLocator)";
        }
    }

    /// <summary>Nạp lại bộ dò biển đã train thêm khi đang chạy (không có file = bỏ model đã train thêm). Đổi dưới <c>_ocrLock</c> như Reload của model ký tự.</summary>
    public bool Reload(out string message)
    {
        OnnxPlateDetector? fresh = null;
        if (File.Exists(_customPlatePath))
        {
            fresh = OnnxPlateDetector.TryCreate(_customPlatePath, _logger);
            if (fresh is null)
            {
                _plateLoadError = "Không nạp được model biển số đã train (file hỏng hoặc sai định dạng) — giữ nguyên model đang chạy.";
                message = _plateLoadError;
                return false;
            }
        }

        OnnxPlateDetector? old;
        lock (_ocrLock)
        {
            old = _customPlateDetector;
            _customPlateDetector = fresh;
        }
        old?.Dispose();
        _plateLoadError = null;
        message = fresh is null ? "Đang dùng bộ dò biển mặc định." : "Đã nạp lại bộ dò biển số, áp dụng ngay.";
        return true;
    }

    public List<(double X1, double Y1, double X2, double Y2, float Score)> SuggestBoxes(Mat image, float confThreshold)
    {
        var result = new List<(double X1, double Y1, double X2, double Y2, float Score)>();
        if (image.Empty()) return result;

        // Lay khung cua tung bo do theo thu tu uu tien (model da train → Darknet → ONNX goc); bo do nao cho khung hop le (sau loc hinh hoc) thi dung luon.
        List<(double X1, double Y1, double X2, double Y2, float Score)> Keep(IEnumerable<(OpenCvSharp.Rect R, float Score)> found)
        {
            var all = found.Select(f => (X1: (double)f.R.X, Y1: (double)f.R.Y, X2: (double)f.R.Right, Y2: (double)f.R.Bottom, f.Score)).ToList();
            return _shapeFilter
                ? PlateBoxFilter.Filter(all, b => (b.X2 - b.X1, b.Y2 - b.Y1), image.Width, image.Height)
                : all;
        }

        lock (_ocrLock)
        {
            try
            {
                var custom = _customPlateDetector;
                if (custom is not null)
                {
                    result = Keep(custom.Detect(image, confThreshold, 6, expand: 0f));
                    if (result.Count > 0) return result;
                }
                if (_darknetDetector is not null)
                {
                    result = Keep(_darknetDetector.Detect(image, confThreshold, 6));
                    if (result.Count > 0) return result;
                }
                if (_plateDetector is not null)
                    result = Keep(_plateDetector.Detect(image, confThreshold, 6, expand: 0f));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Train bien so: khong goi y duoc khung bien.");
            }
        }
        return result;
    }

    private static string? ResolveModel(string configured)
    {
        var name = Path.GetFileName(configured);
        return new[]
        {
            configured,
            Path.Combine(AppContext.BaseDirectory, configured),
            Path.Combine(AppContext.BaseDirectory, name),
            Path.Combine(Directory.GetCurrentDirectory(), name),
        }
        .Select(Path.GetFullPath)
        .FirstOrDefault(File.Exists);
    }

    public (string? PlateNumber, float Confidence, BoundingBox? PlateBox) Recognize(Mat vehicleCrop)
    {
        if (!IsReady || vehicleCrop.Empty())
            return (null, 0f, null);

        lock (_ocrLock)
        {
            return RecognizeLocked(vehicleCrop);
        }
    }

    public (string? PlateNumber, float Confidence, BoundingBox? PlateBox) RecognizeQuick(Mat frame)
    {
        if (!IsReady || frame.Empty())
            return (null, 0f, null);

        lock (_ocrLock)
        {
            return RecognizeLocked(frame, quick: true);
        }
    }

    /// <summary>Đẩy cài đặt chung (ngưỡng ký tự, chiều cao ảnh biển khi đọc) sang bộ đọc CNN trước mỗi lần đọc.</summary>
    private void ApplyReaderSettings()
    {
        RefreshCharDetector();
        var st = _settings.Current;
        float minChar = Math.Clamp(st.CharConfidence ?? 0.5f, 0f, 0.95f);
        var cnn = _cnnReader;
        if (cnn is not null)
        {
            cnn.MinCharConfidence = minChar;
            cnn.TargetHeight = Math.Clamp(st.PlateReadHeight ?? 110, 40, 300);
        }
        var yolo = _charDet;
        if (yolo is not null) yolo.MinCharConfidence = minChar;
    }

    /// <summary>Nạp / nạp lại model dò ký tự khi file đổi (kiểm tra tối đa 15 giây/lần). Gọi dưới <c>_ocrLock</c> nên đổi model là an toàn.
    /// Model không nạp được thì giữ model cũ (nếu có) và chỉ thử lại khi file đổi.</summary>
    private void RefreshCharDetector(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - _charDetChecked).TotalSeconds < 15) return;
        _charDetChecked = now;

        string? path = File.Exists(_charDetActivePath) ? _charDetActivePath : _charDetBasePath;
        if (path is null || !File.Exists(path)) return;

        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(path); } catch { return; }
        if (path == _charDetPath && stamp == _charDetStamp) return;     // khong doi (hoac da thu nap va hong)

        var fresh = YoloCharReader.TryCreate(path, _logger);
        _charDetPath = path;
        _charDetStamp = stamp;
        if (fresh is null) return;

        var old = _charDet;
        _charDet = fresh;
        old?.Dispose();
    }

    public List<BoundingBox> DetectPlateBoxes(Mat frame)
    {
        var result = new List<BoundingBox>();
        if (frame.Empty()) return result;
        lock (_ocrLock)
        {
            // Chi 1 luot nhanh tren ca khung hinh (khong chia o, khong dung PlateLocator) — chay song song voi buoc do xe
            var scores = new Dictionary<OpenCvSharp.Rect, float>();
            var rects = DetectPlateRects(frame, relaxed: false, quickOnly: true, scores: scores);
            // Dong bo voi tab Train bien so (SuggestBoxes dung nguong 0,30): khi nguong chung (mac dinh 0,4) khong ra khung nao thi thu lai 1 lan o 0,30.
            // Khung yeu van phai doc ra bien hop le moi duoc ghi nhan nen it rui ro khung rac.
            if (rects.Count == 0 && PlateBoxConf > TrainSuggestConf)
                rects = DetectPlateRects(frame, relaxed: false, quickOnly: true, scores: scores, confOverride: TrainSuggestConf);
            foreach (var r in rects)
                result.Add(new BoundingBox
                {
                    X1 = r.X, Y1 = r.Y, X2 = r.X + r.Width, Y2 = r.Y + r.Height,
                    Score = scores.TryGetValue(r, out var sc) ? sc : 0f      // do tin cay cua bo do: dung de xep hang khung khi 1 xe co nhieu khung ung vien
                });
        }
        return result;
    }

    private static string ColorKey(PlateColor c) => c.ToString().ToLowerInvariant();

    /// <summary>
    /// Độ tin cậy HIỂN THỊ / ghi nhận = độ tin cậy đọc ký tự giảm theo cú pháp: chuỗi không khớp cú pháp nào ×0,5, biển dân sự mã tỉnh lạ ×0,8.
    /// Trước đây chuỗi sai cú pháp (vd chỉ đọc được "11916") vẫn hiện 100% và có thể được coi là đã xác thực.
    /// </summary>
    private static float ShownConfidence(PlateClass cls, float readConfidence) =>
        cls.Kind == PlateKind.Unknown ? readConfidence * 0.5f
        : (cls.Kind is PlateKind.Civilian or PlateKind.Business && !cls.KnownProvince) ? readConfidence * 0.8f
        : readConfidence;

    public (string? PlateNumber, float Confidence, BoundingBox? PlateBox) ReadPlateAt(Mat frame, BoundingBox plateBox)
    {
        if (!IsReady || frame.Empty()) return (null, 0f, null);

        // Khung chinh + cac khung ung vien khac cua CUNG xe (neu co): doc TAT CA roi chon vi tri doc tot nhat — khong dung o khung duoc chon bang hinh hoc.
        var group = new List<BoundingBox> { plateBox };
        if (_refineAfterFound && plateBox.Alternatives is { Count: > 0 })
            group.AddRange(plateBox.Alternatives.Take(4));
        AddMergedPlateRect(group);   // bo do hay cat 1 bien thanh 2 khung nua bien: thu them khung GOP de doc du bien

        lock (_ocrLock)
        {
            ApplyReaderSettings();
            PlateVote? best = null;
            Dictionary<string, PlateVote>? bestVotes = null;
            OpenCvSharp.Rect bestRect = default, firstRect = default;
            bool haveFirst = false;
            var bestColor = default(PlateColorAnalyzer.Result);
            double bestPos = 0;
            var reads = new List<(Dictionary<string, PlateVote> Votes, OpenCvSharp.Rect Rect, PlateColorAnalyzer.Result Color)>();   // phieu cua MOI khung da doc

            for (int gi = 0; gi < group.Count; gi++)
            {
                var pb = group[gi];
                int x1 = Math.Clamp((int)Math.Floor(pb.X1), 0, Math.Max(0, frame.Width - 1));
                int y1 = Math.Clamp((int)Math.Floor(pb.Y1), 0, Math.Max(0, frame.Height - 1));
                int x2 = Math.Clamp((int)Math.Ceiling(pb.X2), x1 + 1, frame.Width);
                int y2 = Math.Clamp((int)Math.Ceiling(pb.Y2), y1 + 1, frame.Height);
                var rect = new OpenCvSharp.Rect(x1, y1, x2 - x1, y2 - y1);
                if (rect.Width < 8 || rect.Height < 8) continue;
                if (!haveFirst) { firstRect = rect; haveFirst = true; }

                using var crop = new Mat(frame, rect);
                var votes = new Dictionary<string, PlateVote>();
                var plateColor = _useColorRules ? PlateColorAnalyzer.Detect(frame, rect) : default;
                reads.Add((votes, rect, plateColor));

                // Cung cach doc nhu trong RecognizeLocked, nhung tren khung bien da co
                if (HasCharReader) AddCnnVotes(frame, rect, votes, plateColor.Color);
                if (_onnxReader is not null) AddOnnxVote(crop, votes, plateColor.Color);

                bool trusted = votes.Values.Any(v => v.BestConfidence >= OnnxTrustedConfidence);
                if (!trusted && _reader is not null && !(HasCharReader && votes.Count > 0))
                {
                    int padH = (int)(crop.Height * 0.12), padW = (int)(crop.Width * 0.12);
                    using var padded = new Mat();
                    Cv2.CopyMakeBorder(crop, padded, padH, padH, padW, padW, BorderTypes.Replicate);
                    _reader.CollectVotes(padded, votes, plateColor.Color);
                    _reader.CollectVotes(crop, votes, plateColor.Color);
                }

                // Diem VI TRI = diem doc ky tu x he so tin cay cua bo do khung (khung khong biet diem → 0,35 trung tinh)
                float detScore = pb.Score > 0 ? pb.Score : 0.35f;
                var top = PlateVoteResolver.Best(votes);
                double posScore = (top?.Score ?? 0) * (0.5 + Math.Clamp(detScore, 0f, 1f));
                LogReads(votes, top, rect, gi == 0 ? "read" : "alt", pb.Score > 0 ? pb.Score : -1f, plateColor);

                // Khung chinh duoc uu tien: khung khac phai hon >= 5% moi thay
                if (top is not null && (best is null || posScore > bestPos * RefineImproveFactor))
                {
                    best = top;
                    bestVotes = votes;
                    bestRect = rect;
                    bestColor = plateColor;
                    bestPos = posScore;
                }
            }

            if (best is null)
            {
                if (!haveFirst) return (null, 0f, plateBox);
                SaveDebug(frame, firstRect, null, 0f);
                var shown = CharsBoxMode ? firstRect : TightPlateRect(frame, firstRect);
                var unreadColor = _useColorRules ? PlateColorAnalyzer.Detect(frame, firstRect) : default;
                return (null, 0f, new BoundingBox
                {
                    X1 = shown.X, Y1 = shown.Y, X2 = shown.X + shown.Width, Y2 = shown.Y + shown.Height,
                    PlateColor = ColorKey(unreadColor.Color), PlateColorShare = (float)unreadColor.Share
                });
            }

            // Vi tri thang: neu moi co 1 loai bo doc dong y thi nho Tesseract xac nhan (trong pham vi mau cua chinh vi tri do)
            PlateClass cls;
            {
                EnsureSecondOpinion(frame, bestRect, bestVotes!, () => false, bestColor.Color);
                best = PlateVoteResolver.Best(bestVotes!) ?? best;
                // Khung thang chi doc duoc doan cuoi (vd "01970") trong khi khung khac doc du bien hop le ("30A01970") → lay bien day du
                var beforeRule = best;
                if (PreferFullPlate(best, reads) is { } full)
                {
                    best = full.Vote; bestVotes = full.Votes; bestRect = full.Rect; bestColor = full.Color;
                }
                TraceReads(reads, beforeRule, best);
                cls = PlateSyntax.Classify(best.Pretty, null, bestColor.Color);
            }

            var refined = FinalPlateRect(frame, best, bestRect);
            var box = new BoundingBox
            {
                X1 = refined.X, Y1 = refined.Y, X2 = refined.X + refined.Width, Y2 = refined.Y + refined.Height,
                PlateColor = ColorKey(bestColor.Color), PlateColorShare = (float)bestColor.Share,
                PlateKind = cls.Kind.ToString(), PlateLabel = cls.Label,
                PlateChars = best.Chars
            };
            SaveDebug(frame, refined, best.Pretty, best.BestConfidence);
            return (best.Pretty, ShownConfidence(cls, best.BestConfidence), box);
        }
    }

    private (string? PlateNumber, float Confidence, BoundingBox? PlateBox) RecognizeLocked(Mat vehicleCrop, bool quick = false)
    {
        // Gioi han thoi gian: anh khong co bien (hoac bien rat kho) tung chay het moi buoc du phong, moi ung vien lai goi CNN + Tesseract
        // nhieu lan — mat nhieu giay, lai giu khoa _ocrLock lam cac lan doc khac (ke ca camera that) phai cho. Het han muc thi dung viec thu them.
        ApplyReaderSettings();
        var clock = Stopwatch.StartNew();
        long budgetMs = quick ? _quickMaxMs : _maxRecognizeMs;
        bool OutOfTime() => budgetMs > 0 && clock.ElapsedMilliseconds > budgetMs;
        bool hasLearnedDetector = _customPlateDetector is not null || _plateDetector is not null || _darknetDetector is not null;

        PlateVote? best = null;
        OpenCvSharp.Rect bestRect = default;
        PlateColor bestColor = PlateColor.Unknown;         // mau nen cua vi tri dang dan dau
        double bestColorShare = 0;
        Dictionary<string, PlateVote>? bestVotes = null;   // phieu cua vi tri dang dan dau (de nho Tesseract xac nhan sau cung)
        var detScores = new Dictionary<OpenCvSharp.Rect, float>();   // diem tin cay cua bo do khung bien cho tung khung ung vien
        double bestPos = 0;                                // diem VI TRI cua vi tri dan dau (truoc y kien thu 2 de so sanh cong bang)
        var reads = new List<(Dictionary<string, PlateVote> Votes, OpenCvSharp.Rect Rect, PlateColorAnalyzer.Result Color)>();   // phieu cua MOI khung da doc
        bool refine = _refineAfterFound && !quick;         // che do nhanh tren ca khung hinh: khong tim them

        // Thu lan luot cac ung vien vung bien (vung dau tien tim duoc KHONG luon la bien so, vd luoi tan nhiet) va chon ket qua
        // khop dinh dang bien so VN co diem bo phieu cao nhat; dung som khi diem du cao (≈ 2 lan doc trung nhau).
        // stopEarly: dung khi du diem + dong thuan doc lap. secondOpinion: nho Tesseract xac nhan ngay trong vong lap.
        // improveFactor: vi tri moi phai hon vi tri dan dau bay nhieu lan moi duoc thay. stage: nhan ghi log.
        bool TryRects(IEnumerable<OpenCvSharp.Rect> rects, bool stopEarly, bool secondOpinion, double improveFactor, string stage)
        {
            int tried0 = 0;
            foreach (var rect in rects)
            {
                if (tried0++ > 0 && OutOfTime()) break;           // luon thu it nhat 1 ung vien
                using var crop = new Mat(vehicleCrop, rect);
                var votes = new Dictionary<string, PlateVote>();

                // Mau nen bien (trang / vang / xanh / do) → moi buoc chuan hoa + cham diem trong khung nay ap cu phap theo mau
                var plateColor = _useColorRules ? PlateColorAnalyzer.Detect(vehicleCrop, rect) : default;
                reads.Add((votes, rect, plateColor));

                // 0) CNN ky tu (neu co): doc o 3 muc le khac nhau cua khung bien (detector hay cat sat/thieu hang tren)
                if (HasCharReader) AddCnnVotes(vehicleCrop, rect, votes, plateColor.Color);

                // 1) Model OCR ONNX (neu co)
                if (_onnxReader is not null) AddOnnxVote(crop, votes, plateColor.Color);

                // 2) Tesseract: bo qua neu model ONNX da doc duoc voi do tin cay cao
                bool trusted = votes.Values.Any(v => v.BestConfidence >= OnnxTrustedConfidence);
                // Co CNN: chi dung Tesseract khi CNN khong cho duoc phieu hop le nao
                if (!trusted && _reader is not null && !(HasCharReader && votes.Count > 0))
                {
                    // Doc ca ban do them le 12% (thuat toan nhi phan hoa phu thuoc vao vien quanh bien) va ban cat sat
                    int padH = (int)(crop.Height * 0.12), padW = (int)(crop.Width * 0.12);
                    using var padded = new Mat();
                    Cv2.CopyMakeBorder(crop, padded, padH, padH, padW, padW, BorderTypes.Replicate);
                    _reader.CollectVotes(padded, votes, plateColor.Color);
                    _reader.CollectVotes(crop, votes, plateColor.Color);
                }

                // Diem cua VI TRI nay = diem doc ky tu cua ket qua dan dau (TRUOC khi nho Tesseract xac nhan, de moi vi tri cung 1 thuoc do)
                // x he so tin cay cua BO DO khung bien (0,5–1,5): khung co diem do cao (bien that) duoc uu tien hon khung diem thap (den xe, luoi tan nhiet)
                float detScore = detScores.TryGetValue(rect, out var ds) ? ds : 0.35f;
                double posScore = (PlateVoteResolver.Best(votes)?.Score ?? 0) * (0.5 + Math.Clamp(detScore, 0f, 1f));

                // Kết quả chỉ có 1 bộ đọc đồng ý → nhờ Tesseract đọc độc lập trước khi tin
                if (secondOpinion) EnsureSecondOpinion(vehicleCrop, rect, votes, OutOfTime, plateColor.Color);

                var top = PlateVoteResolver.Best(votes);
                LogReads(votes, top, rect, stage, detScore, plateColor);
                if (top is not null && (best is null || posScore > bestPos * improveFactor))
                {
                    best = top;
                    bestRect = rect;
                    bestVotes = votes;
                    bestPos = posScore;
                    bestColor = plateColor.Color;
                    bestColorShare = plateColor.Share;
                }

                // Chỉ dừng sớm khi điểm đủ cao VÀ có ≥ 2 ý kiến độc lập trùng nhau — không dừng chỉ vì 1 lần đọc điểm cao
                if (stopEarly && best is not null && best.Score >= EarlyStopScore && HasIndependentAgreement(best)) return true;
            }
            return best is not null;
        }

        // Lan tim chinh: che do tim-tiep (refine) thi KHONG dung som va KHONG goi Tesseract trong vong lap — so sanh moi vi tri bang bo doc ky tu,
        // roi chi nho Tesseract xac nhan vi tri thang cuoi cung.
        // Ket qua dang dan dau co CAU TRUC (dan su / quan doi / nuoc ngoai) khong? Chuoi "khong ro" (vd doc nham den xe thanh "9RXA7957") KHONG tinh la da tim thay:
        // khi do van tiep tuc do noi long + du phong thay vi dung lai.
        bool Solid() => best is not null && PlateSyntax.Classify(best.Pretty, null, bestColor).Kind != PlateKind.Unknown;

        bool Try(IEnumerable<OpenCvSharp.Rect> candidateRects) => TryRects(candidateRects, !refine, !refine, 1.0, "detect");

        // Vung bien kha di nhat do bo do tim duoc nhung chua doc duoc ky tu → tra ve de nguoi dung thay/chinh o tab Train
        OpenCvSharp.Rect? unread = null;
        var tried = new List<OpenCvSharp.Rect>();

        var detected = DetectPlateRects(vehicleCrop, relaxed: false, scores: detScores);
        // Doc khung co diem bo do cao truoc (neu het thoi gian giua chung thi khung tot nhat da duoc doc)
        if (refine) detected = detected.OrderByDescending(r0 => detScores.TryGetValue(r0, out var d0) ? d0 : 0f).ToList();
        if (detected.Count > 0)
        {
            unread = detected[0];
            tried.AddRange(detected);
            Try(detected);
        }

        // Do nhanh tren ca khung hinh (khong thay xe): co bo do bien da huan luyen ma khong thay bien nao → anh khong co bien, tra ve ngay
        if (quick && hasLearnedDetector && detected.Count == 0)
            return (null, 0f, null);

        // Lan do noi long (ha nguong, o chia nho, can bang sang): chi khi lan thuong chua cho ra bien doc duoc (khong dung o che do nhanh)
        if (!Solid() && _relaxedSearch && !quick && !OutOfTime())
        {
            var more = DetectPlateRects(vehicleCrop, relaxed: true, scores: detScores).Where(r => tried.All(t => RectIou(r, t) < 0.5)).ToList();
            if (more.Count > 0)
            {
                unread ??= more[0];
                tried.AddRange(more);
                Try(more);
            }
        }

        // Du phong bang xu ly anh. O che do nhanh chi dung khi KHONG co bo do bien da huan luyen (khi do day la cach duy nhat), va chi vai ung vien.
        // (Theo khuyen nghi: khong chay phuong phap co dien song song voi YOLO — yeu hon ro khi anh sang kem/bien ban/nghieng va chi them khung gia.)
        // Chi dung khi KHONG co bo do neural nao (khi do day la cach duy nhat), hoac khi bat Anpr:ClassicalPlateFallback.
        if (!Solid() && (!hasLearnedDetector || _classicalFallback) && !OutOfTime())
            Try(PlateLocator.FindPlateCandidates(vehicleCrop, quick ? 2 : MaxCandidates));

        // TIM TIEP trong khu vuc vua tim thay bien: do lai (phong to, ha nguong) + thu vai vi tri lech nhe quanh khung,
        // doc tat ca roi lay VI TRI doc tot nhat (phai hon vi tri hien tai ≥ 5%).
        if (refine && best is not null && _refineMaxRects > 0 && !OutOfTime())
        {
            var extra = BuildRefineRects(vehicleCrop, bestRect, tried, detScores).Take(_refineMaxRects).ToList();
            if (extra.Count > 0)
            {
                tried.AddRange(extra);
                TryRects(extra, false, false, RefineImproveFactor, "refine");
            }
        }

        // Vi tri thang cuoi: nho Tesseract xac nhan (chi khi ket qua moi co 1 loai bo doc)
        if (refine && bestVotes is not null && !OutOfTime())
        {
            EnsureSecondOpinion(vehicleCrop, bestRect, bestVotes, OutOfTime, bestColor);
            best = PlateVoteResolver.Best(bestVotes) ?? best;
        }

        // Ket qua thang chi la doan cuoi cua mot bien hop le da doc o khung khac → lay bien day du
        if (best is not null && PreferFullPlate(best, reads) is { } full)
        {
            best = full.Vote; bestVotes = full.Votes; bestRect = full.Rect;
            bestColor = full.Color.Color; bestColorShare = full.Color.Share;
        }

        if (best is null)
        {
            SaveDebug(vehicleCrop, unread, null, 0f);
            if (unread is { } u0)
            {
                var u = CharsBoxMode ? u0 : TightPlateRect(vehicleCrop, u0);
                return (null, 0f, new BoundingBox { X1 = u.X, Y1 = u.Y, X2 = u.X + u.Width, Y2 = u.Y + u.Height });
            }
            return (null, 0f, null);
        }

        // Khung bao biển tính lại từ DẢI KÝ TỰ đã đọc được (khung cua bo do, nhat la model train it anh, hay bi cut/lech).
        // Khung nay cung la khung goi y cho tab Train nen khung dung se khong "day" nhan sai vao du lieu train.
        bestRect = FinalPlateRect(vehicleCrop, best, bestRect);
        var finalCls = PlateSyntax.Classify(best.Pretty, null, bestColor);
        var box = new BoundingBox
        {
            X1 = bestRect.X,
            Y1 = bestRect.Y,
            X2 = bestRect.X + bestRect.Width,
            Y2 = bestRect.Y + bestRect.Height,
            PlateColor = ColorKey(bestColor), PlateColorShare = (float)bestColorShare,
            PlateKind = finalCls.Kind.ToString(), PlateLabel = finalCls.Label,
            PlateChars = best.Chars
        };
        SaveDebug(vehicleCrop, bestRect, best.Pretty, best.BestConfidence);
        return (best.Pretty, ShownConfidence(finalCls, best.BestConfidence), box);
    }

    /// <summary>Khung biển cuối cùng trả về/hiển thị: PlateBoxMode = 1 → theo dải ký tự đã đọc; PlateBoxMode = 0 → khung ôm sát của bộ dò + offset (xem <see cref="TightPlateRect"/>).</summary>
    private OpenCvSharp.Rect FinalPlateRect(Mat image, PlateVote vote, OpenCvSharp.Rect readRect) =>
        CharsBoxMode ? RefineRectFromChars(vote, readRect, image.Width, image.Height, "chars") : TightPlateRect(image, readRect);

    /// <summary>
    /// Khung biển ÔM SÁT như tab Train biển số (cùng cách dò của <see cref="SuggestBoxes"/>: model đã train trước, không nới viền), cộng thêm offset
    /// (cài đặt PlateBoxOffsetPercent, % mỗi phía). Khung dùng để ĐỌC ký tự có thể đã bị nới viền/lấy từ Darknet (rộng hơn biển) nên ở đây dò lại
    /// và chọn khung ôm sát trùng nhiều nhất với khung đã đọc; không tìm được thì dùng chính khung đã đọc. Chỉ ảnh hưởng khung HIỂN THỊ/ghi nhận, không ảnh hưởng việc đọc.
    /// </summary>
    private OpenCvSharp.Rect TightPlateRect(Mat image, OpenCvSharp.Rect readRect)
    {
        var tight = readRect;
        try
        {
            double bestIou = 0.3;
            foreach (var b in SuggestBoxes(image, PlateBoxConf))   // _ocrLock là khoá tái nhập (cùng luồng) nên gọi lồng được
            {
                int x1 = (int)Math.Floor(b.X1), y1 = (int)Math.Floor(b.Y1);
                var r = new OpenCvSharp.Rect(x1, y1, (int)Math.Ceiling(b.X2) - x1, (int)Math.Ceiling(b.Y2) - y1);
                if (r.Width < 8 || r.Height < 4) continue;
                double iou = RectIou(r, readRect);
                if (iou > bestIou) { bestIou = iou; tight = r; }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Khong tinh duoc khung bien om sat — dung khung da doc.");
        }

        double off = Math.Clamp(_settings.Current.PlateBoxOffsetPercent ?? 4f, 0f, 30f) / 100.0;
        double dx = tight.Width * off, dy = tight.Height * off;
        int nx1 = (int)Math.Max(0, Math.Floor(tight.X - dx));
        int ny1 = (int)Math.Max(0, Math.Floor(tight.Y - dy));
        int nx2 = (int)Math.Min(image.Width, Math.Ceiling(tight.Right + dx));
        int ny2 = (int)Math.Min(image.Height, Math.Ceiling(tight.Bottom + dy));
        return nx2 - nx1 < 8 || ny2 - ny1 < 4 ? tight : new OpenCvSharp.Rect(nx1, ny1, nx2 - nx1, ny2 - ny1);
    }

    /// <summary>
    /// Khung biển TRẢ VỀ / HIỂN THỊ sau khi đọc. Khung của bộ dò (YOLO) đã chính xác (mAP50 ≈ 99%) nên MẶC ĐỊNH chỉ ĐƯỢC MỞ RỘNG, không bao giờ thu hẹp:
    ///   • "expand" (mặc định): dải ký tự đọc được nằm trong khung bộ dò (sai số 4–6%) → GIỮ NGUYÊN khung bộ dò. Chỉ khi dải ký tự lòi hẳn ra ngoài
    ///     (khung bị cụt) mới mở rộng khung để chứa đủ dải ký tự + lề. Nhờ vậy biển 2 dòng mà chỉ đọc được 1 dòng KHÔNG còn làm khung co về 1 dòng.
    ///   • "off": luôn giữ khung bộ dò.   • "chars": kiểu cũ — luôn thay bằng khung tính từ dải ký tự (lề cố định 10% ngang / 28% dọc) → hay cắt thừa/cắt thiếu.
    /// Hiện chọn qua Cấu hình chung (SystemSettings.PlateBoxMode): 0 → "off" (giữ khung bộ dò), 1 → "chars" (theo dải ký tự); "expand" không còn được chọn.
    /// </summary>
    private static OpenCvSharp.Rect RefineRectFromChars(PlateVote vote, OpenCvSharp.Rect detected, int imgW, int imgH, string mode)
    {
        if (mode == "off" || !vote.HasSpan) return detected;
        double w = vote.SpanX2 - vote.SpanX1, h = vote.SpanY2 - vote.SpanY1;
        if (w < 16 || h < 6) return detected;

        double cxs = (vote.SpanX1 + vote.SpanX2) / 2, cys = (vote.SpanY1 + vote.SpanY2) / 2;
        // dai ky tu phai nam gan khung bo do (cho phep khung cut: tam dai ky tu lech toi da 1 chieu rong khung) va co kich thuoc cung bac
        bool near = Math.Abs(cxs - (detected.X + detected.Width / 2.0)) <= detected.Width
                    && Math.Abs(cys - (detected.Y + detected.Height / 2.0)) <= detected.Height * 1.5;
        bool sane = w <= detected.Width * 3.0 && h <= detected.Height * 3.0;
        if (!near || !sane) return detected;

        if (mode == "expand")
        {
            double tolX = Math.Max(2, 0.04 * detected.Width), tolY = Math.Max(2, 0.06 * detected.Height);
            bool inside = vote.SpanX1 >= detected.X - tolX && vote.SpanX2 <= detected.Right + tolX
                          && vote.SpanY1 >= detected.Y - tolY && vote.SpanY2 <= detected.Bottom + tolY;
            if (inside) return detected;          // dai ky tu nam trong khung bo do: khung bo do da dung → khong dung vao
        }

        double padX = Math.Max(3, 0.10 * w), padY = Math.Max(2, 0.28 * h);
        int x1 = (int)Math.Max(0, Math.Floor(vote.SpanX1 - padX));
        int y1 = (int)Math.Max(0, Math.Floor(vote.SpanY1 - padY));
        int x2 = (int)Math.Min(imgW, Math.Ceiling(vote.SpanX2 + padX));
        int y2 = (int)Math.Min(imgH, Math.Ceiling(vote.SpanY2 + padY));
        if (x2 - x1 < 24 || y2 - y1 < 8) return detected;

        if (mode == "expand")                      // hop voi khung bo do: chi to ra, khong co lai
        {
            x1 = Math.Min(x1, detected.X); y1 = Math.Min(y1, detected.Y);
            x2 = Math.Max(x2, detected.Right); y2 = Math.Max(y2, detected.Bottom);
        }
        return new OpenCvSharp.Rect(x1, y1, x2 - x1, y2 - y1);
    }

    private static double RectIou(OpenCvSharp.Rect a, OpenCvSharp.Rect b)
    {
        int x1 = Math.Max(a.X, b.X), y1 = Math.Max(a.Y, b.Y);
        int x2 = Math.Min(a.Right, b.Right), y2 = Math.Min(a.Bottom, b.Bottom);
        double inter = Math.Max(0, x2 - x1) * (double)Math.Max(0, y2 - y1);
        double uni = a.Width * (double)a.Height + b.Width * (double)b.Height - inter;
        return uni <= 0 ? 0 : inter / uni;
    }

    private List<OpenCvSharp.Rect> DetectPlateRects(Mat vehicleCrop, bool relaxed, bool quickOnly = false, bool applyShapeFilter = true,
                                                    Dictionary<OpenCvSharp.Rect, float>? scores = null, float? confOverride = null)
    {
        float boxConf = confOverride ?? PlateBoxConf;
        // Che do tim-tiep: lay NHIEU khung hon (ngưỡng thấp sinh nhiều khung nhiễu như đèn xe, nên nếu cắt còn 3–4 khung thì biển thật có thể bị loại);
        // tất cả đều được đọc và so sánh nên khung nhiễu không gây hại, chỉ tốn thời gian (có giới hạn thời gian chung).
        bool wide = _refineAfterFound && !relaxed;
        var list = new List<OpenCvSharp.Rect>();
        var custom = _customPlateDetector;
        if (custom is null && _plateDetector is null && _darknetDetector is null) return list;

        // Cai dat chung "Bo do khung bien": 0 = gop ca hai, 1 = chi YOLO11n da train (khong co model thi dung Darknet), 2 = chi Darknet
        int mode = Math.Clamp(_settings.Current.PlateDetector ?? 0, 0, 2);
        bool useYolo = mode != 2;
        bool useDarknet = mode != 1 || (custom is null && _plateDetector is null);

        // Gop khung cua NHIEU bo do (model da train them + Darknet mac dinh) thay vi dung o bo do dau tien co ket qua:
        // model train them co the cho khung bi cut (mat ky tu dau/cuoi), khi do khung cua Darknet moi cho doc dung.
        // Chi bo khung gan nhu TRUNG HAN (IoU > 0.85); khung cut van co IoU ~0.7 voi khung day du nen duoc giu lai de thu ca hai.
        void AddRect(OpenCvSharp.Rect r, double score)
        {
            if (list.All(x => RectIou(x, r) <= 0.85))
            {
                list.Add(r);
                if (scores is not null) scores[r] = (float)score;      // diem tin cay cua BO DO cho khung nay
            }
        }

        try
        {
            // Duong nhanh tren ca khung hinh (quickOnly) can khung OM SAT bien (khong noi vien): phan doc ky tu tu noi vien khi cat
            if (custom is not null && useYolo)
                foreach (var (rect, sc) in custom.Detect(vehicleCrop, relaxed ? 0.15f : boxConf, relaxed ? 4 : (wide ? 6 : 3), expand: quickOnly ? 0f : 0.06f)) AddRect(rect, sc);
        }
        catch (Exception ex)
        {
            if (!_warnedOnnx) { _warnedOnnx = true; _logger.LogWarning(ex, "Model do bien so ONNX loi khi chay — dung bo do khac/PlateLocator thay the."); }
        }

        try
        {
            if (_darknetDetector is not null && useDarknet)
            {
                // Da co khung tu model train them thi chi dung 1 luot nhanh (khong chia o) de bo sung; chua co thi dung day du nhu cu.
                var found = relaxed ? _darknetDetector.DetectRelaxed(vehicleCrop)
                          : (quickOnly || list.Count > 0) ? _darknetDetector.DetectQuick(vehicleCrop, boxConf)
                          : _darknetDetector.Detect(vehicleCrop, boxConf);
                foreach (var (rect, sc) in found) AddRect(rect, sc);
            }
            if (!relaxed && list.Count == 0 && _plateDetector is not null && useYolo)
                foreach (var (rect, sc) in _plateDetector.Detect(vehicleCrop, boxConf)) AddRect(rect, sc);
        }
        catch (Exception ex)
        {
            if (!_warnedOnnx) { _warnedOnnx = true; _logger.LogWarning(ex, "Model do bien so ONNX loi khi chay — dung PlateLocator thay the."); }
        }
        return (applyShapeFilter ? ShapeFilter(list, vehicleCrop.Width, vehicleCrop.Height) : list).Take(relaxed ? 5 : (wide ? 8 : 4)).ToList();
    }

    /// <summary>Loc khung theo hinh hoc (xem <see cref="PlateBoxFilter"/>): bo khung vo ly, xep khung dung dang truoc khung nghi cut. Tat bang Anpr:PlateShapeFilter.</summary>
    private List<OpenCvSharp.Rect> ShapeFilter(List<OpenCvSharp.Rect> rects, int searchW, int searchH) =>
        _shapeFilter ? PlateBoxFilter.Filter(rects, r => (r.Width, r.Height), searchW, searchH) : rects;

    /// <summary>
    /// Đọc ký tự bằng CNN trên khung biển với nhiều mức nới viền. Detector (nhất là model train ít ảnh) hay cho khung CỤT ở đầu/cuối biển,
    /// mà mức nới cũ chỉ 0–12% ngang nên không bù nổi. Nên: 3 mức nới nhỏ như cũ → nếu chưa có phiếu hợp lệ nào, hoặc khung nghi bị cụt
    /// (biển 1 dòng mà tỉ lệ rộng/cao &lt; 4, hoặc ký tự chạm mép ảnh cắt) thì thử thêm các mức nới ngang mạnh (một phía và hai phía).
    /// Phiếu của các mức nới mạnh có trọng số thấp hơn (dễ lẫn nền xe) — chỉ thắng khi các mức thường không đọc được/không đồng thuận.
    /// </summary>
    private void AddCnnVotes(Mat vehicleCrop, OpenCvSharp.Rect rect, Dictionary<string, PlateVote> votes, PlateColor color)
    {
        try
        {
            double aspect = rect.Width / (double)Math.Max(1, rect.Height);
            bool suspectCut = aspect >= 2.2 && aspect < 4.0;          // 1 dong that thuong >= ~4.3 (bien o to 520x110); < 2.2 la bien 2 dong
            bool edgeCut = false;

            // Bo do ky tu YOLO (neu da train): doc thang tren anh bien — khong phu thuoc nhi phan hoa/tach ky tu. Doc duoc thi dung han,
            // khong tron phieu cua cach doc co dien (2 cach sai kieu khac nhau se lam phieu loang).
            var yolo = _charDet;
            if (yolo is not null)
            {
                bool cut = ReadYoloLevel(yolo, vehicleCrop, rect, 0.10, 0.10, 0.20, 1.0, votes, color);
                if (votes.Count == 0 || suspectCut || cut)
                    ReadYoloLevel(yolo, vehicleCrop, rect, 0.30, 0.30, 0.25, votes.Count == 0 ? 1.0 : 0.7, votes, color);
                if (votes.Count > 0 && (_cnnReader is null || HasStructuredVote(votes)))
                {
                    // Ket hop: YOLO chua chac (do tin cay thap / cac lan doc bat dong / khung nghi cut) -> nho Tesseract kiem chung (tuy chon)
                    if ((_settings.Current.CharCombine ?? 1) != 0 && _reader is not null && YoloUncertain(votes, suspectCut || cut))
                        AddTesseractVerification(vehicleCrop, rect, votes, color);
                    return;
                }
            }
            if (_cnnReader is null) return;

            // (le trai, le phai, le doc) theo ty le khung: sat, vua, rong hon; khung do co the cat mat hang chu tren
            foreach (var (fl, fr, fy) in new[] { (0.08, 0.08, 0.08), (0.12, 0.12, 0.25) })
                edgeCut |= ReadCnnLevel(vehicleCrop, rect, fl, fr, fy, 1.0, votes, color);
            ReadCnnLevel(vehicleCrop, rect, 0.0, 0.0, 0.0, 1.0, votes, color);   // cat sat (cham mep la binh thuong nen khong tinh vao edgeCut)

            if (votes.Count == 0 || suspectCut || edgeCut)
            {
                double ws = votes.Count == 0 ? 1.0 : 0.6;
                foreach (var (fl, fr, fy) in new[] { (0.0, 0.4, 0.2), (0.4, 0.0, 0.2), (0.4, 0.4, 0.25) })
                    ReadCnnLevel(vehicleCrop, rect, fl, fr, fy, ws, votes, color);
            }
        }
        catch (Exception ex)
        {
            if (!_warnedOnnx) { _warnedOnnx = true; _logger.LogWarning(ex, "CNN ky tu loi khi chay — dung Tesseract thay the."); }
        }
    }

    /// <summary>YOLO coi là CHẮC khi kết quả duy nhất, đủ tự tin và khung không nghi bị cụt; còn lại thì cần Tesseract kiểm chứng.</summary>
    private const float YoloSureConfidence = 0.85f;

    /// <summary>Tổng điểm tối đa Tesseract được cộng cho MỖI kết quả. Đủ để phân xử khi các kết quả YOLO ngang nhau hoặc cộng thêm độ tin cậy khi đồng ý,
    /// không đủ để lật ngược một kết quả YOLO đã đạt ngưỡng tin cậy (phiếu YOLO tối thiểu ≈ 3 điểm với mã tỉnh hợp lệ).</summary>
    private const double TesseractVerifyCap = 2.5;

    private static bool YoloUncertain(Dictionary<string, PlateVote> votes, bool cut)
    {
        if (cut || votes.Count != 1) return true;
        return votes.Values.First().BestConfidence < YoloSureConfidence;
    }

    /// <summary>
    /// Tesseract làm bước KIỂM CHỨNG cho kết quả YOLO (không phải bộ đọc ngang hàng): Tesseract đọc nhiều biến thể ảnh rồi bỏ phiếu, điểm mỗi kết quả bị chặn ở
    /// <see cref="TesseractVerifyCap"/>. Cùng kết quả với YOLO → cộng điểm/độ tin cậy; khác → chỉ có tác dụng khi YOLO chưa phân thắng bại.
    /// </summary>
    private void AddTesseractVerification(Mat vehicleCrop, OpenCvSharp.Rect rect, Dictionary<string, PlateVote> votes, PlateColor color)
    {
        try
        {
            int pl = (int)(rect.Width * 0.10), py = (int)(rect.Height * 0.20);
            int x1 = Math.Max(0, rect.X - pl), y1 = Math.Max(0, rect.Y - py);
            int x2 = Math.Min(vehicleCrop.Width, rect.Right + pl), y2 = Math.Min(vehicleCrop.Height, rect.Bottom + py);
            if (x2 - x1 < 8 || y2 - y1 < 8) return;

            using var crop = new Mat(vehicleCrop, new OpenCvSharp.Rect(x1, y1, x2 - x1, y2 - y1));
            int padH = (int)(crop.Height * 0.12), padW = (int)(crop.Width * 0.12);
            using var padded = new Mat();
            Cv2.CopyMakeBorder(crop, padded, padH, padH, padW, padW, BorderTypes.Replicate);

            var tess = new Dictionary<string, PlateVote>();
            _reader!.CollectVotes(padded, tess, color);
            _reader.CollectVotes(crop, tess, color);

            foreach (var (key, tv) in tess)
            {
                double add = Math.Min(tv.Score, TesseractVerifyCap);
                if (votes.TryGetValue(key, out var v))
                {
                    v.Score += add;                                                  // dong y voi bo doc truoc
                    v.BestConfidence = Math.Max(v.BestConfidence, tv.BestConfidence);
                    v.Sources |= tv.Sources;
                    v.Reads += tv.Reads;
                    foreach (var r in tv.Raws) v.AddRawTagged(r);
                }
                else
                {
                    var nv = new PlateVote { Pretty = tv.Pretty, Score = add, BestConfidence = tv.BestConfidence * 0.5f, Sources = tv.Sources, Reads = tv.Reads, Color = tv.Color };   // Tesseract tu de xuat
                    foreach (var r in tv.Raws) nv.AddRawTagged(r);
                    votes[key] = nv;
                }
            }
        }
        catch (Exception ex)
        {
            if (!_warnedOnnx) { _warnedOnnx = true; _logger.LogWarning(ex, "Tesseract kiem chung loi — bo qua buoc nay."); }
        }
    }

    /// <summary>Cắt khung biển nới (trái, phải, dọc — theo tỉ lệ kích thước khung) rồi đọc bằng model dò ký tự YOLO. Trả về true nếu ký tự chạm mép ảnh cắt.</summary>
    private static bool ReadYoloLevel(YoloCharReader yolo, Mat vehicleCrop, OpenCvSharp.Rect rect, double fl, double fr, double fy, double weightScale, Dictionary<string, PlateVote> votes, PlateColor color)
    {
        int pl = (int)(rect.Width * fl), pr = (int)(rect.Width * fr), py = (int)(rect.Height * fy);
        int x1 = Math.Max(0, rect.X - pl), y1 = Math.Max(0, rect.Y - py);
        int x2 = Math.Min(vehicleCrop.Width, rect.Right + pr), y2 = Math.Min(vehicleCrop.Height, rect.Bottom + py);
        if (x2 - x1 < 8 || y2 - y1 < 8) return false;
        using var c = new Mat(vehicleCrop, new OpenCvSharp.Rect(x1, y1, x2 - x1, y2 - y1));
        return yolo.CollectVotes(c, votes, weightScale, x1, y1, color);
    }

    /// <summary>Cắt khung biển nới (trái, phải, dọc — theo tỉ lệ kích thước khung) rồi đọc bằng CNN. Trả về true nếu ký tự chạm mép ảnh cắt.</summary>
    private bool ReadCnnLevel(Mat vehicleCrop, OpenCvSharp.Rect rect, double fl, double fr, double fy, double weightScale, Dictionary<string, PlateVote> votes, PlateColor color)
    {
        int pl = (int)(rect.Width * fl), pr = (int)(rect.Width * fr), py = (int)(rect.Height * fy);
        int x1 = Math.Max(0, rect.X - pl), y1 = Math.Max(0, rect.Y - py);
        int x2 = Math.Min(vehicleCrop.Width, rect.Right + pr), y2 = Math.Min(vehicleCrop.Height, rect.Bottom + py);
        if (x2 - x1 < 8 || y2 - y1 < 8) return false;
        using var c = new Mat(vehicleCrop, new OpenCvSharp.Rect(x1, y1, x2 - x1, y2 - y1));
        return _cnnReader!.CollectVotes(c, votes, weightScale, x1, y1, color);
    }

    private void AddOnnxVote(Mat plateCrop, Dictionary<string, PlateVote> votes, PlateColor color)
    {
        try
        {
            var (raw, conf, lineLengths) = _onnxReader!.Read(plateCrop);
            if (raw is null) return;

            var pretty = PlateNormalizer.Apply(raw, lineLengths, color);      // ham cua nguoi dung: null / "" = khong nhan dien duoc
            if (pretty is null) return;

            var key = Regex.Replace(pretty.ToUpperInvariant(), "[^A-Z0-9]", "");
            if (!votes.TryGetValue(key, out var vote))
                votes[key] = vote = new PlateVote { Pretty = pretty, Color = color };

            // Doc tin cay cao tu model chuyen dung dang 2 lan doc Tesseract; ma tinh la thi giam trong so (xem PlateTextReader)
            double weight = (conf >= OnnxTrustedConfidence ? 2.0 : 1.0) * (1.0 + conf)
                            * PlateNormalizer.Weight(pretty, color);
            vote.Score += weight;
            vote.BestConfidence = Math.Max(vote.BestConfidence, conf);
            vote.Sources |= ReaderSource.Crnn;
            vote.Reads++;
            vote.AddRaw(ReaderSource.Crnn, raw);
        }
        catch (Exception ex)
        {
            if (!_warnedOnnx) { _warnedOnnx = true; _logger.LogWarning(ex, "Model OCR ONNX loi khi chay — dung Tesseract thay the."); }
        }
    }

    /// <summary>
    /// "Tìm tiếp trong khu vực" quanh khung biển đã tìm thấy: (1) cắt vùng rộng hơn khung (±35% ngang, ±50% dọc), phóng to nếu nhỏ rồi chạy LẠI các bộ dò
    /// với ngưỡng thấp — bộ dò thường định vị tốt hơn khi biển to trong ảnh; (2) thử thêm 4 khung lệch nhẹ (trái/phải 10% rộng, lên/xuống 12% cao).
    /// Khung quá giống khung đã thử bị bỏ. Khung từ bước (1) phải có hình dạng và cỡ gần với khung đã tìm (tỉ lệ 1–7,5; rộng 0,5–2 lần).
    /// </summary>
    private List<OpenCvSharp.Rect> BuildRefineRects(Mat vehicleCrop, OpenCvSharp.Rect found, List<OpenCvSharp.Rect> tried, Dictionary<OpenCvSharp.Rect, float> scores)
    {
        var result = new List<OpenCvSharp.Rect>();
        float parentScore = scores.TryGetValue(found, out var ps) ? ps : 0.35f;
        int w = vehicleCrop.Width, h = vehicleCrop.Height;

        bool Fresh(OpenCvSharp.Rect r) =>
            r.Width >= 8 && r.Height >= 4
            && tried.All(t => RectIou(r, t) < RefineDupIou)
            && result.All(t => RectIou(r, t) < RefineDupIou);

        try
        {
            int ax1 = Math.Max(0, found.X - (int)(found.Width * 0.35));
            int ay1 = Math.Max(0, found.Y - (int)(found.Height * 0.50));
            int ax2 = Math.Min(w, found.X + found.Width + (int)(found.Width * 0.35));
            int ay2 = Math.Min(h, found.Y + found.Height + (int)(found.Height * 0.50));
            var area = new OpenCvSharp.Rect(ax1, ay1, ax2 - ax1, ay2 - ay1);
            if (area.Width >= 16 && area.Height >= 16)
            {
                using var areaMat = new Mat(vehicleCrop, area);
                double scale = Math.Clamp(RefineTargetWidth / area.Width, 1.0, 3.0);
                using var zoomed = new Mat();
                if (scale > 1.01) Cv2.Resize(areaMat, zoomed, new OpenCvSharp.Size(), scale, scale, InterpolationFlags.Cubic);
                else areaMat.CopyTo(zoomed);

                var zoomScores = new Dictionary<OpenCvSharp.Rect, float>();
                foreach (var r in DetectPlateRects(zoomed, relaxed: true, applyShapeFilter: false, scores: zoomScores))
                {
                    int x1 = Math.Clamp(area.X + (int)Math.Round(r.X / scale), 0, w - 1);
                    int y1 = Math.Clamp(area.Y + (int)Math.Round(r.Y / scale), 0, h - 1);
                    int x2 = Math.Clamp(area.X + (int)Math.Round((r.X + r.Width) / scale), x1 + 1, w);
                    int y2 = Math.Clamp(area.Y + (int)Math.Round((r.Y + r.Height) / scale), y1 + 1, h);
                    var mapped = new OpenCvSharp.Rect(x1, y1, x2 - x1, y2 - y1);

                    double aspect = mapped.Width / (double)Math.Max(1, mapped.Height);
                    if (aspect < 1.0 || aspect > 7.5) continue;
                    if (mapped.Width < 0.5 * found.Width || mapped.Width > 2.0 * found.Width) continue;
                    if (Fresh(mapped)) { result.Add(mapped); scores[mapped] = zoomScores.TryGetValue(r, out var zs) ? zs : 0.35f; }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Do lai trong khu vuc quanh khung bien loi — bo qua buoc nay.");
        }

        int dx = (int)Math.Round(found.Width * 0.10), dy = (int)Math.Round(found.Height * 0.12);
        foreach (var (sx, sy) in new[] { (-dx, 0), (dx, 0), (0, -dy), (0, dy) })
        {
            var v = ClampRect(new OpenCvSharp.Rect(found.X + sx, found.Y + sy, found.Width, found.Height), w, h);
            if (Fresh(v)) { result.Add(v); scores[v] = parentScore; }   // khung lech nhe thua huong diem do cua khung goc
        }
        return result;
    }

    private static OpenCvSharp.Rect ClampRect(OpenCvSharp.Rect r, int w, int h)
    {
        int rw = Math.Min(r.Width, w), rh = Math.Min(r.Height, h);
        return new OpenCvSharp.Rect(Math.Clamp(r.X, 0, w - rw), Math.Clamp(r.Y, 0, h - rh), rw, rh);
    }

    private int AvailableReaderKinds() =>
        (_charDet is not null ? 1 : 0) + (_cnnReader is not null ? 1 : 0) + (_onnxReader is not null ? 1 : 0) + (_reader is not null ? 1 : 0);

    /// <summary>≥ 2 bộ đọc khác loại cùng ra chuỗi này. Nếu hệ thống chỉ có 1 loại bộ đọc thì không thể có 2 ý kiến độc lập → chấp nhận ≥ 2 lần đọc trùng.</summary>
    private bool HasIndependentAgreement(PlateVote v) =>
        System.Numerics.BitOperations.PopCount((uint)v.Sources) >= 2 || (AvailableReaderKinds() < 2 && v.Reads >= 2);

    /// <summary>
    /// Mỗi khung biển có bảng phiếu riêng và khung thắng được chọn theo điểm vị trí, nên khung lệch/cụt (chỉ thấy "019.70") có thể thắng khung đọc đủ ("30A-019.70").
    /// Nếu kết quả thắng KHÔNG khớp cú pháp nào, độ dài ≥ 4 và nằm trọn trong một biển HỢP CÚ PHÁP đã đọc ở khung nào đó (dù điểm thấp: một đoạn cụt không thể là biển hoàn chỉnh)
    /// thì chọn biển đầy đủ đó (điểm cao nhất nếu có nhiều). Trả về null nếu không cần / không có.
    /// </summary>
    private static (PlateVote Vote, Dictionary<string, PlateVote> Votes, OpenCvSharp.Rect Rect, PlateColorAnalyzer.Result Color)? PreferFullPlate(
        PlateVote best, List<(Dictionary<string, PlateVote> Votes, OpenCvSharp.Rect Rect, PlateColorAnalyzer.Result Color)> reads)
    {
        if (PlateSyntax.Classify(best.Pretty, null, best.Color).Kind != PlateKind.Unknown) return null;
        var bestKey = Regex.Replace(best.Pretty.ToUpperInvariant(), "[^A-Z0-9]", "");
        if (bestKey.Length < 4) return null;

        (PlateVote Vote, Dictionary<string, PlateVote> Votes, OpenCvSharp.Rect Rect, PlateColorAnalyzer.Result Color)? pick = null;
        foreach (var r in reads)
        {
            foreach (var kv in r.Votes)
            {
                var v = kv.Value;
                if (ReferenceEquals(v, best)) continue;
                if (kv.Key.Length <= bestKey.Length || !kv.Key.Contains(bestKey, StringComparison.Ordinal)) continue;
                if (PlateSyntax.Classify(v.Pretty, null, v.Color).Kind == PlateKind.Unknown) continue;
                if (pick is null || v.Score > pick.Value.Vote.Score) pick = (v, r.Votes, r.Rect, r.Color);
            }
        }
        return pick;
    }

    /// <summary>TẠM THỜI: in tóm tắt mỗi lần đọc ra Console (từng khung, ứng viên, điểm, bộ đọc, kết quả thắng) để tìm vì sao kết quả cuối khác các chuỗi đọc được. Xoá khi không cần.</summary>
    private static void TraceReads(List<(Dictionary<string, PlateVote> Votes, OpenCvSharp.Rect Rect, PlateColorAnalyzer.Result Color)> reads, PlateVote? before, PlateVote after)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[ANPR v3] {reads.Count} khung da doc");
            for (int i = 0; i < reads.Count; i++)
            {
                var (votes, rect, color) = reads[i];
                sb.AppendLine($"  khung#{i} rect={rect.X},{rect.Y},{rect.Width},{rect.Height} mau={color.Color} so_ung_vien={votes.Count}");
                foreach (var v in votes.Values.OrderByDescending(x => x.Score).Take(6))
                    sb.AppendLine($"    {v.Pretty,-14} {PlateSyntax.Classify(v.Pretty, null, v.Color).Kind,-9} diem={v.Score:0.00} nguon={v.Sources} lan={v.Reads}");
            }
            sb.AppendLine($"  => thang truoc quy tac: {before?.Pretty} ; cuoi: {after.Pretty}{(ReferenceEquals(before, after) ? "" : "  [DA DOI sang bien day du]")}");
            Console.WriteLine(sb.ToString());
        }
        catch { /* chi de chan doan */ }
    }

    /// <summary>
    /// Bộ dò đôi khi cắt MỘT biển thành 2 khung, mỗi khung chỉ phủ khoảng nửa biển (lệch nhau theo chiều ngang, chồng lên nhau 1 phần) nên mỗi khung chỉ đọc ra đoạn
    /// ("01970" hoặc "30A019"). Nếu khung chính và 1 khung ứng viên cùng hàng ngang (chồng dọc ≥ 60%), chồng/chạm nhau theo ngang và hợp lại rộng hơn khung lớn hơn
    /// 15–100% mà không quá cao (≤ 1,5 lần) thì thêm khung HỢP (bao cả hai) vào cuối nhóm để đọc thêm — khung chính vẫn được đọc và ưu tiên như cũ.
    /// </summary>
    private static void AddMergedPlateRect(List<BoundingBox> group)
    {
        if (group.Count < 2) return;
        var main = group[0];
        float mainW = main.X2 - main.X1, mainH = main.Y2 - main.Y1;
        BoundingBox? widest = null;
        float widestW = 0;
        for (int i = 1; i < group.Count; i++)
        {
            var o = group[i];
            float oW = o.X2 - o.X1, oH = o.Y2 - o.Y1;
            float minW = Math.Min(mainW, oW), minH = Math.Min(mainH, oH);
            if (minW <= 0 || minH <= 0) continue;

            float xOverlap = Math.Min(main.X2, o.X2) - Math.Max(main.X1, o.X1);
            float yOverlap = Math.Min(main.Y2, o.Y2) - Math.Max(main.Y1, o.Y1);
            if (xOverlap < -0.1f * minW || yOverlap < 0.6f * minH) continue;

            float ux1 = Math.Min(main.X1, o.X1), uy1 = Math.Min(main.Y1, o.Y1);
            float ux2 = Math.Max(main.X2, o.X2), uy2 = Math.Max(main.Y2, o.Y2);
            float uw = ux2 - ux1, uh = uy2 - uy1;
            float maxW = Math.Max(mainW, oW), maxH = Math.Max(mainH, oH);
            if (uw < 1.15f * maxW || uw > 2.0f * maxW || uh > 1.5f * maxH) continue;

            if (uw > widestW)
            {
                widestW = uw;
                widest = new BoundingBox { X1 = ux1, Y1 = uy1, X2 = ux2, Y2 = uy2, Score = Math.Max(main.Score, o.Score) };
            }
        }
        if (widest is not null) group.Add(widest);
    }

    private static bool HasStructuredVote(Dictionary<string, PlateVote> votes) =>
        votes.Values.Any(v => PlateSyntax.Classify(v.Pretty, null, v.Color).Kind != PlateKind.Unknown);

    /// <summary>Kết quả đứng đầu mới chỉ có 1 loại bộ đọc → chạy Tesseract làm ý kiến thứ 2 (điểm bị chặn, xem <see cref="TesseractVerifyCap"/>).</summary>
    private void EnsureSecondOpinion(Mat vehicleCrop, OpenCvSharp.Rect rect, Dictionary<string, PlateVote> votes, Func<bool> outOfTime, PlateColor color)
    {
        if (!_requireSecondOpinion || _reader is null || votes.Count == 0 || outOfTime()) return;
        if (votes.Values.Any(v => (v.Sources & ReaderSource.Tesseract) != 0)) return;      // Tesseract đã đọc rồi
        var top = PlateVoteResolver.Best(votes);
        if (top is null || System.Numerics.BitOperations.PopCount((uint)top.Sources) >= 2) return;
        AddTesseractVerification(vehicleCrop, rect, votes, color);
    }

    /// <summary>Ghi 1 dòng JSON cho mỗi lần đọc: các ứng viên (kèm loại cấu trúc, bộ đọc, chuỗi thô của từng bộ đọc) và kết quả được chọn.</summary>
    private void LogReads(Dictionary<string, PlateVote> votes, PlateVote? chosen, OpenCvSharp.Rect rect, string stage = "read", float detScore = -1f, PlateColorAnalyzer.Result color = default)
    {
        if (_readLogDir is null) return;
        try
        {
            Directory.CreateDirectory(_readLogDir);
            var file = Path.Combine(_readLogDir, $"reads-{DateTime.Now:yyyyMMdd}.jsonl");
            if (File.Exists(file) && new FileInfo(file).Length > 50_000_000) return;

            var line = System.Text.Json.JsonSerializer.Serialize(new
            {
                time = DateTime.Now.ToString("o"),
                tag = LogTag.Value,
                stage,
                color = color.Color.ToString(),
                colorShare = Math.Round(color.Share, 2),
                det = detScore < 0 ? (double?)null : Math.Round(detScore, 2),
                rect = $"{rect.X},{rect.Y},{rect.Width},{rect.Height}",
                chosen = chosen?.Pretty,
                candidates = votes.Values.OrderByDescending(v => v.Score).Select(v => new
                {
                    plate = v.Pretty,
                    kind = PlateSyntax.Classify(v.Pretty, null, v.Color).Kind.ToString(),
                    score = Math.Round(v.Score, 2),
                    conf = Math.Round(v.BestConfidence, 2),
                    sources = v.Sources.ToString(),
                    reads = v.Reads,
                    gapFilled = v.GapFilled,
                    chars = v.Chars?.Select(c => $"{c.Value}:{c.Score:0.00}" + (c.Dropped is null ? "" : "(bo)")),
                    raws = v.Raws
                })
            });
            File.AppendAllText(file, line + "\n");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Khong ghi duoc log doc bien.");
        }
    }

    /// <summary>Luu anh xe (kem khung bien da chon) khi bat Anpr:DebugDir — mac dinh chi luu khi KHONG doc duoc hoac do tin cay thap.</summary>
    private void SaveDebug(Mat vehicleCrop, OpenCvSharp.Rect? rect, string? plate, float conf)
    {
        if (_debugDir is null) return;
        if (!_debugAll && plate is not null && conf >= 0.6f) return;

        try
        {
            using var img = vehicleCrop.Clone();
            if (rect is { } r) Cv2.Rectangle(img, r, new Scalar(0, 255, 0), 2);

            var files = Directory.GetFiles(_debugDir, "*.jpg");
            if (files.Length > 600) // giu thu muc gon: xoa 200 anh cu nhat
                foreach (var old in files.OrderBy(File.GetCreationTimeUtc).Take(200)) File.Delete(old);

            var label = plate is null ? "khong-doc-duoc" : Regex.Replace(plate, "[^A-Z0-9]", "") + $"_{conf:0.00}";
            Cv2.ImWrite(Path.Combine(_debugDir, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{label}.jpg"), img);
            _logger.LogInformation("ANPR debug: {Plate} (tin cay {Conf:0.00}) khung={Rect}", plate ?? "KHONG DOC DUOC", conf, rect);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Khong luu duoc anh debug ANPR.");
        }
    }

    public void Dispose()
    {
        _engine?.Dispose();
        _plateDetector?.Dispose();
        _customPlateDetector?.Dispose();
        _onnxReader?.Dispose();
        _darknetDetector?.Dispose();
        _cnnReader?.Dispose();
        _charDet?.Dispose();
    }
}
