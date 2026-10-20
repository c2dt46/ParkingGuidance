using System.Diagnostics;
using System.Text.Json;
using ParkingGuidance.Web.Services.Detection;

namespace ParkingGuidance.Web.Services.VehicleTrain;

/// <summary>Kết quả cài model xe mới vào ứng dụng.</summary>
public sealed record VehicleInstallInfo(bool Ok, string Message, string? Backup, DateTime At);

/// <summary>
/// Điều khiển việc huấn luyện thêm (fine-tune) model nhận diện xe YOLO11 từ các ảnh đã gán khung ở tab "Train xe".
/// • Train chạy ở MỘT TIẾN TRÌNH RIÊNG (tools/train_vehicle.py, Python + Ultralytics) — ưu tiên thấp (BelowNormal), chỉ khi bấm Train và thoát khi xong,
///   nên không ảnh hưởng nhận diện camera thật; không có dịch vụ nền nào chạy khi không dùng.
/// • Xong mà model mới không kém model đang chạy (mAP50-95 trên tập kiểm tra) thì tự CÀI vào <c>data/vehicle-train/active/vehicle.onnx</c>
///   (sao lưu bản cũ) và NẠP LẠI ngay, không cần khởi động lại ứng dụng. Kém hơn thì không tự cài; người dùng có thể bấm "Vẫn cài bản mới".
/// • "Khôi phục model gốc" bỏ model đã train thêm (giữ lại bản sao lưu) và nạp lại model gốc.
/// </summary>
public sealed class VehicleTrainer : IDisposable
{
    public const int MinSamples = 10;

    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly VehicleTrainStore _store;
    private readonly IVehicleDetectionService _detector;
    private readonly ILogger<VehicleTrainer> _logger;

    private readonly object _lock = new();
    private readonly object _installLock = new();
    private readonly Queue<string> _log = new();
    private Process? _proc;           // khac null tu luc bat dau toi khi xu ly xong (ke ca buoc cai model)
    private bool _cancelled;
    private VehicleInstallInfo? _install;

    public VehicleTrainer(IConfiguration config, IWebHostEnvironment env, VehicleTrainStore store,
        IVehicleDetectionService detector, ILogger<VehicleTrainer> logger)
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
        var configured = _config["VehicleTrainer:Script"];
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
                var cand = Path.Combine(dir.FullName, "tools", "train_vehicle.py");
                if (File.Exists(cand)) return cand;
            }
        }
        return null;
    }

    private string ResolvePython(string scriptPath)
    {
        var configured = _config["VehicleTrainer:Python"];
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
                return (false, $"Cần ít nhất {MinSamples} ảnh đã duyệt (hiện có {reviewed}). Nên có từ 100 ảnh trở lên để model tốt hơn rõ rệt.");

            var script = ResolveScriptPath();
            if (script is null)
                return (false, "Không tìm thấy tools/train_vehicle.py. Hãy đặt đường dẫn ở VehicleTrainer:Script trong appsettings.json.");

            var ep = Math.Clamp(epochs ?? _config.GetValue("VehicleTrainer:Epochs", 30), 3, 300);
            var baseWeights = _config["VehicleTrainer:BaseWeights"] is { Length: > 0 } w ? w : "yolo11n.pt";
            var imgsz = Math.Clamp(_config.GetValue("VehicleTrainer:ImgSize", 640), 320, 1280);
            var device = _config["VehicleTrainer:Device"] is { Length: > 0 } d ? d : "cpu";
            var threads = _config.GetValue("VehicleTrainer:Threads", 0);
            var batch = _config.GetValue("VehicleTrainer:Batch", 8);

            // model "hien tai" de so sanh: model da train them neu co, khong thi model goc dang chay
            var current = File.Exists(_detector.CustomModelPath) ? _detector.CustomModelPath : _detector.ResolvedModelPath;
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
                psi.ArgumentList.Add("--fallback-base"); psi.ArgumentList.Add(baseWeights);
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
                _logger.LogInformation("Train xe: bat dau ({Epochs} epoch, {Imgs} anh da duyet, xuat phat tu {Start}).", ep, reviewed, start);
                return (true, $"Đã bắt đầu huấn luyện ({reviewed} ảnh, {ep} epoch).");
            }
            catch (Exception ex)
            {
                _proc = null;
                _logger.LogError(ex, "Train xe: khong chay duoc.");
                return (false, "Không chạy được Python: " + ex.Message + " — kiểm tra VehicleTrainer:Python và đã cài ultralytics (pip install ultralytics).");
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
                else _logger.LogInformation("Train xe: model moi khong tot hon model dang chay — khong tu cai.");
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Train xe: loi khi xu ly ket qua."); }
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

    /// <summary>Model mới không kém model đang chạy (mAP50-95 trên tập kiểm tra). Không đo được model cũ thì coi là tốt hơn nếu có số của model mới.</summary>
    private static bool IsBetterOrEqual(JsonElement status)
    {
        var nw = Metric(status, "new", "map5095");
        if (nw is null) return false;
        var old = Metric(status, "base", "map5095");
        return old is null || nw.Value >= old.Value;
    }

    public object GetStatus()
    {
        JsonElement? st = ReadStatus();
        bool running, cancelled;
        string[] tail;
        VehicleInstallInfo? install;
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
            usingCustom = _detector.UsingCustomModel,
            hasCustomFile = File.Exists(_detector.CustomModelPath),
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
    /// <summary>Cài model vừa train (best.onnx) làm model nhận diện xe và nạp lại ngay. <paramref name="force"/> = cài dù chỉ số không vượt model cũ.</summary>
    public VehicleInstallInfo Install(bool force)
    {
        lock (_installLock)
        {
            var src = Path.Combine(RunDir, "best.onnx");
            if (!File.Exists(src))
                return _install = new VehicleInstallInfo(false, "Không tìm thấy model vừa train (best.onnx).", null, DateTime.Now);

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
                    return _install = new VehicleInstallInfo(false, "Model mới không nạp được: " + msg, null, DateTime.Now);
                }

                _logger.LogInformation("Train xe: da cai model moi {Target} (sao luu: {Backup}).", target, backup ?? "khong co");
                return _install = new VehicleInstallInfo(true,
                    "Đã cài model xe mới và nạp lại ngay — không cần khởi động lại." + (backup is null ? "" : $" Bản cũ được sao lưu ({Path.GetFileName(backup)})."),
                    backup, DateTime.Now);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Train xe: cai model that bai.");
                return _install = new VehicleInstallInfo(false, "Không cài được model: " + ex.Message, null, DateTime.Now);
            }
        }
    }

    /// <summary>Bỏ model đã train thêm (giữ lại bản sao lưu) và quay về model gốc.</summary>
    public (bool Ok, string Message) RestoreBase()
    {
        lock (_lock) { if (_proc is not null) return (false, "Đang huấn luyện — hãy đợi xong hoặc huỷ trước."); }
        lock (_installLock)
        {
            try
            {
                var target = _detector.CustomModelPath;
                if (!File.Exists(target)) return (false, "Đang dùng model gốc rồi.");
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Move(target, target + ".removed-" + stamp);
                if (File.Exists(LastBestPtPath)) File.Move(LastBestPtPath, LastBestPtPath + ".removed-" + stamp);

                _detector.Reload(out var msg);
                _install = null;
                return (true, "Đã quay về model gốc (bản đã train được giữ lại với đuôi .removed). " + msg);
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
