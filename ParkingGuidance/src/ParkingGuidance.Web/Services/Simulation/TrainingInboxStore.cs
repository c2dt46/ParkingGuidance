using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenCvSharp;
using ParkingGuidance.Web.Services.Persistence;

namespace ParkingGuidance.Web.Services.Simulation;

/// <summary>1 ảnh đang chờ xử lý ở tab Train: ảnh gốc (đầy đủ độ phân giải) + gợi ý khung biển/biển đọc được từ tab Mô phỏng.</summary>
public sealed class InboxEntry
{
    public string Id { get; set; } = "";
    public string ImageName { get; set; } = "";
    public string Ext { get; set; } = ".jpg";
    /// <summary><c>simulation</c> = thêm từ tab Mô phỏng; <c>upload</c> = nạp thẳng ở tab Train.</summary>
    public string Source { get; set; } = "simulation";
    /// <summary>Chuỗi JSON do trình duyệt gửi (các khung biển + biển đã đọc, toạ độ theo pixel ảnh gốc). Chỉ để gợi ý, server không phân tích.</summary>
    public string Hint { get; set; } = "";
    public bool Done { get; set; }
    public DateTime Added { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// "Hàng chờ" của tab Train: các ảnh người dùng bấm "Thêm vào Train" ở tab Mô phỏng (ảnh không nhận diện được / đọc sai biển).
/// Lưu trên đĩa tại <c>{DataDirectory}/simulation/inbox</c> (<c>index.json</c> + <c>&lt;id&gt;.jpg|png|bmp|webp</c> + <c>&lt;id&gt;.thumb.jpg</c>),
/// nên còn nguyên sau khi tắt/mở ứng dụng. Id = 16 ký tự hex đầu của SHA-1 nội dung ảnh → thêm cùng 1 ảnh nhiều lần chỉ giữ 1 bản.
/// Không giữ gì trong bộ nhớ ngoài lúc đang đọc/ghi.
/// </summary>
public sealed class TrainingInboxStore
{
    private static readonly Regex IdPattern = new("^[0-9a-f]{16}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private const int MaxHintChars = 20_000;
    private const int ThumbSize = 260;

    private readonly object _lock = new();
    private readonly string _dir;
    private readonly ILogger<TrainingInboxStore> _logger;

    public TrainingInboxStore(IPersistenceService persistence, ILogger<TrainingInboxStore> logger)
    {
        _logger = logger;
        _dir = Path.Combine(persistence.DataDirectory, "simulation", "inbox");
    }

    private string IndexPath => Path.Combine(_dir, "index.json");

    /// <summary>Thư mục hàng chờ (index.json + ảnh gốc).</summary>
    public string InboxDirectory => _dir;

    /// <summary>Chạy <paramref name="action"/> khi đang giữ khoá của kho (dùng cho Export/Import tab Train).</summary>
    public T WithLock<T>(Func<T> action) { lock (_lock) return action(); }

    public static bool IsValidId(string? id) => IdPattern.IsMatch(id ?? "");

    public List<InboxEntry> List()
    {
        lock (_lock) return Load();
    }

    public InboxEntry? Get(string id)
    {
        if (!IsValidId(id)) return null;
        lock (_lock) return Load().FirstOrDefault(e => e.Id == id);
    }

    /// <summary>Thêm ảnh (hoặc cập nhật gợi ý nếu ảnh đã có). Ném <see cref="ArgumentException"/> (thông báo tiếng Việt) nếu dữ liệu sai.</summary>
    public (InboxEntry Entry, bool IsNew) Add(string? name, byte[] data, string? hint, string? source)
    {
        if (data is null || data.Length == 0) throw new ArgumentException("Thiếu ảnh.");
        var ext = DetectExtension(data) ?? throw new ArgumentException("Định dạng ảnh không hỗ trợ (chỉ nhận jpg, png, bmp, webp).");
        var id = Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant()[..16];
        var cleanHint = (hint ?? "").Trim();
        if (cleanHint.Length > MaxHintChars) cleanHint = "";

        lock (_lock)
        {
            Directory.CreateDirectory(_dir);
            var list = Load();
            var entry = list.FirstOrDefault(e => e.Id == id);
            bool isNew = entry is null;
            if (entry is null)
            {
                entry = new InboxEntry { Id = id, Ext = ext, Added = DateTime.UtcNow };
                list.Add(entry);
            }
            var path = Path.Combine(_dir, id + entry.Ext);
            if (!File.Exists(path)) WriteAtomic(path, data);

            if (!string.IsNullOrWhiteSpace(name)) entry.ImageName = name.Trim();
            else if (string.IsNullOrEmpty(entry.ImageName)) entry.ImageName = id + entry.Ext;
            if (!string.IsNullOrEmpty(cleanHint)) entry.Hint = cleanHint;
            entry.Source = string.Equals(source, "upload", StringComparison.OrdinalIgnoreCase) ? "upload" : "simulation";
            Save(list);
            return (entry, isNew);
        }
    }

    public bool SetDone(string id, bool done)
    {
        if (!IsValidId(id)) return false;
        lock (_lock)
        {
            var list = Load();
            var e = list.FirstOrDefault(x => x.Id == id);
            if (e is null) return false;
            if (e.Done != done) { e.Done = done; Save(list); }
            return true;
        }
    }

    public bool Delete(string id)
    {
        if (!IsValidId(id)) return false;
        lock (_lock)
        {
            var list = Load();
            var e = list.FirstOrDefault(x => x.Id == id);
            if (e is null) return false;
            list.Remove(e);
            RemoveFiles(e);
            Save(list);
            return true;
        }
    }

    /// <summary>Xoá các ảnh đã xử lý xong khỏi hàng chờ (mẫu đã lưu không bị ảnh hưởng). Trả về số ảnh đã xoá.</summary>
    public int ClearDone()
    {
        lock (_lock)
        {
            var list = Load();
            var done = list.Where(e => e.Done).ToList();
            foreach (var e in done) { list.Remove(e); RemoveFiles(e); }
            if (done.Count > 0) Save(list);
            return done.Count;
        }
    }

    /// <summary>Đường dẫn ảnh gốc (null nếu id sai/không có file).</summary>
    public string? ImagePath(string id)
    {
        if (!IsValidId(id)) return null;
        lock (_lock)
        {
            var e = Load().FirstOrDefault(x => x.Id == id);
            if (e is null) return null;
            var p = Path.Combine(_dir, id + e.Ext);
            return File.Exists(p) ? p : null;
        }
    }

    public static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => "image/jpeg"
    };

    /// <summary>Ảnh thu nhỏ (JPEG, cạnh dài ≤ 260 px) cho danh sách; tạo lần đầu rồi lưu lại. Null nếu không đọc được ảnh.</summary>
    public byte[]? Thumbnail(string id)
    {
        var src = ImagePath(id);
        if (src is null) return null;
        var thumb = Path.Combine(_dir, id + ".thumb.jpg");
        try
        {
            if (File.Exists(thumb)) return File.ReadAllBytes(thumb);
            using var m = Cv2.ImRead(src, ImreadModes.Color);
            if (m.Empty()) return null;
            double f = (double)ThumbSize / Math.Max(m.Width, m.Height);
            using var small = new Mat();
            if (f < 1) Cv2.Resize(m, small, new Size(), f, f, InterpolationFlags.Area);
            else m.CopyTo(small);
            Cv2.ImEncode(".jpg", small, out var buf, new ImageEncodingParam(ImwriteFlags.JpegQuality, 80));
            lock (_lock) { WriteAtomic(thumb, buf); }
            return buf;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Khong tao duoc anh thu nho cho {Id}", id);
            return null;
        }
    }

    // ---- nội bộ ----
    private static string? DetectExtension(byte[] d)
    {
        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return ".png";
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return ".jpg";
        if (d.Length >= 2 && d[0] == 0x42 && d[1] == 0x4D) return ".bmp";
        if (d.Length >= 12 && d[0] == 0x52 && d[1] == 0x49 && d[2] == 0x46 && d[3] == 0x46 && d[8] == 0x57 && d[9] == 0x45 && d[10] == 0x42 && d[11] == 0x50) return ".webp";
        return null;
    }

    private void RemoveFiles(InboxEntry e)
    {
        TryDelete(Path.Combine(_dir, e.Id + e.Ext));
        TryDelete(Path.Combine(_dir, e.Id + ".thumb.jpg"));
    }

    private List<InboxEntry> Load()
    {
        try
        {
            if (!File.Exists(IndexPath)) return new List<InboxEntry>();
            return JsonSerializer.Deserialize<List<InboxEntry>>(File.ReadAllText(IndexPath, Encoding.UTF8), JsonOpts)
                   ?? new List<InboxEntry>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Khong doc duoc hang cho train {Path}", IndexPath);
            return new List<InboxEntry>();
        }
    }

    private void Save(List<InboxEntry> list)
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
