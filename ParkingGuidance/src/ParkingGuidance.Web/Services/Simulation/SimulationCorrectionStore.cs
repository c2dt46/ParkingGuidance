using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ParkingGuidance.Web.Services.Persistence;

namespace ParkingGuidance.Web.Services.Simulation;

/// <summary>1 mẫu do người dùng cắt + gán ký tự ở tab Train: ảnh biển + biển ĐÚNG + biển hệ thống đã đọc.</summary>
public sealed class CorrectionEntry
{
    public string Id { get; set; } = "";
    public string Key { get; set; } = "";
    public string ImageName { get; set; } = "";
    public string Kind { get; set; } = "vehicle";
    public string Correct { get; set; } = "";
    public string Recognized { get; set; } = "";
    public DateTime Updated { get; set; } = DateTime.UtcNow;
    /// <summary>Dữ liệu phụ do tab Train gửi (JSON: khung cắt trên ảnh gốc + các ô ký tự khoanh tay nếu có) để mở lại ảnh vẫn thấy khung đã vẽ. Trình train dùng phần "chars" khi có.</summary>
    public string Meta { get; set; } = "";
}

/// <summary>
/// Lưu các mẫu "sửa biển" xuống đĩa (thư mục <c>{DataDirectory}/simulation/corrections</c>: <c>index.json</c> + <c>&lt;id&gt;.png</c>),
/// nên còn nguyên khi tắt/mở lại ứng dụng. Không giữ gì trong bộ nhớ ngoài lúc đang đọc/ghi. PlateTrainer đọc đúng thư mục này khi train.
/// </summary>
public sealed class SimulationCorrectionStore
{
    private const string Alphabet = "ABCDEFGHKLMNPRSTUVXYZ0123456789";
    private static readonly Regex IdPattern = new("^[0-9a-f]{16}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly object _lock = new();
    private readonly string _dir;
    private readonly ILogger<SimulationCorrectionStore> _logger;

    public SimulationCorrectionStore(IPersistenceService persistence, ILogger<SimulationCorrectionStore> logger)
    {
        _logger = logger;
        _dir = Path.Combine(persistence.DataDirectory, "simulation", "corrections");
    }

    public string CorrectionsDirectory => _dir;
    public string StatusDirectory => Path.GetDirectoryName(_dir) ?? _dir;
    private string IndexPath => Path.Combine(_dir, "index.json");

    /// <summary>Mã ổn định của 1 khoá mẫu (16 ký tự hex) — cũng là tên file ảnh.</summary>
    public static string IdFor(string key) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..16];

    /// <summary>Chỉ giữ A-Z, 0-9 (viết hoa): "51d-932.40" → "51D93240".</summary>
    public static string NormalizePlate(string? s) =>
        Regex.Replace((s ?? "").ToUpperInvariant(), "[^A-Z0-9]", "");

    /// <summary>Biển VN: 7–9 ký tự chữ/số, không dùng I, J, O, Q, W.</summary>
    public static bool IsValidPlate(string normalized) =>
        normalized.Length is >= 7 and <= 9 && normalized.All(c => Alphabet.Contains(c));

    public List<CorrectionEntry> List()
    {
        lock (_lock) return Load();
    }

    /// <summary>Chạy <paramref name="action"/> khi đang giữ khoá của kho (dùng cho Export/Import tab Train).</summary>
    public T WithLock<T>(Func<T> action) { lock (_lock) return action(); }

    /// <summary>Thêm mới hoặc thay thế mẫu theo <paramref name="key"/>. Ném <see cref="ArgumentException"/> (thông báo tiếng Việt) nếu dữ liệu sai.</summary>
    public CorrectionEntry Upsert(string key, string imageName, string kind, string correct, string? recognized, byte[] png, string? meta = null)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Thiếu khoá mẫu.");
        var norm = NormalizePlate(correct);
        if (!IsValidPlate(norm))
            throw new ArgumentException($"Biển số '{norm}' không hợp lệ: cần 7–9 ký tự chữ/số, biển Việt Nam không dùng I, J, O, Q, W.");
        if (png is null || png.Length == 0) throw new ArgumentException("Thiếu ảnh biển số.");

        var id = IdFor(key);
        lock (_lock)
        {
            Directory.CreateDirectory(_dir);
            WriteAtomic(Path.Combine(_dir, id + ".png"), png);

            var list = Load();
            var entry = list.FirstOrDefault(e => e.Id == id);
            if (entry is null)
            {
                entry = new CorrectionEntry { Id = id, Key = key };
                list.Add(entry);
            }
            entry.ImageName = imageName ?? "";
            entry.Kind = string.IsNullOrWhiteSpace(kind) ? "vehicle" : kind;
            entry.Correct = norm;
            entry.Recognized = NormalizePlate(recognized);
            entry.Updated = DateTime.UtcNow;
            entry.Meta = (meta ?? "").Length <= 8000 ? (meta ?? "") : "";
            Save(list);
            return entry;
        }
    }

    public bool Delete(string key)
    {
        var id = IdFor(key);
        lock (_lock)
        {
            var list = Load();
            var removed = list.RemoveAll(e => e.Id == id) > 0;
            TryDelete(Path.Combine(_dir, id + ".png"));
            if (removed) Save(list);
            return removed;
        }
    }

    /// <summary>Xoá mọi mẫu có khoá bắt đầu bằng <paramref name="prefix"/> (vd mọi vùng biển của 1 ảnh ở tab Train). Trả về số mẫu đã xoá.</summary>
    public int DeleteByKeyPrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return 0;
        lock (_lock)
        {
            var list = Load();
            var gone = list.Where(e => e.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (gone.Count == 0) return 0;
            foreach (var e in gone) { list.Remove(e); TryDelete(Path.Combine(_dir, e.Id + ".png")); }
            Save(list);
            return gone.Count;
        }
    }

    public int Clear()
    {
        lock (_lock)
        {
            var list = Load();
            foreach (var e in list) TryDelete(Path.Combine(_dir, e.Id + ".png"));
            Save(new List<CorrectionEntry>());
            return list.Count;
        }
    }

    /// <summary>Đường dẫn ảnh của mẫu (null nếu id sai/không có file). Kiểm tra id để không đọc file ngoài thư mục.</summary>
    public string? ImagePath(string id)
    {
        if (!IdPattern.IsMatch(id ?? "")) return null;
        var p = Path.Combine(_dir, id + ".png");
        return File.Exists(p) ? p : null;
    }

    // ---- nội bộ (gọi trong lock) ----
    private List<CorrectionEntry> Load()
    {
        try
        {
            if (!File.Exists(IndexPath)) return new List<CorrectionEntry>();
            return JsonSerializer.Deserialize<List<CorrectionEntry>>(File.ReadAllText(IndexPath, Encoding.UTF8), JsonOpts)
                   ?? new List<CorrectionEntry>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Khong doc duoc danh sach mau mo phong {Path}", IndexPath);
            return new List<CorrectionEntry>();
        }
    }

    private void Save(List<CorrectionEntry> list)
    {
        Directory.CreateDirectory(_dir);
        WriteAtomic(IndexPath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(list, JsonOpts)));
    }

    private static void WriteAtomic(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* khong quan trong */ }
    }
}
