using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ParkingGuidance.Web.Services.Anpr;
using ParkingGuidance.Web.Services.Detection;
using ParkingGuidance.Web.Services.PlateTrain;
using ParkingGuidance.Web.Services.Simulation;
using ParkingGuidance.Web.Services.VehicleTrain;

namespace ParkingGuidance.Web.Services.TrainTransfer;

/// <summary>Kết quả Export / Import: <see cref="Lines"/> là từng dòng báo cáo theo phần (ký tự / biển số / xe) để hiển thị.</summary>
public sealed class TransferResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string? Folder { get; set; }
    public List<string> Lines { get; set; } = new();
}

/// <summary>Số liệu 1 phần trong gói train (đọc từ chính các file index.json, không tin manifest).</summary>
public sealed class KindInfo
{
    public string Kind { get; set; } = "";
    public bool Available { get; set; }
    /// <summary>Số mục dữ liệu chính (mẫu ký tự + ảnh hàng chờ / ảnh biển / ảnh xe).</summary>
    public int Items { get; set; }
    /// <summary>Trong đó có bao nhiêu mục chưa có ở máy này (Import sẽ thêm). null khi đang xem dữ liệu của chính máy này.</summary>
    public int? NewItems { get; set; }
    public int Corrections { get; set; }
    public int Inbox { get; set; }
    public bool HasModel { get; set; }
}

/// <summary>
/// Export / Import dữ liệu train (ký tự · biển số · xe) ra / vào một THƯ MỤC do người dùng chỉ định (trên máy chạy ứng dụng).
/// Export tạo 1 gói con <c>ParkingGuidance-train-yyyyMMdd-HHmmss</c> có file <see cref="ManifestName"/>:
/// <code>
///   char/corrections  (index.json + &lt;id&gt;.png)   char/inbox (index.json + ảnh gốc)   char/model/char_cnn.onnx
///   plate/samples     (index.json + ảnh)            plate/model/plate.onnx
///   vehicle/samples   (index.json + ảnh)            vehicle/model/vehicle.onnx
/// </code>
/// Import là GỘP, không xoá gì: mục đã có (cùng mã) được giữ nguyên, chỉ thêm mục mới. Model chỉ thay khi người dùng chọn kèm model
/// (sao lưu bản cũ .bak-… và nạp lại ngay; nạp lỗi thì khôi phục).
/// </summary>
public sealed class TrainTransferService
{
    public const string ManifestName = "parkingguidance-train.json";
    public static readonly string[] Kinds = { "char", "plate", "vehicle" };

