using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Persistence;
using ParkingGuidance.Web.Services.Settings;

namespace ParkingGuidance.Web.Services.Detection;

/// <summary>
/// Nhận diện phương tiện bằng model YOLO (YOLO11 hoặc YOLOv8, định dạng ONNX) chạy qua ONNX Runtime.
/// YOLO11 và YOLOv8 xuất ONNX cùng một dạng đầu ra [1, 4 + số lớp, số khung] (4 số đầu = cx, cy, w, h; không có "objectness"),
/// nên dùng chung 1 bộ giải mã. Đầu ra dạng chuyển vị [1, số khung, 4 + số lớp] cũng được nhận biết tự động.
/// • Lớp xe lấy từ tên lớp ghi trong model (metadata "names" do Ultralytics ghi khi export): car → ô tô, bus, truck; motorcycle chỉ khi
///   bật <c>Detection:IncludeMotorcycle</c>. Model không có tên lớp / 80 lớp → dùng bộ COCO: car(2), motorcycle(3), bus(5), truck(7).
/// • Kích thước ảnh vào lấy từ chính model (mặc định 640). Tiền xử lý dùng các phép toán OpenCV theo khối (nhanh hơn nhiều so với lặp từng điểm ảnh).
/// • Thứ tự tìm model: model đã train thêm (data/vehicle-train/active/vehicle.onnx) → <c>Detection:ModelPath</c> (mặc định models/yolo11n.onnx)
///   → models/yolov8n.onnx (dự phòng cho bản cài cũ). Có thể nạp lại khi đang chạy (<see cref="Reload"/>).
/// </summary>
public class YoloVehicleDetectionService : IVehicleDetectionService, IDisposable
{
    private const int DefaultInputSize = 640;
    private static readonly Regex NameEntry = new(@"(\d+)\s*:\s*(?:'([^']*)'|""([^""]*)"")", RegexOptions.Compiled);

    /// <summary>Bộ lớp COCO mặc định khi model không ghi tên lớp.</summary>
    private static readonly Dictionary<int, VehicleType> CocoClasses = new()
    {
        { 2, VehicleType.Car }, { 3, VehicleType.Motorcycle }, { 5, VehicleType.Bus }, { 7, VehicleType.Truck },
    };

    private sealed class ModelState : IDisposable
    {
        public required InferenceSession Session { get; init; }
        public required string InputName { get; init; }
        public required int InputSize { get; init; }
        public required Dictionary<int, VehicleType> Classes { get; init; }   // id lop → loai xe (gom ca xe may)
        public required string Path { get; init; }
        public required bool IsCustom { get; init; }
        public required string Info { get; init; }
        public void Dispose() => Session.Dispose();
    }

    private readonly ILogger<YoloVehicleDetectionService> _logger;
    private readonly ISettingsService _settings;
    private readonly IConfiguration _config;
    private readonly int _threads;
    private readonly object _runLock = new();          // Detect va Reload khong chay cung luc (huy model cu an toan)
    private ModelState? _state;

    public bool IsReady => _state is not null;
    public string ModelPath { get; }
    public string CustomModelPath { get; }
    public string? ResolvedModelPath => _state?.Path;
    public IReadOnlyList<string> SearchedPaths { get; private set; } = Array.Empty<string>();
    public string? LoadError { get; private set; }
    public string ModelInfo => _state?.Info ?? "chưa nạp model";
    public bool UsingCustomModel => _state?.IsCustom ?? false;

    public YoloVehicleDetectionService(
        IConfiguration config, ISettingsService settings, IPersistenceService persistence,
        ILogger<YoloVehicleDetectionService> logger)
    {
        _logger = logger;
        _settings = settings;
        _config = config;
        _threads = Math.Max(0, config.GetValue("Detection:Threads", 0));
        ModelPath = config["Detection:ModelPath"] ?? "models/yolo11n.onnx";
        CustomModelPath = Path.Combine(persistence.DataDirectory, "vehicle-train", "active", "vehicle.onnx");

        Reload(out _);
    }

