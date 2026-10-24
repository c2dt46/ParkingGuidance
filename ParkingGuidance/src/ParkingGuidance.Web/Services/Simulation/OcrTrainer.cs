using System.Diagnostics;
using System.Text.Json;
using ParkingGuidance.Web.Services.Anpr;

namespace ParkingGuidance.Web.Services.Simulation;

/// <summary>
/// Điều khiển việc huấn luyện model ĐỌC CẢ CHUỖI BIỂN (OCR, CRNN + CTC) từ các biển bạn đã gán ở tab Train.
/// Khác bộ train ký tự cũ (<see cref="SimulationTrainer"/>): model nhìn cả dòng chữ nên KHÔNG cần tách ký tự — lỗi "tách ký tự" không còn.
/// • Chạy <c>tools\train_plate_ocr.py</c> ở MỘT TIẾN TRÌNH RIÊNG (Python + PyTorch), ưu tiên thấp; xong là tự thoát. Tiến trình ghi file JSON trạng thái.
/// • Xong mà model đọc đúng ≥ 80% trên ≥ 5 biển thật chưa từng học thì tự CÀI (sao lưu bản cũ) và NẠP LẠI ngay, không cần khởi động lại.
///   Chưa đủ tin cậy thì không tự cài; người dùng có thể bấm "Vẫn cài bản mới".
/// </summary>
public sealed class OcrTrainer : IDisposable
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly SimulationCorrectionStore _store;
    private readonly IOcrModelReloader _reloader;
    private readonly ILogger<OcrTrainer> _logger;

    private readonly object _lock = new();
    private readonly object _installLock = new();
    private readonly Queue<string> _stderr = new();
    private Process? _proc;           // khác null từ lúc Python chạy tới khi xử lý xong (kể cả bước cài model)
    private bool _starting;           // đang kiểm tra Python / khởi động (chưa có tiến trình)
    private string? _startError;
    private bool _cancelled;
    private InstallInfo? _install;

    public OcrTrainer(IConfiguration config, IWebHostEnvironment env, SimulationCorrectionStore store,
        IOcrModelReloader reloader, ILogger<OcrTrainer> logger)
    {
        _config = config;
        _env = env;
        _store = store;
        _reloader = reloader;
        _logger = logger;
    }

    private string StatusPath => Path.Combine(_store.StatusDirectory, "ocr-train-status.json");
    private string RunDir => Path.Combine(_store.StatusDirectory, "ocr-run");

    // ------------------------------------------------------------------ tìm script / Python
    public string? ResolveScript()
    {
        var configured = _config["Trainer:OcrScript"];
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
                var cand = Path.Combine(dir.FullName, "tools", "train_plate_ocr.py");
                if (File.Exists(cand)) return cand;
            }
        }
        return null;
    }

    /// <summary>Các Python sẽ thử lần lượt: cấu hình Trainer:OcrPython → .venv cạnh script → .venv của PlateTrainer → python trong PATH.</summary>
    private List<string> PythonCandidates(string script)
    {
        var list = new List<string>();
        var configured = _config["Trainer:OcrPython"];
        if (!string.IsNullOrWhiteSpace(configured)) list.Add(configured.Trim().Trim('"'));

        string venvExe = OperatingSystem.IsWindows() ? Path.Combine(".venv", "Scripts", "python.exe") : Path.Combine(".venv", "bin", "python");
        var scriptDir = Path.GetDirectoryName(script)!;
        var beside = Path.Combine(scriptDir, venvExe);
        if (File.Exists(beside)) list.Add(beside);

        foreach (var start in new[] { _env.ContentRootPath, AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (int i = 0; dir is not null && i < 8; i++, dir = dir.Parent)
            {
                var cand = Path.Combine(dir.FullName, "PlateTrainer", venvExe);
                if (File.Exists(cand) && !list.Contains(cand, StringComparer.OrdinalIgnoreCase)) list.Add(cand);
            }
        }

        list.Add(OperatingSystem.IsWindows() ? "python" : "python3");
        return list;
    }

    /// <summary>Python này có đủ thư viện để train không (torch, cv2, numpy, onnxruntime, PIL)? Nạp torch có thể mất vài giây.</summary>
    private static bool CheckPython(string python, out string problem)
    {
        problem = "";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = python,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import torch, cv2, numpy, onnxruntime, PIL");
            using var p = Process.Start(psi);
            if (p is null) { problem = "không chạy được"; return false; }
            var err = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(90_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* da thoat */ }
                problem = "kiểm tra quá thời gian";
                return false;
            }
            if (p.ExitCode == 0) return true;
            var lines = err.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            problem = lines.Length > 0 ? lines[^1].Trim() : "lỗi không rõ";
            return false;
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return false;
        }
    }

    // ------------------------------------------------------------------ bắt đầu / huỷ
    public (bool Ok, string Message) Start(int? epochs, int? synth)
    {
        string script;
        lock (_lock)
        {
            if (_proc is not null || _starting) return (false, "Đang huấn luyện — hãy chờ xong hoặc bấm Huỷ.");

            if (_store.List().Count == 0)
                return (false, "Chưa có mẫu nào. Hãy nhập 'Biển đúng' (hoặc bấm ✓ Đúng) cho ít nhất 1 xe — càng nhiều biển KHÁC NHAU càng tốt.");

            var found = ResolveScript();
            if (found is null)
                return (false, "Không tìm thấy tools\\train_plate_ocr.py cạnh dự án. Hãy chép file vào thư mục tools hoặc đặt đường dẫn ở Trainer:OcrScript trong appsettings.json.");
            script = found;

            _starting = true;
            _startError = null;
            _cancelled = false;
            _install = null;
            _stderr.Clear();
            try { Directory.CreateDirectory(Path.GetDirectoryName(StatusPath)!); if (File.Exists(StatusPath)) File.Delete(StatusPath); }
            catch { /* khong quan trong */ }
        }

        int ep = Math.Clamp(epochs ?? _config.GetValue("Trainer:OcrEpochs", 20), 3, 200);
        int syn = Math.Clamp(synth ?? _config.GetValue("Trainer:OcrSynth", 4000), 0, 30000);
        _ = Task.Run(() => Launch(script, ep, syn));   // kiểm tra Python mất vài giây: không chặn yêu cầu HTTP
        return (true, "Đang kiểm tra Python và khởi động huấn luyện…");
    }

    private void Launch(string script, int epochs, int synth)
    {
        try
        {
            string? python = null;
            var problems = new List<string>();
            foreach (var cand in PythonCandidates(script))
            {
                lock (_lock) { if (_cancelled) { _starting = false; return; } }
                if (CheckPython(cand, out var problem)) { python = cand; break; }
                problems.Add($"'{cand}': {problem}");
            }

            if (python is null)
            {
                lock (_lock)
                {
                    _starting = false;
                    _startError = "Không tìm thấy Python có đủ thư viện (torch, opencv-python, numpy, onnxruntime, pillow). Đã thử: "
                                  + string.Join(" | ", problems)
                                  + ". Cài: pip install torch onnxruntime pillow opencv-python numpy — hoặc đặt đường dẫn Python đã cài sẵn ở Trainer:OcrPython trong appsettings.json.";
                }
                return;
            }

            int threads = _config.GetValue("Trainer:Threads", 0);
            if (threads <= 0) threads = Math.Max(1, Environment.ProcessorCount / 2);

            var psi = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = Path.GetDirectoryName(script)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add("--corrections"); psi.ArgumentList.Add(_store.CorrectionsDirectory);
            psi.ArgumentList.Add("--out"); psi.ArgumentList.Add(RunDir);
            psi.ArgumentList.Add("--status"); psi.ArgumentList.Add(StatusPath);
            psi.ArgumentList.Add("--epochs"); psi.ArgumentList.Add(epochs.ToString());
            psi.ArgumentList.Add("--synth"); psi.ArgumentList.Add(synth.ToString());
            psi.ArgumentList.Add("--threads"); psi.ArgumentList.Add(threads.ToString());
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUNBUFFERED"] = "1";

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, _) => { };   // bắt buộc đọc để ống không đầy; chi tiết nằm trong file trạng thái
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

            lock (_lock)
            {
                if (_cancelled) { _starting = false; p.Dispose(); return; }
                _proc = p;   // gán TRƯỚC khi chạy: nếu thoát cực nhanh thì OnExited vẫn thấy đúng đối tượng
                _starting = false;
            }

            try
            {
                p.Start();
            }
            catch (Exception ex)
            {
                lock (_lock) { _proc = null; _startError = $"Không chạy được Python ('{python}'): {ex.Message}"; }
                p.Dispose();
                return;
            }

            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; }   // tiến trình con kế thừa mức ưu tiên thấp
            catch { /* khong dat duoc thi van chay */ }

            _logger.LogInformation("Train OCR: bat dau ({Python}, {Epochs} epoch, {Synth} bien gia lap, {Threads} luong).", python, epochs, synth, threads);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Train OCR: loi khi khoi dong.");
            lock (_lock) { _starting = false; _startError = "Lỗi khi khởi động huấn luyện: " + ex.Message; }
        }
    }

    public bool Cancel()
    {
        Process? p;
        bool starting;
        lock (_lock)
        {
            p = _proc;
            starting = _starting;
            if (p is null && !starting) return false;
            _cancelled = true;
        }
        if (p is not null)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* da thoat */ }
        }
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
            _logger.LogWarning(ex, "Train OCR: loi khi hoan tat.");
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
        var script = ResolveScript();
        var st = ReadStatus();
        bool running, starting, cancelled;
        InstallInfo? install;
        string[] errLines;
        string? startError;
        lock (_lock)
        {
            running = _proc is not null;
            starting = _starting;
            cancelled = _cancelled;
            install = _install;
            errLines = _stderr.ToArray();
            startError = _startError;
        }

        bool busy = running || starting;
        bool finished = st is JsonElement r && IsFinished(r);
        bool canInstall = !busy && !cancelled && st is JsonElement r2 && IsFinishedOk(r2)
                          && TryGetCandidate(r2, out _) && install is not { Ok: true };
        // Có file trạng thái nhưng chưa "xong/lỗi" mà không còn tiến trình: bị dừng giữa chừng (tắt máy, tắt ứng dụng...)
        bool interrupted = !busy && !cancelled && st is not null && !finished;

        return new
        {
            scriptFound = script is not null,
            running,
            starting,
            cancelled,
            interrupted,
            startError,
            status = st,
            installed = install,
            canInstall,
            currentModel = _reloader.CurrentOcrModelPath,
            stderr = (interrupted || (!busy && st is null && errLines.Length > 0)) ? string.Join("\n", errLines.TakeLast(8)) : null,
            corrections = _store.List().Count,
        };
    }

    /// <summary>Xoá kết quả lần train gần nhất. Không đụng tới model đang dùng, các mẫu đã lưu và các bản sao lưu. Không xoá được khi đang train.</summary>
    public (bool Ok, string Message) ClearResult()
    {
        lock (_lock)
        {
            if (_proc is not null || _starting) return (false, "Đang huấn luyện — hãy đợi xong hoặc huỷ train trước khi xoá kết quả.");
            _cancelled = false;
            _install = null;
            _startError = null;
            _stderr.Clear();
        }
        try { if (File.Exists(StatusPath)) File.Delete(StatusPath); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Train OCR: khong xoa duoc ket qua.");
            return (false, "Không xoá được tệp kết quả train: " + ex.Message);
        }
        return (true, "Đã xoá kết quả train gần nhất.");
    }

    // ------------------------------------------------------------------ cài model
    /// <summary>Người dùng bấm "Vẫn cài bản mới" (model mới chưa đủ tin cậy nên không được tự cài).</summary>
    public (bool Ok, string Message) InstallLatest()
    {
        lock (_lock)
        {
            if (_proc is not null || _starting) return (false, "Đang huấn luyện.");
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

            var runtime = _reloader.CurrentOcrModelPath;
            if (string.IsNullOrEmpty(runtime)) runtime = Path.Combine(AppContext.BaseDirectory, "models", "plate_ocr.onnx");
            targets.Add(Path.GetFullPath(runtime));

            var srcDir = Path.Combine(_env.ContentRootPath, "Models");
            var srcModel = Path.GetFullPath(Path.Combine(srcDir, "plate_ocr.onnx"));
            if (Directory.Exists(srcDir) && !targets.Contains(srcModel, StringComparer.OrdinalIgnoreCase))
                targets.Add(srcModel);

            // 1) Nạp model mới ngay từ file ứng viên: nhả file model cũ (Windows không cho ghi đè file đang mở).
            if (!_reloader.ReloadOcr(candidate, out var msg))
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
                return new InstallInfo(false, "Model đã nạp tạm nhưng không ghi được file: " + ex.Message
                    + " — khởi động lại ứng dụng sẽ quay về model cũ.", targets.ToArray(), backups.ToArray(), DateTime.Now);
            }

            // 3) Trỏ lại về file model thật trong thư mục chạy (để lần build/khởi động sau dùng đúng file).
            _reloader.ReloadOcr(targets[0], out _);
            _logger.LogInformation("Train OCR: da cai model OCR moi ({Targets}); ban cu luu: {Backups}",
                string.Join("; ", targets), string.Join("; ", backups));
            return new InstallInfo(true, "Đã cài model OCR mới và nạp lại ngay — không cần khởi động lại. Bản cũ (nếu có) được sao lưu (.bak-" + stamp + ").",
                targets.ToArray(), backups.ToArray(), DateTime.Now);
        }
    }

    // ------------------------------------------------------------------ đọc file trạng thái do Python ghi
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
