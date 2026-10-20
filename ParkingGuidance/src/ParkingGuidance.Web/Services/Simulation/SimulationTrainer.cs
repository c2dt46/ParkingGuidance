using System.Diagnostics;
using System.Text.Json;
using ParkingGuidance.Web.Services.Anpr;

namespace ParkingGuidance.Web.Services.Simulation;

/// <summary>Kết quả cài model mới vào ứng dụng.</summary>
public sealed record InstallInfo(bool Ok, string Message, string[] Targets, string[] Backups, DateTime At);

/// <summary>
/// Điều khiển việc "dạy lại" model đọc ký tự từ các biển đã sửa ở tab Mô phỏng.
/// • Train chạy ở MỘT TIẾN TRÌNH RIÊNG (PlateTrainer, Python) — ưu tiên thấp (BelowNormal), ít luồng CPU — chỉ khi bấm Train và thoát khi xong,
///   nên không ảnh hưởng nhận diện camera thật; không có dịch vụ nền nào chạy khi không dùng.
/// • Xong mà model mới không kém model đang chạy thì tự CÀI (sao lưu bản cũ) và NẠP LẠI ngay, không cần khởi động lại ứng dụng.
///   Nếu kém hơn thì không tự cài; người dùng có thể bấm "Vẫn cài bản mới".
/// </summary>
public sealed class SimulationTrainer : IDisposable
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly SimulationCorrectionStore _store;
    private readonly ICharModelReloader _reloader;
    private readonly ILogger<SimulationTrainer> _logger;

    private readonly object _lock = new();
    private readonly object _installLock = new();
    private readonly Queue<string> _stderr = new();
    private Process? _proc;          // khác null từ lúc bắt đầu tới khi xử lý xong (kể cả bước cài model)
    private bool _cancelled;
    private InstallInfo? _install;

    public SimulationTrainer(IConfiguration config, IWebHostEnvironment env, SimulationCorrectionStore store,
        ICharModelReloader reloader, ILogger<SimulationTrainer> logger)
    {
        _config = config;
        _env = env;
        _store = store;
        _reloader = reloader;
        _logger = logger;
    }

    private string StatusPath => Path.Combine(_store.StatusDirectory, "train-status.json");

    // ------------------------------------------------------------------ tìm PlateTrainer / Python
    public string? ResolveTrainerPath()
    {
        var configured = _config["Trainer:Path"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.GetFullPath(configured.Trim().Trim('"'));
            return IsTrainerDir(full) ? full : null;
        }

        foreach (var start in new[] { _env.ContentRootPath, AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (int i = 0; dir is not null && i < 8; i++, dir = dir.Parent)
            {
                var cand = Path.Combine(dir.FullName, "PlateTrainer");
                if (IsTrainerDir(cand)) return cand;
            }
        }
        return null;
    }

    private static bool IsTrainerDir(string dir) => File.Exists(Path.Combine(dir, "anpr", "autotrain.py"));

    private string ResolvePython(string trainerPath)
    {
        var configured = _config["Trainer:Python"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim().Trim('"');

        var venv = OperatingSystem.IsWindows()
            ? Path.Combine(trainerPath, ".venv", "Scripts", "python.exe")
            : Path.Combine(trainerPath, ".venv", "bin", "python");
        if (File.Exists(venv)) return venv;
        return OperatingSystem.IsWindows() ? "python" : "python3";
    }

    // ------------------------------------------------------------------ bắt đầu / huỷ
    public (bool Ok, string Message) Start(int? epochs)
    {
        lock (_lock)
        {
            if (_proc is not null) return (false, "Đang huấn luyện — hãy chờ xong hoặc bấm Huỷ.");

            if (_store.List().Count == 0)
                return (false, "Chưa có mẫu nào. Hãy nhập 'Biển đúng' (hoặc bấm ✓ Đúng) cho ít nhất 1 xe.");

            var trainer = ResolveTrainerPath();
            if (trainer is null)
                return (false, "Không tìm thấy thư mục PlateTrainer (cần PlateTrainer\\anpr\\autotrain.py cạnh dự án). " +
                               "Hãy đặt đường dẫn ở Trainer:Path trong appsettings.json.");

            var python = ResolvePython(trainer);
            var ep = Math.Clamp(epochs ?? _config.GetValue("Trainer:Epochs", 25), 3, 200);
            var threads = _config.GetValue("Trainer:Threads", 0);
            if (threads <= 0) threads = Math.Max(1, Environment.ProcessorCount / 4);

            var psi = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = trainer,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add("anpr.autotrain");
            psi.ArgumentList.Add("--corrections"); psi.ArgumentList.Add(_store.CorrectionsDirectory);
            psi.ArgumentList.Add("--current-model"); psi.ArgumentList.Add(_reloader.CurrentModelPath ?? "");
            psi.ArgumentList.Add("--status"); psi.ArgumentList.Add(StatusPath);
            psi.ArgumentList.Add("--epochs"); psi.ArgumentList.Add(ep.ToString());
            psi.ArgumentList.Add("--threads"); psi.ArgumentList.Add(threads.ToString());
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUNBUFFERED"] = "1";

            _cancelled = false;
            _install = null;
            _stderr.Clear();
            try { Directory.CreateDirectory(Path.GetDirectoryName(StatusPath)!); if (File.Exists(StatusPath)) File.Delete(StatusPath); }
            catch { /* khong quan trong */ }

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, _) => { };   // bắt buộc đọc để ống không đầy; nội dung chi tiết nằm trong status.json
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (_lock)
                {
                    _stderr.Enqueue(e.Data);
                    while (_stderr.Count > 30) _stderr.Dequeue();
                }
            };
            p.Exited += (_, _) => _ = Task.Run(() => OnExited(p));

            _proc = p;   // gán TRƯỚC khi chạy: nếu tiến trình thoát cực nhanh thì OnExited vẫn thấy đúng đối tượng
            try
            {
                p.Start();
            }
            catch (Exception ex)
            {
                _proc = null;
                p.Dispose();
                return (false, $"Không chạy được Python ('{python}'): {ex.Message}. Hãy cài Python rồi chạy PlateTrainer\\install.bat.");
            }

            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; }   // tiến trình con (train) kế thừa mức ưu tiên này
            catch { /* khong dat duoc thi van chay */ }

            _logger.LogInformation("Mo phong: bat dau train ({Python}, {Epochs} epoch, {Threads} luong, uu tien thap).", python, ep, threads);
            return (true, "Đã bắt đầu huấn luyện ở chế độ nền (ưu tiên thấp).");
        }
    }

    public bool Cancel()
    {
        Process? p;
        lock (_lock)
        {
            p = _proc;
            if (p is null) return false;
            _cancelled = true;
        }
        try { p.Kill(entireProcessTree: true); } catch { /* da thoat */ }
        return true;
    }

    private void OnExited(Process p)
    {
        try
        {
            bool cancelled;
            lock (_lock) cancelled = _cancelled;
            if (!cancelled && ReadStatus() is JsonElement root && IsFinishedOk(root) && IsRecommended(root) && TryGetCandidate(root, out var candidate))
            {
                var info = InstallCandidate(candidate);
                lock (_lock) _install = info;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mo phong: loi khi hoan tat train.");
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_proc, p)) _proc = null;
            }
            try { p.Dispose(); } catch { /* khong quan trong */ }
        }
    }

    // ------------------------------------------------------------------ trạng thái
    public object GetStatus()
    {
        var trainer = ResolveTrainerPath();
        var st = ReadStatus();
        bool running, cancelled;
        InstallInfo? install;
        string[] errLines;
        lock (_lock)
        {
            running = _proc is not null;
            cancelled = _cancelled;
            install = _install;
            errLines = _stderr.ToArray();
        }

        bool finished = st is JsonElement r && IsFinished(r);
        bool canInstall = !running && !cancelled && st is JsonElement r2 && IsFinishedOk(r2)
                          && TryGetCandidate(r2, out _) && install is not { Ok: true };
        // Có file trạng thái nhưng chưa "xong/lỗi" mà không còn tiến trình: bị dừng giữa chừng (tắt máy, tắt ứng dụng...)
        bool interrupted = !running && !cancelled && st is not null && !finished;

        return new
        {
            trainerFound = trainer is not null,
            running,
            cancelled,
            interrupted,
            status = st,
            installed = install,
            canInstall,
            stderr = (interrupted || (!running && st is null && errLines.Length > 0)) ? string.Join("\n", errLines.TakeLast(8)) : null,
            corrections = _store.List().Count,
        };
    }

    /// <summary>
    /// Xoá kết quả lần train gần nhất (bảng so sánh model cũ / mới, chi tiết từng biển, thông tin cài đặt). Không đụng tới model đang dùng,
    /// các mẫu đã lưu và các bản sao lưu model. Không xoá được khi đang train.
    /// </summary>
    public (bool Ok, string Message) ClearResult()
    {
        lock (_lock)
        {
            if (_proc is not null) return (false, "Đang huấn luyện — hãy đợi xong hoặc huỷ train trước khi xoá kết quả.");
            _cancelled = false;
            _install = null;
            _stderr.Clear();
        }
        try { if (File.Exists(StatusPath)) File.Delete(StatusPath); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mo phong: khong xoa duoc ket qua train.");
            return (false, "Không xoá được tệp kết quả train: " + ex.Message);
        }
        return (true, "Đã xoá kết quả train gần nhất.");
    }

    // ------------------------------------------------------------------ cài model
    /// <summary>Người dùng bấm "Vẫn cài bản mới" (model mới không được tự cài vì kém hơn ở một chỉ số).</summary>
    public (bool Ok, string Message) InstallLatest()
    {
        lock (_lock)
        {
            if (_proc is not null) return (false, "Đang huấn luyện.");
        }
        if (!(ReadStatus() is JsonElement root) || !IsFinishedOk(root) || !TryGetCandidate(root, out var candidate))
            return (false, "Không có model mới nào để cài.");

        var info = InstallCandidate(candidate);
        lock (_lock) _install = info;
        return (info.Ok, info.Message);
    }

    /// <summary>Sao lưu rồi ghi đè model ở thư mục đang chạy + thư mục nguồn (Models/), sau đó nạp lại ngay.</summary>
    private InstallInfo InstallCandidate(string candidate)
    {
        lock (_installLock)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var targets = new List<string>();
            var backups = new List<string>();

            var runtime = _reloader.CurrentModelPath;
            if (!string.IsNullOrEmpty(runtime)) targets.Add(Path.GetFullPath(runtime));

            var srcDir = Path.Combine(_env.ContentRootPath, "Models");
            var srcModel = Path.GetFullPath(Path.Combine(srcDir, "char_cnn.onnx"));
            if (Directory.Exists(srcDir) && !targets.Contains(srcModel, StringComparer.OrdinalIgnoreCase))
                targets.Add(srcModel);

            if (targets.Count == 0)
                return new InstallInfo(false, "Không xác định được nơi cài model.", Array.Empty<string>(), Array.Empty<string>(), DateTime.Now);

            // 1) Nạp model mới ngay từ file ứng viên: nhả file model cũ (Windows không cho ghi đè file đang mở).
            if (!_reloader.Reload(candidate, out var msg))
                return new InstallInfo(false, msg, targets.ToArray(), Array.Empty<string>(), DateTime.Now);

            // 2) Sao lưu + ghi đè các file model.
            try
            {
                foreach (var t in targets)
                {
                    if (File.Exists(t))
                    {
                        var b = t + ".bak-" + stamp;
                        File.Copy(t, b, true);
                        backups.Add(b);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(t)!);
                    File.Copy(candidate, t, true);
                }
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrEmpty(runtime) && File.Exists(runtime)) _reloader.Reload(runtime, out _);   // quay lại model cũ
                return new InstallInfo(false, "Không ghi được file model: " + ex.Message, targets.ToArray(), backups.ToArray(), DateTime.Now);
            }

            // 3) Trỏ lại về file model thật trong thư mục chạy (để lần build/khởi động sau dùng đúng file).
            _reloader.Reload(targets[0], out _);
            _logger.LogInformation("Mo phong: da cai model ky tu moi ({Targets}); ban cu luu: {Backups}",
                string.Join("; ", targets), string.Join("; ", backups));
            return new InstallInfo(true, "Đã cài model mới và nạp lại ngay — không cần khởi động lại. Bản cũ được sao lưu (.bak-" + stamp + ").",
                targets.ToArray(), backups.ToArray(), DateTime.Now);
        }
    }

    // ------------------------------------------------------------------ đọc status.json do Python ghi
    private JsonElement? ReadStatus()
    {
        try
        {
            if (!File.Exists(StatusPath)) return null;
            using var fs = new FileStream(StatusPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;   // đang bị thay thế giữa chừng / chưa có: lần đọc sau sẽ được
        }
    }

    private static bool IsFinished(JsonElement root) =>
        root.TryGetProperty("finished", out var f) && f.ValueKind == JsonValueKind.True;

    private static bool IsFinishedOk(JsonElement root) =>
        IsFinished(root) && !(root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String);

    private static bool IsRecommended(JsonElement root) =>
        root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.Object
        && res.TryGetProperty("recommendInstall", out var rec) && rec.ValueKind == JsonValueKind.True;

    private static bool TryGetCandidate(JsonElement root, out string candidate)
    {
        candidate = "";
        if (root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.Object
            && res.TryGetProperty("candidate", out var c) && c.ValueKind == JsonValueKind.String)
            candidate = c.GetString() ?? "";
        return candidate.Length > 0 && File.Exists(candidate);
    }

    public void Dispose() => Cancel();   // tắt ứng dụng thì không để tiến trình train mồ côi
}