    /// <summary>Các vị trí có thể chứa model gốc (thư mục làm việc có thể khác nhau tuỳ cách chạy ứng dụng).</summary>
    private static IEnumerable<string> BaseCandidates(string configured)
    {
        var fileName = Path.GetFileName(configured);
        return new[]
        {
            configured,
            Path.Combine(AppContext.BaseDirectory, configured),
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(Directory.GetCurrentDirectory(), fileName),
        }.Select(Path.GetFullPath);
    }

    public bool Reload(out string message)
    {
        // custom -> model goc (cau hinh) -> yolov8n du phong
        var tried = new List<(string path, bool custom)> { (CustomModelPath, true) };
        tried.AddRange(BaseCandidates(ModelPath).Select(p => (p, false)));
        if (!Path.GetFileName(ModelPath).Equals("yolov8n.onnx", StringComparison.OrdinalIgnoreCase))
            tried.AddRange(BaseCandidates("models/yolov8n.onnx").Select(p => (p, false)));
        tried = tried.DistinctBy(t => t.path).ToList();
        SearchedPaths = tried.Select(t => t.path).ToList();

        string? lastError = null;
        foreach (var (path, custom) in tried)
        {
            if (!File.Exists(path)) continue;
            var fresh = TryLoad(path, custom, out var error);
            if (fresh is null) { lastError = $"{path}: {error}"; continue; }

            ModelState? old;
            lock (_runLock) { old = _state; _state = fresh; }
            old?.Dispose();
            LoadError = null;
            message = $"Đã nạp model nhận diện xe: {fresh.Info}.";
            _logger.LogInformation("Da tai model nhan dien xe: {Info} ({Path}); nguong tin cay mac dinh {Conf}",
                fresh.Info, path, _settings.Current.ConfidenceThreshold);
            return true;
        }

        if (_state is not null)
        {
            message = "Không nạp được model mới" + (lastError is null ? "" : $" ({lastError})") + " — giữ nguyên model đang chạy.";
            return false;
        }

        LoadError = lastError ?? "Khong tim thay file model o bat ky vi tri nao da thu.";
        message = LoadError;
        _logger.LogWarning(
            "*** KHONG NAP DUOC MODEL AI *** Day la nguyen nhan pho bien nhat khien tinh nang nhan dien luon tra ve 'khong co xe'. {Err}\n" +
            "Da thu cac vi tri sau:\n - {Paths}\nDat file yolo11n.onnx (xuat tu yolo11n.pt, xem README.md) vao 1 trong cac vi tri tren, " +
            "hoac dat dung theo cau hinh 'Detection:ModelPath' trong appsettings.json.",
            lastError, string.Join("\n - ", SearchedPaths));
        return false;
    }

    private ModelState? TryLoad(string path, bool custom, out string? error)
    {
        error = null;
        InferenceSession? session = null;
        try
        {
            var opts = new Microsoft.ML.OnnxRuntime.SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            if (_threads > 0) opts.IntraOpNumThreads = _threads;
            session = new InferenceSession(path, opts);

            var input = session.InputMetadata.First();
            var dims = input.Value.Dimensions;                            // [1, 3, H, W] (co the dong: <= 0)
            int size = dims.Length == 4 && dims[2] > 0 && dims[3] > 0 ? Math.Max(dims[2], dims[3]) : DefaultInputSize;

            var output = session.OutputMetadata.First().Value.Dimensions; // [1, 4+nc, N] hoac [1, N, 4+nc]
            int nc = output.Length == 3 ? Math.Min(output[1], output[2]) - 4 : -1;

            var classes = ClassesFromMetadata(session, nc);
            if (classes.Count == 0) { error = "model khong co lop xe nao (car/bus/truck)."; session.Dispose(); return null; }

            var info = $"{Path.GetFileName(path)} · {size}×{size}" + (nc > 0 ? $" · {nc} lớp" : "") + (custom ? " · đã train thêm" : "");
            return new ModelState
            {
                Session = session, InputName = input.Key, InputSize = size, Classes = classes,
                Path = path, IsCustom = custom, Info = info
            };
        }
        catch (Exception ex)
        {
            error = ex.Message;
            session?.Dispose();
            _logger.LogError(ex, "Tim thay file model tai '{Path}' nhung khong the nap (file loi, sai dinh dang ONNX hoac opset).", path);
            return null;
        }
    }

