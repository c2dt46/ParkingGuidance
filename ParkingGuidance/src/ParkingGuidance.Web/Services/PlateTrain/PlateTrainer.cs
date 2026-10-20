using System.Diagnostics;
using System.Text.Json;
using ParkingGuidance.Web.Services.Anpr;

namespace ParkingGuidance.Web.Services.PlateTrain;

/// <summary>Kết quả cài bộ dò biển số mới vào ứng dụng.</summary>
public sealed record PlateInstallInfo(bool Ok, string Message, string? Backup, DateTime At);

/// <summary>
/// Điều khiển việc huấn luyện thêm (fine-tune) BỘ DÒ BIỂN SỐ (YOLO11, 1 lớp) từ các ảnh đã gán khung ở tab "Train biển số".
/// • Train chạy ở MỘT TIẾN TRÌNH RIÊNG (tools/train_plate.py, Python + Ultralytics) — ưu tiên thấp (BelowNormal), chỉ khi bấm Train và thoát khi xong,
///   nên không ảnh hưởng nhận diện camera thật; không có dịch vụ nền nào chạy khi không dùng.
/// • Xong mà model mới không kém model đang chạy (mAP50-95 trên tập kiểm tra) thì tự CÀI vào <c>data/plate-train/active/plate.onnx</c>
///   (sao lưu bản cũ) và NẠP LẠI ngay, không cần khởi động lại ứng dụng. Kém hơn thì không tự cài; người dùng có thể bấm "Vẫn cài bản mới".
///   Chưa có model ONNX biển số nào để so sánh (bộ dò mặc định là Darknet) thì chỉ tự cài khi mAP50 của model mới đạt <see cref="MinAutoInstallMap50"/>.
/// • "Khôi phục model gốc" bỏ model đã train thêm (giữ lại bản sao lưu) và quay về bộ dò biển mặc định.
/// </summary>
public sealed class PlateTrainer : IDisposable
{
    public const int MinSamples = 10;

    /// <summary>Không có model cũ để so sánh: chỉ tự cài model mới khi mAP50 trên tập kiểm tra từ mức này (tránh thay bộ dò đang chạy tốt bằng model train quá ít ảnh).</summary>
    public const double MinAutoInstallMap50 = 0.6;

    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly PlateTrainStore _store;
    private readonly IPlateDetectorModel _detector;
    private readonly ILogger<PlateTrainer> _logger;

    private readonly object _lock = new();
    private readonly object _installLock = new();
    private readonly Queue<string> _log = new();
    private Process? _proc;           // khac null tu luc bat dau toi khi xu ly xong (ke ca buoc cai model)
    private bool _cancelled;
    private PlateInstallInfo? _install;

    public PlateTrainer(IConfiguration config, IWebHostEnvironment env, PlateTrainStore store,
        IPlateDetectorModel detector, ILogger<PlateTrainer> logger)
    {
        _config = config;
        _env = env;
        _store = store;
        _detector = detector;
        _logger = logger;
    }

    private string RunDir => Path.Combine(_store.RootDirectory, "run");
    private string StatusPath => Path.Combine(_store.RootDirectory, "train-status.json");
    private string ActiveDir => Path.GetDirectoryName(_detector.CustomModelPath)!;
    private string LastBestPtPath => Path.Combine(ActiveDir, "last-best.pt");

    public bool Running { get { lock (_lock) return _proc is not null; } }

