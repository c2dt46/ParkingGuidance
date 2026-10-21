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
public class TesseractPlateRecognitionService : IPlateRecognitionService, ICharModelReloader, IPlateDetectorModel, IDisposable
{
    private readonly TesseractEngine? _engine;
    private readonly PlateTextReader? _reader;
    private readonly OnnxPlateDetector? _plateDetector;
    private OnnxPlateDetector? _customPlateDetector;           // bo do bien da train them (tab Train bien so) — uu tien truoc bo do mac dinh; doi duoc luc dang chay: xem Reload(out string)
    private readonly string _customPlatePath;                  // {DataDirectory}/plate-train/active/plate.onnx
    private readonly string? _baseOnnxPath;                    // plate_detector.onnx goc (neu co) — moc so sanh khi train
    private string? _plateLoadError;
    private readonly OnnxPlateReader? _onnxReader;
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
    private readonly bool _relaxedSearch;                       // lan do noi long khi lan thuong khong ra bien doc duoc (Anpr:RelaxedSearch, mac dinh bat)
    // TesseractEngine KHONG an toan da luong: API demo va dich vu camera nen co the goi dong thoi
    private readonly object _ocrLock = new();
    private readonly ILogger<TesseractPlateRecognitionService> _logger;
    private readonly ISettingsService _settings;                // nguong tin cay / chat luong anh: doi trong "Cau hinh chung" co hieu luc ngay

    private float PlateBoxConf => Math.Clamp(_settings.Current.PlateBoxConfidence ?? 0.4f, 0.05f, 0.95f);

    /// <summary>Điểm bỏ phiếu đủ cao (≈ 2 lần đọc trùng nhau) để dừng sớm cho nhanh.</summary>
    private const double EarlyStopScore = 3.2;
    private const int MaxCandidates = 5;

    public bool IsReady => _engine is not null || _onnxReader is not null || _cnnReader is not null || _charDet is not null;

    private bool HasCharReader => _cnnReader is not null || _charDet is not null;

    /// <summary>Lần đọc ONNX có độ tin cậy từ mức này là đủ tin để khỏi chạy thêm Tesseract.</summary>
    private const float OnnxTrustedConfidence = 0.8f;