    /// <summary>Lớp xe từ tên lớp ghi trong model; không có tên (hoặc bộ 80 lớp COCO) → COCO mặc định.</summary>
    private static Dictionary<int, VehicleType> ClassesFromMetadata(InferenceSession session, int numClasses)
    {
        if (numClasses != 80 && session.ModelMetadata.CustomMetadataMap.TryGetValue("names", out var names) && !string.IsNullOrWhiteSpace(names))
        {
            var map = new Dictionary<int, VehicleType>();
            foreach (Match m in NameEntry.Matches(names))
            {
                int id = int.Parse(m.Groups[1].Value);
                var name = (m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).Trim().ToLowerInvariant();
                VehicleType? type = name switch
                {
                    "car" => VehicleType.Car,
                    "bus" => VehicleType.Bus,
                    "truck" => VehicleType.Truck,
                    "motorcycle" or "motorbike" => VehicleType.Motorcycle,
                    _ => null
                };
                if (type is not null && (numClasses <= 0 || id < numClasses)) map[id] = type.Value;
            }
            if (map.Count > 0) return map;
        }
        return CocoClasses.Where(kv => numClasses <= 0 || kv.Key < numClasses).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public List<DetectionResult> Detect(Mat frame, float? confThreshold = null)
    {
        var st = _settings.Current;   // cai dat (nguong + loai xe) doi la co hieu luc ngay
        return Run(frame, confThreshold ?? st.ConfidenceThreshold, st.AllowsVehicle);
    }

    public List<DetectionResult> DetectAll(Mat frame, float confThreshold) => Run(frame, confThreshold, _ => true);

    private List<DetectionResult> Run(Mat frame, float threshold, Func<VehicleType, bool> allow)
    {
        var results = new List<DetectionResult>();
        if (frame.Empty()) return results;

        lock (_runLock)
        {
            var st = _state;
            if (st is null) return results;

            var classes = st.Classes.Where(kv => allow(kv.Value)).ToArray();
            if (classes.Length == 0) return results;

            var (input, scale, padX, padY) = Letterbox(frame, st.InputSize);
            var inputTensor = new DenseTensor<float>(input, new[] { 1, 3, st.InputSize, st.InputSize });
            using var outputs = st.Session.Run(new[] { NamedOnnxValue.CreateFromTensor(st.InputName, inputTensor) });
            var tensor = outputs.First().AsTensor<float>();
            var dense = tensor as DenseTensor<float> ?? tensor.ToDenseTensor();
            var data = dense.Buffer;   // Memory<float> (khong dung Span: Span la ref struct, local function khong bat duoc)

            // [1, 4+nc, N] (chuan) hoac [1, N, 4+nc] (chuyen vi): kenh nho hon la 4+nc
            int d1 = dense.Dimensions[1], d2 = dense.Dimensions[2];
            bool channelsFirst = d1 <= d2;
            int numBoxes = channelsFirst ? d2 : d1, numCh = channelsFirst ? d1 : d2;
            float At(int ch, int i) => channelsFirst ? data.Span[ch * numBoxes + i] : data.Span[i * numCh + ch];

            // Mot so file ONNX tra toa do khung dang CHUAN HOA (0-1) thay vi pixel: tu nhan biet roi nhan voi kich thuoc anh vao.
            float maxW = 0f, maxH = 0f;
            for (int i = 0; i < numBoxes; i++) { maxW = Math.Max(maxW, At(2, i)); maxH = Math.Max(maxH, At(3, i)); }
            float coordScale = (maxW <= 2f && maxH <= 2f) ? st.InputSize : 1f;

            var candidates = new List<(BoundingBox box, int classId, float score)>();
            for (int i = 0; i < numBoxes; i++)
            {
                // Chi xet cac lop xe: nhanh hon va khong bo sot khi lop khac co diem cao hon
                float bestScore = 0f;
                int bestClass = -1;
                foreach (var (classId, _) in classes)
                {
                    float score = At(4 + classId, i);
                    if (score > bestScore) { bestScore = score; bestClass = classId; }
                }
                if (bestScore < threshold || bestClass < 0) continue;

                float cx = At(0, i) * coordScale, cy = At(1, i) * coordScale;
                float w = At(2, i) * coordScale, h = At(3, i) * coordScale;
                candidates.Add((new BoundingBox
                {
                    X1 = (cx - w / 2 - padX) / scale, Y1 = (cy - h / 2 - padY) / scale,
                    X2 = (cx + w / 2 - padX) / scale, Y2 = (cy + h / 2 - padY) / scale
                }, bestClass, bestScore));
            }

            foreach (var (box, classId, score) in NonMaxSuppression(candidates, 0.5f))
                results.Add(new DetectionResult
                {
                    Box = box, Confidence = score, VehicleType = st.Classes[classId], Color = EstimateDominantColor(frame, box)
                });
        }
        return results;
    }

    /// <summary>Resize ảnh về size×size giữ tỉ lệ (letterbox) và chuyển sang tensor CHW (RGB, 0-1) bằng các phép toán OpenCV theo khối.</summary>
    private static (float[] data, float scale, float padX, float padY) Letterbox(Mat frame, int size)
    {
        float scale = Math.Min((float)size / frame.Width, (float)size / frame.Height);
        int newW = Math.Max(1, (int)(frame.Width * scale));
        int newH = Math.Max(1, (int)(frame.Height * scale));
        float padX = (size - newW) / 2f;
        float padY = (size - newH) / 2f;

        using var resized = new Mat();
        Cv2.Resize(frame, resized, new Size(newW, newH));

        using var canvas = new Mat(size, size, MatType.CV_8UC3, new Scalar(114, 114, 114));
        using (var roi = new Mat(canvas, new Rect((int)padX, (int)padY, newW, newH)))
            resized.CopyTo(roi);

        using var rgb = new Mat();
        Cv2.CvtColor(canvas, rgb, ColorConversionCodes.BGR2RGB);
        using var f32 = new Mat();
        rgb.ConvertTo(f32, MatType.CV_32FC3, 1.0 / 255.0);

        int plane = size * size;
        var data = new float[3 * plane];
        Cv2.Split(f32, out Mat[] channels);                       // moi kenh la 1 Mat lien tuc size x size
        try
        {
            for (int c = 0; c < 3; c++) Marshal.Copy(channels[c].Data, data, c * plane, plane);
        }
        finally
        {
            foreach (var ch in channels) ch.Dispose();
        }
        return (data, scale, padX, padY);
    }

    /// <summary>NMS khong phan biet lop: 1 chiec xe khong the vua la "car" vua la "truck".</summary>
    private static List<(BoundingBox box, int classId, float score)> NonMaxSuppression(
        List<(BoundingBox box, int classId, float score)> candidates, float iouThreshold)
    {
        var result = new List<(BoundingBox, int, float)>();
        var sorted = candidates.OrderByDescending(c => c.score).ToList();
        while (sorted.Count > 0)
        {
            var best = sorted[0];
            result.Add(best);
            sorted.RemoveAt(0);
            sorted.RemoveAll(c => Iou(best.box, c.box) > iouThreshold);
        }
        return result;
    }

    private static float Iou(BoundingBox a, BoundingBox b)
    {
        float x1 = Math.Max(a.X1, b.X1);
        float y1 = Math.Max(a.Y1, b.Y1);
        float x2 = Math.Min(a.X2, b.X2);
        float y2 = Math.Min(a.Y2, b.Y2);
        float inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        float union = a.Width * a.Height + b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>Ước lượng màu chủ đạo của xe rất đơn giản (dùng cho hiển thị, không phải AI thực sự).</summary>
    private static string EstimateDominantColor(Mat frame, BoundingBox box)
    {
        try
        {
            var rect = new Rect(
                (int)Math.Max(0, box.X1), (int)Math.Max(0, box.Y1),
                (int)Math.Min(box.Width, frame.Width - box.X1),
                (int)Math.Min(box.Height, frame.Height - box.Y1));
            if (rect.Width <= 0 || rect.Height <= 0) return "unknown";

            using var crop = new Mat(frame, rect);
            var mean = Cv2.Mean(crop);
            double b = mean.Val0, g = mean.Val1, r = mean.Val2;

            if (r > 150 && g < 100 && b < 100) return "red";
            if (r > 180 && g > 180 && b > 180) return "white";
            if (r < 70 && g < 70 && b < 70) return "black";
            if (b > 130 && b > r && b > g) return "blue";
            return "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    public void Dispose() => _state?.Dispose();
}