    // ------------------------------------------------------------------ tim script / Python
    public string? ResolveScriptPath()
    {
        var configured = _config["PlateTrainer:Script"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.GetFullPath(configured.Trim().Trim('"'));
            return File.Exists(full) ? full : null;
        }
        foreach (var start in new[] { _env.ContentRootPath, AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (int i = 0; dir is not null && i < 8; i++, dir = dir.Parent)
            {
                var cand = Path.Combine(dir.FullName, "tools", "train_plate.py");
                if (File.Exists(cand)) return cand;
            }
        }
        return null;
    }

    private string ResolvePython(string scriptPath)
    {
        var configured = _config["PlateTrainer:Python"];
        if (string.IsNullOrWhiteSpace(configured)) configured = _config["Trainer:Python"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim().Trim('"');

        var toolsDir = Path.GetDirectoryName(scriptPath)!;
        var venv = OperatingSystem.IsWindows()
            ? Path.Combine(toolsDir, ".venv", "Scripts", "python.exe")
            : Path.Combine(toolsDir, ".venv", "bin", "python");
        if (File.Exists(venv)) return venv;
        return OperatingSystem.IsWindows() ? "python" : "python3";
    }

    // ------------------------------------------------------------------ bat dau / huy
    public (bool Ok, string Message) Start(int? epochs, bool fromScratch)
    {
        lock (_lock)
        {
            if (_proc is not null) return (false, "Đang huấn luyện — hãy đợi xong hoặc huỷ.");

            var reviewed = _store.List().Count(s => s.Reviewed);
            if (reviewed < MinSamples)
                return (false, $"Cần ít nhất {MinSamples} ảnh đã duyệt (hiện có {reviewed}). Nên có từ 50 ảnh trở lên (đủ ngày/đêm, gần/xa) để model tốt hơn rõ rệt.");

            var script = ResolveScriptPath();
            if (script is null)
                return (false, "Không tìm thấy tools/train_plate.py. Hãy đặt đường dẫn ở PlateTrainer:Script trong appsettings.json.");

            var ep = Math.Clamp(epochs ?? _config.GetValue("PlateTrainer:Epochs", 40), 3, 300);
            var baseWeights = _config["PlateTrainer:BaseWeights"] is { Length: > 0 } w ? w : "yolo11n.pt";
            var imgsz = Math.Clamp(_config.GetValue("PlateTrainer:ImgSize", 640), 320, 1280);
            var device = _config["PlateTrainer:Device"] is { Length: > 0 } d ? d : "cpu";
            var threads = _config.GetValue("PlateTrainer:Threads", 0);
            var batch = _config.GetValue("PlateTrainer:Batch", 8);

            // model "hien tai" de so sanh: model da train them neu co, khong thi plate_detector.onnx goc (neu co). Bo do Darknet mac dinh
            // khong do duoc bang Ultralytics → current = null (khi do chi tu cai neu mAP50 cua model moi du cao).
            var current = File.Exists(_detector.CustomModelPath) ? _detector.CustomModelPath : _detector.BaseModelPath;
            // tiep tuc tu lan train gan nhat (neu co) tru khi nguoi dung chon bat dau lai tu model goc
            var start = !fromScratch && File.Exists(LastBestPtPath) ? LastBestPtPath : baseWeights;

            try
            {
                Directory.CreateDirectory(_store.RootDirectory);
                if (File.Exists(StatusPath)) File.Delete(StatusPath);
                if (Directory.Exists(RunDir)) Directory.Delete(RunDir, recursive: true);

                var psi = new ProcessStartInfo
                {
                    FileName = ResolvePython(script),
                    WorkingDirectory = Path.GetDirectoryName(script)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var a in new[]
                {
                    "-u", script, "--samples", _store.SamplesDirectory, "--out", RunDir, "--status", StatusPath,
                    "--base", start, "--epochs", ep.ToString(), "--imgsz", imgsz.ToString(), "--device", device,
                    "--batch", batch.ToString(), "--threads", threads.ToString()
                }) psi.ArgumentList.Add(a);
                if (!string.IsNullOrEmpty(current)) { psi.ArgumentList.Add("--current"); psi.ArgumentList.Add(current); }
                psi.Environment["PYTHONUNBUFFERED"] = "1";
                psi.Environment["PYTHONIOENCODING"] = "utf-8";

                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (_, e) => Remember(e.Data);
                p.ErrorDataReceived += (_, e) => Remember(e.Data);
                p.Exited += (_, _) => Task.Run(() => OnExited(p));

                _log.Clear();
                _cancelled = false;
                _install = null;
                p.Start();
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* he dieu hanh khong cho */ }
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                _proc = p;
                _logger.LogInformation("Train bien so: bat dau ({Epochs} epoch, {Imgs} anh da duyet, xuat phat tu {Start}).", ep, reviewed, start);
                return (true, $"Đã bắt đầu huấn luyện ({reviewed} ảnh, {ep} epoch).");
            }
            catch (Exception ex)
            {
                _proc = null;
                _logger.LogError(ex, "Train bien so: khong chay duoc.");
                return (false, "Không chạy được Python: " + ex.Message + " — kiểm tra PlateTrainer:Python và đã cài ultralytics (pip install ultralytics).");
            }
        }
    }

    public (bool Ok, string Message) Cancel()
    {
        Process? p;
        lock (_lock) { p = _proc; if (p is null) return (false, "Không có tiến trình train nào đang chạy."); _cancelled = true; }
        try { p.Kill(entireProcessTree: true); } catch { /* da thoat */ }
        return (true, "Đã huỷ huấn luyện.");
    }

    private void Remember(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_lock) { _log.Enqueue(line.Trim()); while (_log.Count > 40) _log.Dequeue(); }
    }