    public TesseractPlateRecognitionService(
        IConfiguration config, IPersistenceService persistence, ILogger<TesseractPlateRecognitionService> logger, ISettingsService settings)
    {
        _logger = logger;
        _settings = settings;
        _customPlatePath = Path.Combine(persistence.DataDirectory, "plate-train", "active", "plate.onnx");

        // Mac dinh chi nhan bien 5 so (NNL-NNN.NN). Bien o to doi cu 4 so rat hiem va de bi nhan nham
        // khi OCR roi mat 1 chu so, nen tat mac dinh; bat lai bang Anpr:AllowLegacy4DigitPlates = true.
        VietnamPlateFormatter.AllowLegacyFourDigit = config.GetValue("Anpr:AllowLegacy4DigitPlates", false);

        _relaxedSearch = config.GetValue("Anpr:RelaxedSearch", true);
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
        if (ocrPath is not null) _onnxReader = OnnxPlateReader.TryCreate(ocrPath, config, _logger);

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

        lock (_ocrLock)
        {
            try
            {
                var custom = _customPlateDetector;
                if (custom is not null)
                {
                    foreach (var (r, sc) in custom.Detect(image, confThreshold, 6, expand: 0f)) result.Add((r.X, r.Y, r.Right, r.Bottom, sc));
                    if (result.Count > 0) return result;
                }
                if (_darknetDetector is not null)
                {
                    foreach (var (r, sc) in _darknetDetector.Detect(image, confThreshold, 6)) result.Add((r.X, r.Y, r.Right, r.Bottom, sc));
                    if (result.Count > 0) return result;
                }
                if (_plateDetector is not null)
                    foreach (var (r, sc) in _plateDetector.Detect(image, confThreshold, 6, expand: 0f)) result.Add((r.X, r.Y, r.Right, r.Bottom, sc));
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
            foreach (var r in DetectPlateRects(frame, relaxed: false, quickOnly: true))
                result.Add(new BoundingBox { X1 = r.X, Y1 = r.Y, X2 = r.X + r.Width, Y2 = r.Y + r.Height });
        }
        return result;
    }

    public (string? PlateNumber, float Confidence, BoundingBox? PlateBox) ReadPlateAt(Mat frame, BoundingBox plateBox)
    {
        if (!IsReady || frame.Empty()) return (null, 0f, null);

        int x1 = Math.Clamp((int)Math.Floor(plateBox.X1), 0, Math.Max(0, frame.Width - 1));
        int y1 = Math.Clamp((int)Math.Floor(plateBox.Y1), 0, Math.Max(0, frame.Height - 1));
        int x2 = Math.Clamp((int)Math.Ceiling(plateBox.X2), x1 + 1, frame.Width);
        int y2 = Math.Clamp((int)Math.Ceiling(plateBox.Y2), y1 + 1, frame.Height);
        var rect = new OpenCvSharp.Rect(x1, y1, x2 - x1, y2 - y1);
        if (rect.Width < 8 || rect.Height < 8) return (null, 0f, plateBox);

        lock (_ocrLock)
        {
            ApplyReaderSettings();
            using var crop = new Mat(frame, rect);
            var votes = new Dictionary<string, PlateVote>();

            // Cung cach doc nhu trong RecognizeLocked, nhung CHI tren khung bien da co (khong do lai, khong tim ung vien khac)
            if (HasCharReader) AddCnnVotes(frame, rect, votes);
            if (_onnxReader is not null) AddOnnxVote(crop, votes);

            bool trusted = votes.Values.Any(v => v.BestConfidence >= OnnxTrustedConfidence);
            if (!trusted && _reader is not null && !(HasCharReader && votes.Count > 0))
            {
                int padH = (int)(crop.Height * 0.12), padW = (int)(crop.Width * 0.12);
                using var padded = new Mat();
                Cv2.CopyMakeBorder(crop, padded, padH, padH, padW, padW, BorderTypes.Replicate);
                _reader.CollectVotes(padded, votes);
                _reader.CollectVotes(crop, votes);
            }

            var best = votes.Values.OrderByDescending(v => v.Score).FirstOrDefault();
            if (best is null)
            {
                SaveDebug(frame, rect, null, 0f);
                return (null, 0f, plateBox);
            }

            var refined = RefineRectFromChars(best, rect, frame.Width, frame.Height);
            var box = new BoundingBox { X1 = refined.X, Y1 = refined.Y, X2 = refined.X + refined.Width, Y2 = refined.Y + refined.Height };
            SaveDebug(frame, refined, best.Pretty, best.BestConfidence);
            return (PlateNormalizer.Apply(best.Pretty), best.BestConfidence, box);
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

        // Thu lan luot cac ung vien vung bien (vung dau tien tim duoc KHONG luon la bien so, vd luoi tan nhiet) va chon ket qua
        // khop dinh dang bien so VN co diem bo phieu cao nhat; dung som khi diem du cao (≈ 2 lan doc trung nhau).
        bool TryRects(IEnumerable<OpenCvSharp.Rect> rects)
        {
            int tried0 = 0;
            foreach (var rect in rects)
            {
                if (tried0++ > 0 && OutOfTime()) break;           // luon thu it nhat 1 ung vien
                using var crop = new Mat(vehicleCrop, rect);
                var votes = new Dictionary<string, PlateVote>();

                // 0) CNN ky tu (neu co): doc o 3 muc le khac nhau cua khung bien (detector hay cat sat/thieu hang tren)
                if (HasCharReader) AddCnnVotes(vehicleCrop, rect, votes);

                // 1) Model OCR ONNX (neu co)
                if (_onnxReader is not null) AddOnnxVote(crop, votes);

                // 2) Tesseract: bo qua neu model ONNX da doc duoc voi do tin cay cao
                bool trusted = votes.Values.Any(v => v.BestConfidence >= OnnxTrustedConfidence);
                // Co CNN: chi dung Tesseract khi CNN khong cho duoc phieu hop le nao
                if (!trusted && _reader is not null && !(HasCharReader && votes.Count > 0))
                {
                    // Doc ca ban do them le 12% (thuat toan nhi phan hoa phu thuoc vao vien quanh bien) va ban cat sat
                    int padH = (int)(crop.Height * 0.12), padW = (int)(crop.Width * 0.12);
                    using var padded = new Mat();
                    Cv2.CopyMakeBorder(crop, padded, padH, padH, padW, padW, BorderTypes.Replicate);
                    _reader.CollectVotes(padded, votes);
                    _reader.CollectVotes(crop, votes);
                }

                var top = votes.Values.OrderByDescending(v => v.Score).FirstOrDefault();
                if (top is not null && (best is null || top.Score > best.Score))
                {
                    best = top;
                    bestRect = rect;
                }

                if (best is not null && best.Score >= EarlyStopScore) return true;
            }
            return best is not null;
        }

        // Vung bien kha di nhat do bo do tim duoc nhung chua doc duoc ky tu → tra ve de nguoi dung thay/chinh o tab Train
        OpenCvSharp.Rect? unread = null;
        var tried = new List<OpenCvSharp.Rect>();

        var detected = DetectPlateRects(vehicleCrop, relaxed: false);
        if (detected.Count > 0)
        {
            unread = detected[0];
            tried.AddRange(detected);
            TryRects(detected);
        }

        // Do nhanh tren ca khung hinh (khong thay xe): co bo do bien da huan luyen ma khong thay bien nao → anh khong co bien, tra ve ngay
        if (quick && hasLearnedDetector && detected.Count == 0)
            return (null, 0f, null);

        // Lan do noi long (ha nguong, o chia nho, can bang sang): chi khi lan thuong chua cho ra bien doc duoc (khong dung o che do nhanh)
        if (best is null && _relaxedSearch && !quick && !OutOfTime())
        {
            var more = DetectPlateRects(vehicleCrop, relaxed: true).Where(r => tried.All(t => RectIou(r, t) < 0.5)).ToList();
            if (more.Count > 0)
            {
                unread ??= more[0];
                tried.AddRange(more);
                TryRects(more);
            }
        }

        // Du phong bang xu ly anh. O che do nhanh chi dung khi KHONG co bo do bien da huan luyen (khi do day la cach duy nhat), va chi vai ung vien.
        if (best is null && (!quick || !hasLearnedDetector) && !OutOfTime())
            TryRects(PlateLocator.FindPlateCandidates(vehicleCrop, quick ? 2 : MaxCandidates));

        if (best is null)
        {
            SaveDebug(vehicleCrop, unread, null, 0f);
            if (unread is { } u)
                return (null, 0f, new BoundingBox { X1 = u.X, Y1 = u.Y, X2 = u.X + u.Width, Y2 = u.Y + u.Height });
            return (null, 0f, null);
        }

        // Khung bao biển tính lại từ DẢI KÝ TỰ đã đọc được (khung cua bo do, nhat la model train it anh, hay bi cut/lech).
        // Khung nay cung la khung goi y cho tab Train nen khung dung se khong "day" nhan sai vao du lieu train.
        bestRect = RefineRectFromChars(best, bestRect, vehicleCrop.Width, vehicleCrop.Height);
        var box = new BoundingBox
        {
            X1 = bestRect.X,
            Y1 = bestRect.Y,
            X2 = bestRect.X + bestRect.Width,
            Y2 = bestRect.Y + bestRect.Height
        };
        SaveDebug(vehicleCrop, bestRect, best.Pretty, best.BestConfidence);
        return (PlateNormalizer.Apply(best.Pretty), best.BestConfidence, box);
    }

    /// <summary>
    /// Biển rộng hơn dải ký tự khoảng 10% mỗi bên (ngang) và ~28% chiều cao ký tự (dọc) — tỉ lệ đo trên ảnh thực. Chỉ dùng khi có dải ký tự
    /// hợp lý (nằm gần khung bộ dò và dải không quá lớn/nhỏ so với khung) để không "nhảy" sang vùng khác; ngược lại giữ khung bộ dò.
    /// </summary>
    private static OpenCvSharp.Rect RefineRectFromChars(PlateVote vote, OpenCvSharp.Rect detected, int imgW, int imgH)
    {
        if (!vote.HasSpan) return detected;
        double w = vote.SpanX2 - vote.SpanX1, h = vote.SpanY2 - vote.SpanY1;
        if (w < 16 || h < 6) return detected;

        double cxs = (vote.SpanX1 + vote.SpanX2) / 2, cys = (vote.SpanY1 + vote.SpanY2) / 2;
        // dai ky tu phai nam gan khung bo do (cho phep khung cut: tam dai ky tu lech toi da 1 chieu rong khung) va co kich thuoc cung bac
        bool near = Math.Abs(cxs - (detected.X + detected.Width / 2.0)) <= detected.Width
                    && Math.Abs(cys - (detected.Y + detected.Height / 2.0)) <= detected.Height * 1.5;
        bool sane = w <= detected.Width * 3.0 && h <= detected.Height * 3.0;
        if (!near || !sane) return detected;

        double padX = Math.Max(3, 0.10 * w), padY = Math.Max(2, 0.28 * h);
        int x1 = (int)Math.Max(0, Math.Floor(vote.SpanX1 - padX));
        int y1 = (int)Math.Max(0, Math.Floor(vote.SpanY1 - padY));
        int x2 = (int)Math.Min(imgW, Math.Ceiling(vote.SpanX2 + padX));
        int y2 = (int)Math.Min(imgH, Math.Ceiling(vote.SpanY2 + padY));
        if (x2 - x1 < 24 || y2 - y1 < 8) return detected;
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

    private List<OpenCvSharp.Rect> DetectPlateRects(Mat vehicleCrop, bool relaxed, bool quickOnly = false)
    {
        float boxConf = PlateBoxConf;
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
        void AddRect(OpenCvSharp.Rect r)
        {
            if (list.All(x => RectIou(x, r) <= 0.85)) list.Add(r);
        }

        try
        {
            // Duong nhanh tren ca khung hinh (quickOnly) can khung OM SAT bien (khong noi vien): phan doc ky tu tu noi vien khi cat
            if (custom is not null && useYolo)
                foreach (var (rect, _) in custom.Detect(vehicleCrop, relaxed ? 0.15f : boxConf, relaxed ? 4 : 3, expand: quickOnly ? 0f : 0.06f)) AddRect(rect);
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
                foreach (var (rect, _) in found) AddRect(rect);
            }
            if (!relaxed && list.Count == 0 && _plateDetector is not null && useYolo)
                foreach (var (rect, _) in _plateDetector.Detect(vehicleCrop, boxConf)) AddRect(rect);
        }
        catch (Exception ex)
        {
            if (!_warnedOnnx) { _warnedOnnx = true; _logger.LogWarning(ex, "Model do bien so ONNX loi khi chay — dung PlateLocator thay the."); }
        }
        return list.Take(relaxed ? 5 : 4).ToList();
    }

    /// <summary>
    /// Đọc ký tự bằng CNN trên khung biển với nhiều mức nới viền. Detector (nhất là model train ít ảnh) hay cho khung CỤT ở đầu/cuối biển,
    /// mà mức nới cũ chỉ 0–12% ngang nên không bù nổi. Nên: 3 mức nới nhỏ như cũ → nếu chưa có phiếu hợp lệ nào, hoặc khung nghi bị cụt
    /// (biển 1 dòng mà tỉ lệ rộng/cao &lt; 4, hoặc ký tự chạm mép ảnh cắt) thì thử thêm các mức nới ngang mạnh (một phía và hai phía).
    /// Phiếu của các mức nới mạnh có trọng số thấp hơn (dễ lẫn nền xe) — chỉ thắng khi các mức thường không đọc được/không đồng thuận.
    /// </summary>
    private void AddCnnVotes(Mat vehicleCrop, OpenCvSharp.Rect rect, Dictionary<string, PlateVote> votes)
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
                bool cut = ReadYoloLevel(yolo, vehicleCrop, rect, 0.10, 0.10, 0.20, 1.0, votes);
                if (votes.Count == 0 || suspectCut || cut)
                    ReadYoloLevel(yolo, vehicleCrop, rect, 0.30, 0.30, 0.25, votes.Count == 0 ? 1.0 : 0.7, votes);
                if (votes.Count > 0)
                {
                    // Ket hop: YOLO chua chac (do tin cay thap / cac lan doc bat dong / khung nghi cut) -> nho Tesseract kiem chung (tuy chon)
                    if ((_settings.Current.CharCombine ?? 1) != 0 && _reader is not null && YoloUncertain(votes, suspectCut || cut))
                        AddTesseractVerification(vehicleCrop, rect, votes);
                    return;
                }
            }
            if (_cnnReader is null) return;

            // (le trai, le phai, le doc) theo ty le khung: sat, vua, rong hon; khung do co the cat mat hang chu tren
            foreach (var (fl, fr, fy) in new[] { (0.08, 0.08, 0.08), (0.12, 0.12, 0.25) })
                edgeCut |= ReadCnnLevel(vehicleCrop, rect, fl, fr, fy, 1.0, votes);
            ReadCnnLevel(vehicleCrop, rect, 0.0, 0.0, 0.0, 1.0, votes);   // cat sat (cham mep la binh thuong nen khong tinh vao edgeCut)

            if (votes.Count == 0 || suspectCut || edgeCut)
            {
                double ws = votes.Count == 0 ? 1.0 : 0.6;
                foreach (var (fl, fr, fy) in new[] { (0.0, 0.4, 0.2), (0.4, 0.0, 0.2), (0.4, 0.4, 0.25) })
                    ReadCnnLevel(vehicleCrop, rect, fl, fr, fy, ws, votes);
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
    private void AddTesseractVerification(Mat vehicleCrop, OpenCvSharp.Rect rect, Dictionary<string, PlateVote> votes)
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
            _reader!.CollectVotes(padded, tess);
            _reader.CollectVotes(crop, tess);

            foreach (var (key, tv) in tess)
            {
                double add = Math.Min(tv.Score, TesseractVerifyCap);
                if (votes.TryGetValue(key, out var v))
                {
                    v.Score += add;                                                  // dong y voi YOLO
                    v.BestConfidence = Math.Max(v.BestConfidence, tv.BestConfidence);
                }
                else
                {
                    votes[key] = new PlateVote { Pretty = tv.Pretty, Score = add, BestConfidence = tv.BestConfidence * 0.5f };   // Tesseract tu de xuat
                }
            }
        }
        catch (Exception ex)
        {
            if (!_warnedOnnx) { _warnedOnnx = true; _logger.LogWarning(ex, "Tesseract kiem chung loi — bo qua buoc nay."); }
        }
    }