    private static readonly Regex IdPattern = new("^[0-9a-f]{16}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly SimulationCorrectionStore _corr;
    private readonly TrainingInboxStore _inbox;
    private readonly PlateTrainStore _plate;
    private readonly VehicleTrainStore _vehicle;
    private readonly ICharModelReloader _charModel;
    private readonly IPlateDetectorModel _plateModel;
    private readonly IVehicleDetectionService _vehicleModel;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<TrainTransferService> _logger;
    private readonly object _modelLock = new();

    public TrainTransferService(SimulationCorrectionStore corr, TrainingInboxStore inbox, PlateTrainStore plate, VehicleTrainStore vehicle,
        ICharModelReloader charModel, IPlateDetectorModel plateModel, IVehicleDetectionService vehicleModel,
        IWebHostEnvironment env, ILogger<TrainTransferService> logger)
    {
        _corr = corr; _inbox = inbox; _plate = plate; _vehicle = vehicle;
        _charModel = charModel; _plateModel = plateModel; _vehicleModel = vehicleModel; _env = env; _logger = logger;
    }

    // ===================================================================== số liệu hiện có trên máy này (cho hộp thoại Export)
    public List<KindInfo> LocalSummary() => new List<KindInfo>
    {
        new KindInfo { Kind = "char", Available = true, Corrections = IndexIds(_corr.CorrectionsDirectory).Count, Inbox = IndexIds(_inbox.InboxDirectory).Count,
            HasModel = !string.IsNullOrEmpty(_charModel.CurrentModelPath) && File.Exists(_charModel.CurrentModelPath) },
        new KindInfo { Kind = "plate", Available = true, Items = IndexIds(_plate.SamplesDirectory).Count, HasModel = File.Exists(_plateModel.CustomModelPath) },
        new KindInfo { Kind = "vehicle", Available = true, Items = IndexIds(_vehicle.SamplesDirectory).Count, HasModel = File.Exists(_vehicleModel.CustomModelPath) },
    }.Select(k => { if (k.Kind == "char") k.Items = k.Corrections + k.Inbox; return k; }).ToList();

    // ===================================================================== EXPORT
    public TransferResult Export(string? folder, IEnumerable<string>? kinds, bool includeModels)
    {
        var res = new TransferResult();
        var want = Normalize(kinds);
        if (want.Count == 0) return Fail(res, "Chưa chọn phần nào để export.");
        if (!TryFullPath(folder, out var root, out var err)) return Fail(res, err);

        string pkg;
        try
        {
            Directory.CreateDirectory(root);
            pkg = Path.Combine(root, "ParkingGuidance-train-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(pkg);
        }
        catch (Exception ex) { return Fail(res, "Không tạo được thư mục đích: " + ex.Message); }

        try
        {
            var manifest = new JsonObject { ["app"] = "ParkingGuidance", ["format"] = 1, ["createdAt"] = DateTime.Now.ToString("s"), ["machine"] = Environment.MachineName };
            var kindsNode = new JsonObject();
            manifest["kinds"] = kindsNode;

            if (want.Contains("char"))
            {
                int c = _corr.WithLock(() => CopyIndexed(_corr.CorrectionsDirectory, Path.Combine(pkg, "char", "corrections")));
                int i = _inbox.WithLock(() => CopyIndexed(_inbox.InboxDirectory, Path.Combine(pkg, "char", "inbox")));
                bool m = includeModels && CopyModel(_charModel.CurrentModelPath, Path.Combine(pkg, "char", "model", "char_cnn.onnx"));
                kindsNode["char"] = new JsonObject { ["corrections"] = c, ["inbox"] = i, ["model"] = m };
                res.Lines.Add($"🔤 Ký tự: {c} mẫu đã gán, {i} ảnh trong hàng chờ{(m ? ", kèm model" : "")}.");
            }
            if (want.Contains("plate"))
            {
                int n = _plate.WithLock(() => CopyIndexed(_plate.SamplesDirectory, Path.Combine(pkg, "plate", "samples")));
                bool m = includeModels && CopyModel(_plateModel.CustomModelPath, Path.Combine(pkg, "plate", "model", "plate.onnx"));
                kindsNode["plate"] = new JsonObject { ["samples"] = n, ["model"] = m };
                res.Lines.Add($"🔖 Biển số: {n} ảnh{(m ? ", kèm model đã train" : "")}.");
            }
            if (want.Contains("vehicle"))
            {
                int n = _vehicle.WithLock(() => CopyIndexed(_vehicle.SamplesDirectory, Path.Combine(pkg, "vehicle", "samples")));
                bool m = includeModels && CopyModel(_vehicleModel.CustomModelPath, Path.Combine(pkg, "vehicle", "model", "vehicle.onnx"));
                kindsNode["vehicle"] = new JsonObject { ["samples"] = n, ["model"] = m };
                res.Lines.Add($"🚗 Xe: {n} ảnh{(m ? ", kèm model đã train" : "")}.");
            }

            File.WriteAllText(Path.Combine(pkg, ManifestName), manifest.ToJsonString(JsonOpts), new UTF8Encoding(false));
            res.Ok = true; res.Folder = pkg;
            res.Message = "Đã export xong vào: " + pkg;
            _logger.LogInformation("Train: da export du lieu ({Kinds}) ra {Folder}", string.Join(",", want), pkg);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Train: export that bai.");
            res.Ok = false; res.Message = "Export lỗi giữa chừng: " + ex.Message + " (thư mục đã tạo: " + pkg + ")";
        }
        return res;
    }

    // ===================================================================== IMPORT: xem trước gói
    public (bool Ok, string Message, string? Folder, DateTime? CreatedAt, List<KindInfo> Kinds) Inspect(string? folder)
    {
        var kinds = new List<KindInfo>();
        if (!TryFullPath(folder, out var root, out var err)) return (false, err, null, null, kinds);
        if (!Directory.Exists(root)) return (false, "Thư mục không tồn tại.", null, null, kinds);
        var mf = Path.Combine(root, ManifestName);
        if (!File.Exists(mf))
            return (false, "Đây không phải thư mục gói train (không thấy " + ManifestName + "). Hãy chọn thư mục “ParkingGuidance-train-…” do Export tạo ra.", root, null, kinds);

        DateTime? created = null;
        try
        {
            var m = JsonNode.Parse(File.ReadAllText(mf, Encoding.UTF8));
            if (m?["app"]?.GetValue<string>() != "ParkingGuidance") return (false, "File gói train không đúng định dạng.", root, null, kinds);
            if (DateTime.TryParse(m["createdAt"]?.GetValue<string>(), out var d)) created = d;
        }
        catch (Exception ex) { return (false, "Không đọc được " + ManifestName + ": " + ex.Message, root, null, kinds); }

        var cDir = Path.Combine(root, "char", "corrections"); var iDir = Path.Combine(root, "char", "inbox");
        var cIds = IndexIds(cDir); var iIds = IndexIds(iDir);
        var localC = IndexIds(_corr.CorrectionsDirectory); var localI = IndexIds(_inbox.InboxDirectory);
        var cNew = cIds.Count(id => !localC.Contains(id));
        var iNew = iIds.Count(id => !localI.Contains(id));
        bool hasChar = cIds.Count + iIds.Count > 0 || File.Exists(Path.Combine(root, "char", "model", "char_cnn.onnx"));
        kinds.Add(new KindInfo { Kind = "char", Available = hasChar, Corrections = cIds.Count, Inbox = iIds.Count, Items = cIds.Count + iIds.Count, NewItems = cNew + iNew,
            HasModel = File.Exists(Path.Combine(root, "char", "model", "char_cnn.onnx")) });

        var pIds = IndexIds(Path.Combine(root, "plate", "samples")); var localP = IndexIds(_plate.SamplesDirectory);
        bool pModel = File.Exists(Path.Combine(root, "plate", "model", "plate.onnx"));
        kinds.Add(new KindInfo { Kind = "plate", Available = pIds.Count > 0 || pModel, Items = pIds.Count, NewItems = pIds.Count(id => !localP.Contains(id)), HasModel = pModel });

        var vIds = IndexIds(Path.Combine(root, "vehicle", "samples")); var localV = IndexIds(_vehicle.SamplesDirectory);
        bool vModel = File.Exists(Path.Combine(root, "vehicle", "model", "vehicle.onnx"));
        kinds.Add(new KindInfo { Kind = "vehicle", Available = vIds.Count > 0 || vModel, Items = vIds.Count, NewItems = vIds.Count(id => !localV.Contains(id)), HasModel = vModel });

        return (true, "", root, created, kinds);
    }

    // ===================================================================== IMPORT
    public TransferResult Import(string? folder, IEnumerable<string>? kinds, bool includeModels)
    {
        var res = new TransferResult();
        var want = Normalize(kinds);
        if (want.Count == 0) return Fail(res, "Chưa chọn phần nào để import.");
        var info = Inspect(folder);
        if (!info.Ok) return Fail(res, info.Message);
        var root = info.Folder!;

        try
        {
            if (want.Contains("char"))
            {
                var (ca, cs, cb) = _corr.WithLock(() => MergeIndexed(Path.Combine(root, "char", "corrections"), _corr.CorrectionsDirectory));
                var (ia, isk, ib) = _inbox.WithLock(() => MergeIndexed(Path.Combine(root, "char", "inbox"), _inbox.InboxDirectory));
                res.Lines.Add($"🔤 Ký tự: thêm {ca} mẫu đã gán (đã có {cs}{Broken(cb)}), thêm {ia} ảnh vào hàng chờ (đã có {isk}{Broken(ib)}).");
                if (includeModels) res.Lines.Add(ImportCharModel(Path.Combine(root, "char", "model", "char_cnn.onnx")));
            }
            if (want.Contains("plate"))
            {
                var (a, s, b) = _plate.WithLock(() => MergeIndexed(Path.Combine(root, "plate", "samples"), _plate.SamplesDirectory));
                res.Lines.Add($"🔖 Biển số: thêm {a} ảnh (đã có {s}{Broken(b)}).");
                if (includeModels) res.Lines.Add(ImportModel("🔖 Model biển số", Path.Combine(root, "plate", "model", "plate.onnx"), _plateModel.CustomModelPath, m => (_plateModel.Reload(out var msg), msg)));
            }
            if (want.Contains("vehicle"))
            {
                var (a, s, b) = _vehicle.WithLock(() => MergeIndexed(Path.Combine(root, "vehicle", "samples"), _vehicle.SamplesDirectory));
                res.Lines.Add($"🚗 Xe: thêm {a} ảnh (đã có {s}{Broken(b)}).");
                if (includeModels) res.Lines.Add(ImportModel("🚗 Model xe", Path.Combine(root, "vehicle", "model", "vehicle.onnx"), _vehicleModel.CustomModelPath, m => (_vehicleModel.Reload(out var msg), msg)));
            }
            res.Ok = true; res.Folder = root; res.Message = "Đã import xong từ: " + root;
            _logger.LogInformation("Train: da import du lieu ({Kinds}) tu {Folder}", string.Join(",", want), root);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Train: import that bai.");
            res.Ok = false; res.Message = "Import lỗi giữa chừng: " + ex.Message + " (phần đã import trước đó vẫn được giữ).";
        }
        return res;
    }

    private static string Broken(int n) => n > 0 ? $", {n} mục thiếu file ảnh nên bỏ qua" : "";

    // ===================================================================== model
    private static bool CopyModel(string? src, string dst)
    {
        if (string.IsNullOrEmpty(src) || !File.Exists(src)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, true);
        return true;
    }

    /// <summary>Model biển số / xe: sao lưu bản cũ (.bak-…), chép bản mới vào chỗ, nạp lại; nạp lỗi thì khôi phục.</summary>
    private string ImportModel(string label, string src, string target, Func<string, (bool Ok, string Msg)> reload)
    {
        if (!File.Exists(src)) return $"{label}: gói không có model — bỏ qua.";
        lock (_modelLock)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string? backup = null;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target)) { backup = target + ".bak-" + stamp; File.Move(target, backup, true); }
            File.Copy(src, target, true);
            var (ok, msg) = reload(target);
            if (ok) return $"{label}: đã thay và nạp lại ngay{(backup is null ? "" : " (bản cũ lưu: " + Path.GetFileName(backup) + ")")}.";

            File.Delete(target);
            if (backup is not null) File.Move(backup, target, true);
            reload(target);
            return $"{label}: KHÔNG nạp được model trong gói ({msg}) — giữ nguyên model đang dùng.";
        }
    }

    /// <summary>Model ký tự: cùng cách cài của tab Mô phỏng — nạp thử từ file trong gói trước (nhả file cũ), rồi sao lưu + ghi đè file đang chạy, nạp lại.</summary>
    private string ImportCharModel(string src)
    {
        const string label = "🔤 Model ký tự";
        if (!File.Exists(src)) return $"{label}: gói không có model — bỏ qua.";
        lock (_modelLock)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var targets = new List<string>();
            var runtime = _charModel.CurrentModelPath;
            if (!string.IsNullOrEmpty(runtime)) targets.Add(Path.GetFullPath(runtime));
            var srcDir = Path.Combine(_env.ContentRootPath, "Models");
            var srcModel = Path.GetFullPath(Path.Combine(srcDir, "char_cnn.onnx"));
            if (Directory.Exists(srcDir) && !targets.Contains(srcModel, StringComparer.OrdinalIgnoreCase)) targets.Add(srcModel);
            if (targets.Count == 0) return $"{label}: không xác định được nơi cài model — bỏ qua.";

            if (!_charModel.Reload(src, out var msg)) return $"{label}: KHÔNG nạp được model trong gói ({msg}) — giữ nguyên model đang dùng.";
            try
            {
                foreach (var t in targets)
                {
                    if (File.Exists(t)) File.Copy(t, t + ".bak-" + stamp, true);
                    Directory.CreateDirectory(Path.GetDirectoryName(t)!);
                    File.Copy(src, t, true);
                }
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrEmpty(runtime) && File.Exists(runtime)) _charModel.Reload(runtime, out _);
                return $"{label}: không ghi được file model ({ex.Message}) — giữ nguyên model cũ.";
            }
            _charModel.Reload(targets[0], out _);
            return $"{label}: đã thay và nạp lại ngay (bản cũ lưu .bak-{stamp}).";
        }
    }

    // ===================================================================== sao chép / gộp thư mục kiểu "index.json + <id>.*"
    private static HashSet<string> IndexIds(string dir)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in ReadIndex(dir)) { var id = IdOf(n); if (id is not null) set.Add(id); }
        return set;
    }

    private static JsonArray ReadIndex(string dir)
    {
        try
        {
            var p = Path.Combine(dir, "index.json");
            if (!File.Exists(p)) return new JsonArray();
            return JsonNode.Parse(File.ReadAllText(p, Encoding.UTF8)) as JsonArray ?? new JsonArray();
        }
        catch { return new JsonArray(); }
    }

    private static string? IdOf(JsonNode? n)
    {
        if (n is not JsonObject o) return null;
        string? id = null;
        foreach (var kv in o)
            if (string.Equals(kv.Key, "id", StringComparison.OrdinalIgnoreCase))
            {
                if (kv.Value is JsonValue jv && jv.TryGetValue<string>(out var text)) id = text;
                break;
            }
        return id is not null && IdPattern.IsMatch(id) ? id : null;      // chặn id lạ (không cho thoát khỏi thư mục)
    }

    /// <summary>Danh sách file theo mã (phần tên trước dấu chấm đầu tiên): "ab12….png", "ab12….thumb.jpg" → mã "ab12…". Bỏ file .tmp.</summary>
    private static ILookup<string, string> FilesById(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && !string.Equals(Path.GetFileName(f), "index.json", StringComparison.OrdinalIgnoreCase))
                .ToLookup(f => Path.GetFileName(f).Split('.')[0], StringComparer.Ordinal)
            : Enumerable.Empty<string>().ToLookup(x => x);

    /// <summary>Export: chép index.json + mọi file của các mục có trong index. Trả về số mục. Gọi khi đang giữ khoá của kho.</summary>
    private static int CopyIndexed(string srcDir, string dstDir)
    {
        var index = ReadIndex(srcDir);
        if (index.Count == 0) return 0;
        Directory.CreateDirectory(dstDir);
        var files = FilesById(srcDir);
        int n = 0;
        foreach (var node in index)
        {
            var id = IdOf(node); if (id is null) continue;
            foreach (var f in files[id]) File.Copy(f, Path.Combine(dstDir, Path.GetFileName(f)), true);
            n++;
        }
        File.Copy(Path.Combine(srcDir, "index.json"), Path.Combine(dstDir, "index.json"), true);
        return n;
    }

    /// <summary>Import: thêm các mục (theo mã) chưa có ở <paramref name="dstDir"/>; mục đã có giữ nguyên. Gọi khi đang giữ khoá của kho.</summary>
    private static (int Added, int Skipped, int Broken) MergeIndexed(string srcDir, string dstDir)
    {
        var src = ReadIndex(srcDir);
        if (src.Count == 0) return (0, 0, 0);
        var dst = ReadIndex(dstDir);
        var have = new HashSet<string>(dst.Select(IdOf).Where(i => i is not null).Select(i => i!), StringComparer.Ordinal);
        var files = FilesById(srcDir);
        Directory.CreateDirectory(dstDir);

        int added = 0, skipped = 0, broken = 0;
        foreach (var node in src)
        {
            var id = IdOf(node); if (id is null || node is null) continue;
            if (have.Contains(id)) { skipped++; continue; }
            var list = files[id].ToList();
            // phải có ít nhất 1 file ảnh chính (không tính thumb) thì mục mới dùng được
            if (!list.Any(f => !Path.GetFileName(f).Contains(".thumb.", StringComparison.OrdinalIgnoreCase))) { broken++; continue; }
            foreach (var f in list) File.Copy(f, Path.Combine(dstDir, Path.GetFileName(f)), true);
            dst.Add(JsonNode.Parse(node.ToJsonString()));
            have.Add(id); added++;
        }
        if (added > 0)
        {
            var idx = Path.Combine(dstDir, "index.json");
            var tmp = idx + ".tmp";
            File.WriteAllText(tmp, dst.ToJsonString(JsonOpts), new UTF8Encoding(false));
            File.Move(tmp, idx, true);
        }
        return (added, skipped, broken);
    }

    // ===================================================================== tiện ích
    private static HashSet<string> Normalize(IEnumerable<string>? kinds) =>
        new((kinds ?? Array.Empty<string>()).Select(k => (k ?? "").Trim().ToLowerInvariant()).Where(k => Kinds.Contains(k)));

    public static bool TryFullPath(string? input, out string full, out string error)
    {
        full = ""; error = "";
        if (string.IsNullOrWhiteSpace(input)) { error = "Chưa chọn thư mục."; return false; }
        try
        {
            var p = input.Trim().Trim('"');
            if (!Path.IsPathRooted(p)) { error = "Hãy nhập đường dẫn đầy đủ (vd D:\\Backup\\Train)."; return false; }
            full = Path.GetFullPath(p);
            return true;
        }
        catch (Exception ex) { error = "Đường dẫn không hợp lệ: " + ex.Message; return false; }
    }

    private static TransferResult Fail(TransferResult r, string msg) { r.Ok = false; r.Message = msg; return r; }
}