    private void OnExited(Process p)
    {
        try
        {
            try { p.WaitForExit(); } catch { /* bo qua */ }
            bool cancelled; lock (_lock) cancelled = _cancelled;
            if (cancelled) return;

            var st = ReadStatus();
            if (st is { } s && s.TryGetProperty("state", out var state) && state.GetString() == "done")
            {
                if (IsBetterOrEqual(s)) Install(force: false);
                else _logger.LogInformation("Train bien so: model moi khong tot hon model dang chay — khong tu cai.");
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Train bien so: loi khi xu ly ket qua."); }
        finally
        {
            lock (_lock) { _proc = null; }
            try { p.Dispose(); } catch { /* bo qua */ }
        }
    }

    // ------------------------------------------------------------------ trang thai
    private JsonElement? ReadStatus()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(StatusPath)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(StatusPath));
                return doc.RootElement.Clone();
            }
            catch { Thread.Sleep(50); }                  // script dang ghi do dang: doc lai
        }
        return null;
    }

    private static double? Metric(JsonElement root, string which, string key)
    {
        if (root.TryGetProperty("metrics", out var m) && m.ValueKind == JsonValueKind.Object
            && m.TryGetProperty(which, out var w) && w.ValueKind == JsonValueKind.Object
            && w.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        return null;
    }

    /// <summary>Model mới không kém model đang chạy (mAP50-95 trên tập kiểm tra). Không đo được model cũ (đang dùng bộ dò Darknet mặc định)
    /// thì chỉ coi là đủ tốt nếu mAP50 của model mới ≥ <see cref="MinAutoInstallMap50"/>.</summary>
    private static bool IsBetterOrEqual(JsonElement status)
    {
        var nw = Metric(status, "new", "map5095");
        if (nw is null) return false;
        var old = Metric(status, "base", "map5095");
        if (old is not null) return nw.Value >= old.Value;
        return (Metric(status, "new", "map50") ?? 0) >= MinAutoInstallMap50;
    }

    public object GetStatus()
    {
        JsonElement? st = ReadStatus();
        bool running, cancelled;
        string[] tail;
        PlateInstallInfo? install;
        lock (_lock) { running = _proc is not null; cancelled = _cancelled; tail = _log.ToArray(); install = _install; }

        bool done = st is { } s && s.TryGetProperty("state", out var state) && state.GetString() == "done";
        bool canInstall = !running && done && install is not { Ok: true } && File.Exists(Path.Combine(RunDir, "best.onnx"));
        var script = ResolveScriptPath();
        return new
        {
            running,
            cancelled,
            status = st,
            canInstall,
            install,
            log = (running || (st is { } s2 && s2.TryGetProperty("state", out var s3) && s3.GetString() == "error")) ? tail.TakeLast(12).ToArray() : Array.Empty<string>(),
            scriptFound = script is not null,
            minSamples = MinSamples,
            minAutoInstallMap50 = MinAutoInstallMap50,
            usingCustom = _detector.UsingCustomModel,
            hasCustomFile = File.Exists(_detector.CustomModelPath),
            hasBaseOnnx = _detector.BaseModelPath is not null,
            modelInfo = _detector.ModelInfo
        };
    }

    public (bool Ok, string Message) ClearResult()
    {
        lock (_lock)
        {
            if (_proc is not null) return (false, "Đang huấn luyện — hãy đợi xong hoặc huỷ trước khi xoá kết quả.");
            _cancelled = false; _install = null; _log.Clear();
        }
        try { if (File.Exists(StatusPath)) File.Delete(StatusPath); }
        catch (Exception ex) { return (false, "Không xoá được tệp kết quả: " + ex.Message); }
        return (true, "Đã xoá kết quả train gần nhất.");
    }

    // ------------------------------------------------------------------ cai / khoi phuc model
    /// <summary>Cài model vừa train (best.onnx) làm bộ dò biển số và nạp lại ngay. <paramref name="force"/> = cài dù chỉ số không vượt model cũ.</summary>
    public PlateInstallInfo Install(bool force)
    {
        lock (_installLock)
        {
            var src = Path.Combine(RunDir, "best.onnx");
            if (!File.Exists(src))
                return _install = new PlateInstallInfo(false, "Không tìm thấy model vừa train (best.onnx).", null, DateTime.Now);

            try
            {
                Directory.CreateDirectory(ActiveDir);
                var target = _detector.CustomModelPath;
                string? backup = null;
                if (File.Exists(target))
                {
                    backup = target + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Move(target, backup);
                }

                File.Copy(src, target, overwrite: true);
                var pt = Path.Combine(RunDir, "best.pt");
                if (File.Exists(pt)) File.Copy(pt, LastBestPtPath, overwrite: true);   // lan sau train tiep tu day

                if (!_detector.Reload(out var msg))
                {
                    // nap that bai: tra lai model cu
                    try { File.Delete(target); if (backup is not null) File.Move(backup, target); } catch { /* bo qua */ }
                    _detector.Reload(out _);
                    return _install = new PlateInstallInfo(false, "Model biển số mới không nạp được: " + msg, null, DateTime.Now);
                }

                _logger.LogInformation("Train bien so: da cai model moi {Target} (sao luu: {Backup}).", target, backup ?? "khong co");
                return _install = new PlateInstallInfo(true,
                    "Đã cài bộ dò biển số mới và nạp lại ngay — không cần khởi động lại." + (backup is null ? "" : $" Bản cũ được sao lưu ({Path.GetFileName(backup)})."),
                    backup, DateTime.Now);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Train bien so: cai model that bai.");
                return _install = new PlateInstallInfo(false, "Không cài được model: " + ex.Message, null, DateTime.Now);
            }
        }
    }

    /// <summary>Bỏ model đã train thêm (giữ lại bản sao lưu) và quay về bộ dò biển mặc định.</summary>
    public (bool Ok, string Message) RestoreBase()
    {
        lock (_lock) { if (_proc is not null) return (false, "Đang huấn luyện — hãy đợi xong hoặc huỷ trước."); }
        lock (_installLock)
        {
            try
            {
                var target = _detector.CustomModelPath;
                if (!File.Exists(target)) return (false, "Đang dùng bộ dò biển mặc định rồi.");
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Move(target, target + ".removed-" + stamp);
                if (File.Exists(LastBestPtPath)) File.Move(LastBestPtPath, LastBestPtPath + ".removed-" + stamp);

                _detector.Reload(out var msg);
                _install = null;
                return (true, "Đã quay về bộ dò biển mặc định (bản đã train được giữ lại với đuôi .removed). " + msg);
            }
            catch (Exception ex) { return (false, "Không khôi phục được: " + ex.Message); }
        }
    }

    public void Dispose()
    {
        Process? p; lock (_lock) p = _proc;
        try { p?.Kill(entireProcessTree: true); } catch { /* bo qua */ }
    }
}