    /// <summary>Cắt khung biển nới (trái, phải, dọc — theo tỉ lệ kích thước khung) rồi đọc bằng model dò ký tự YOLO. Trả về true nếu ký tự chạm mép ảnh cắt.</summary>
    private static bool ReadYoloLevel(YoloCharReader yolo, Mat vehicleCrop, OpenCvSharp.Rect rect, double fl, double fr, double fy, double weightScale, Dictionary<string, PlateVote> votes)
    {
        int pl = (int)(rect.Width * fl), pr = (int)(rect.Width * fr), py = (int)(rect.Height * fy);
        int x1 = Math.Max(0, rect.X - pl), y1 = Math.Max(0, rect.Y - py);
        int x2 = Math.Min(vehicleCrop.Width, rect.Right + pr), y2 = Math.Min(vehicleCrop.Height, rect.Bottom + py);
        if (x2 - x1 < 8 || y2 - y1 < 8) return false;
        using var c = new Mat(vehicleCrop, new OpenCvSharp.Rect(x1, y1, x2 - x1, y2 - y1));
        return yolo.CollectVotes(c, votes, weightScale, x1, y1);
    }

    /// <summary>Cắt khung biển nới (trái, phải, dọc — theo tỉ lệ kích thước khung) rồi đọc bằng CNN. Trả về true nếu ký tự chạm mép ảnh cắt.</summary>
    private bool ReadCnnLevel(Mat vehicleCrop, OpenCvSharp.Rect rect, double fl, double fr, double fy, double weightScale, Dictionary<string, PlateVote> votes)
    {
        int pl = (int)(rect.Width * fl), pr = (int)(rect.Width * fr), py = (int)(rect.Height * fy);
        int x1 = Math.Max(0, rect.X - pl), y1 = Math.Max(0, rect.Y - py);
        int x2 = Math.Min(vehicleCrop.Width, rect.Right + pr), y2 = Math.Min(vehicleCrop.Height, rect.Bottom + py);
        if (x2 - x1 < 8 || y2 - y1 < 8) return false;
        using var c = new Mat(vehicleCrop, new OpenCvSharp.Rect(x1, y1, x2 - x1, y2 - y1));
        return _cnnReader!.CollectVotes(c, votes, weightScale, x1, y1);
    }

    private void AddOnnxVote(Mat plateCrop, Dictionary<string, PlateVote> votes)
    {
        try
        {
            var (raw, conf, lineLengths) = _onnxReader!.Read(plateCrop);
            if (raw is null) return;

            var (pretty, isValid) = VietnamPlateFormatter.Normalize(raw, lineLengths);
            if (!isValid) return;

            var key = Regex.Replace(pretty, "[^A-Z0-9]", "");
            if (!votes.TryGetValue(key, out var vote))
                votes[key] = vote = new PlateVote { Pretty = pretty };

            // Doc tin cay cao tu model chuyen dung dang 2 lan doc Tesseract; ma tinh la thi giam trong so (xem PlateTextReader)
            double weight = (conf >= OnnxTrustedConfidence ? 2.0 : 1.0) * (1.0 + conf)
                            * (VietnamPlateFormatter.IsKnownProvinceCode(pretty) ? 1.0 : 0.4);
            vote.Score += weight;
            vote.BestConfidence = Math.Max(vote.BestConfidence, conf);
        }
        catch (Exception ex)
        {
            if (!_warnedOnnx) { _warnedOnnx = true; _logger.LogWarning(ex, "Model OCR ONNX loi khi chay — dung Tesseract thay the."); }
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
