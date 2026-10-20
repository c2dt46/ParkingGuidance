using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenCvSharp;
using ParkingGuidance.Web.Services.Persistence;

namespace ParkingGuidance.Web.Services.PlateTrain;

/// <summary>1 khung biển số đã gán nhãn trên ảnh (toạ độ theo PIXEL của ảnh gốc). Chỉ có 1 lớp duy nhất: biển số.</summary>
public sealed class PlateBox
{
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
}

/// <summary>1 ảnh trong bộ dữ liệu huấn luyện dò biển số (ảnh cả khung hình hoặc ảnh xe đã cắt).</summary>
public sealed class PlateSample
{
    public string Id { get; set; } = "";
    public string ImageName { get; set; } = "";
    public string Ext { get; set; } = ".jpg";
    public int Width { get; set; }
    public int Height { get; set; }
    public List<PlateBox> Boxes { get; set; } = new();
    /// <summary>Người dùng đã kiểm tra khung. CHỈ ảnh đã duyệt mới được đưa vào huấn luyện (ảnh đã duyệt không có khung nào = ảnh "không có biển", giúp giảm báo nhầm).</summary>
    public bool Reviewed { get; set; }
    public DateTime Added { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Bộ dữ liệu huấn luyện dò biển số, lưu trên đĩa tại <c>{DataDirectory}/plate-train/samples</c>:
/// <c>index.json</c> + <c>&lt;id&gt;.jpg|png|bmp|webp</c> (ảnh gốc, đầy đủ độ phân giải) + <c>&lt;id&gt;.thumb.jpg</c>.
/// Id = 16 ký tự hex đầu của SHA-1 nội dung ảnh → thêm cùng 1 ảnh nhiều lần chỉ giữ 1 bản. Không giữ gì trong bộ nhớ ngoài lúc đọc/ghi.
/// Script huấn luyện (tools/train_plate.py) đọc thẳng index.json này.
/// </summary>
public sealed class PlateTrainStore
{
    public const int MaxSamples = 5000;
    public const int MaxBoxesPerImage = 50;
    private const int MaxImageBytes = 30 * 1024 * 1024;
    private const int ThumbWidth = 260;
    private static readonly string[] AllowedExt = { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
    private static readonly Regex IdPattern = new("^[0-9a-f]{16}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly object _lock = new();
    private readonly ILogger<PlateTrainStore> _logger;

    public string RootDirectory { get; }
    public string SamplesDirectory { get; }

    public PlateTrainStore(IPersistenceService persistence, ILogger<PlateTrainStore> logger)
    {
        _logger = logger;
        RootDirectory = Path.Combine(persistence.DataDirectory, "plate-train");
        SamplesDirectory = Path.Combine(RootDirectory, "samples");
    }

    private string IndexPath => Path.Combine(SamplesDirectory, "index.json");
    public static bool IsValidId(string? id) => IdPattern.IsMatch(id ?? "");

    public List<PlateSample> List() { lock (_lock) return Load(); }

    /// <summary>Chạy <paramref name="action"/> khi đang giữ khoá của kho (dùng cho Export/Import tab Train, để không đọc/ghi lẫn với thao tác đang diễn ra).</summary>
    public T WithLock<T>(Func<T> action) { lock (_lock) return action(); }

    public PlateSample? Get(string id)
    {
        if (!IsValidId(id)) return null;
        lock (_lock) return Load().FirstOrDefault(s => s.Id == id);
    }

    public string? ImagePath(string id)
    {
        var s = Get(id);
        return s is null ? null : Path.Combine(SamplesDirectory, s.Id + s.Ext);
    }

    public string? ThumbPath(string id)
    {
        if (!IsValidId(id)) return null;
        var p = Path.Combine(SamplesDirectory, id + ".thumb.jpg");
        return File.Exists(p) ? p : null;
    }

    /// <summary>Thêm 1 ảnh. Ảnh đã có (cùng nội dung) thì trả về bản cũ, Existing = true.</summary>
    public (PlateSample? Sample, bool Existing, string? Error) Add(string fileName, byte[] bytes)
    {
        if (bytes.Length == 0) return (null, false, "Tệp rỗng.");
        if (bytes.Length > MaxImageBytes) return (null, false, "Ảnh quá lớn (tối đa 30 MB).");

        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        if (!AllowedExt.Contains(ext)) return (null, false, "Chỉ nhận ảnh .jpg, .png, .bmp, .webp.");
        if (ext == ".jpeg") ext = ".jpg";

        using var mat = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (mat.Empty()) return (null, false, "Không đọc được ảnh (tệp hỏng hoặc không phải ảnh).");

        var id = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant()[..16];
        lock (_lock)
        {
            var list = Load();
            var existing = list.FirstOrDefault(s => s.Id == id);
            if (existing is not null) return (existing, true, null);
            if (list.Count >= MaxSamples) return (null, false, $"Tối đa {MaxSamples} ảnh.");

            Directory.CreateDirectory(SamplesDirectory);
            File.WriteAllBytes(Path.Combine(SamplesDirectory, id + ext), bytes);

            double k = (double)ThumbWidth / Math.Max(1, mat.Width);
            using var thumb = new Mat();
            Cv2.Resize(mat, thumb, new Size(ThumbWidth, Math.Max(1, (int)(mat.Height * k))));
            Cv2.ImWrite(Path.Combine(SamplesDirectory, id + ".thumb.jpg"), thumb, new ImageEncodingParam(ImwriteFlags.JpegQuality, 80));

            var sample = new PlateSample
            {
                Id = id,
                ImageName = Path.GetFileName(fileName ?? "") is { Length: > 0 } n ? n : id + ext,
                Ext = ext, Width = mat.Width, Height = mat.Height
            };
            list.Add(sample);
            Save(list);
            return (sample, false, null);
        }
    }

    /// <summary>Lưu khung + trạng thái đã duyệt của 1 ảnh (khung được cắt vào trong ảnh; khung quá nhỏ bị bỏ).</summary>
    public (PlateSample? Sample, string? Error) SaveBoxes(string id, IEnumerable<PlateBox> boxes, bool reviewed)
    {
        if (!IsValidId(id)) return (null, "Mã ảnh không hợp lệ.");
        lock (_lock)
        {
            var list = Load();
            var s = list.FirstOrDefault(x => x.Id == id);
            if (s is null) return (null, "Không tìm thấy ảnh.");

            var clean = new List<PlateBox>();
            foreach (var b in boxes ?? Enumerable.Empty<PlateBox>())
            {
                double x1 = Math.Clamp(Math.Min(b.X1, b.X2), 0, s.Width), x2 = Math.Clamp(Math.Max(b.X1, b.X2), 0, s.Width);
                double y1 = Math.Clamp(Math.Min(b.Y1, b.Y2), 0, s.Height), y2 = Math.Clamp(Math.Max(b.Y1, b.Y2), 0, s.Height);
                if (x2 - x1 < 4 || y2 - y1 < 4) continue;
                clean.Add(new PlateBox { X1 = Math.Round(x1, 1), Y1 = Math.Round(y1, 1), X2 = Math.Round(x2, 1), Y2 = Math.Round(y2, 1) });
            }
            if (clean.Count > MaxBoxesPerImage) return (null, $"Tối đa {MaxBoxesPerImage} khung mỗi ảnh.");

            s.Boxes = clean;
            s.Reviewed = reviewed;
            Save(list);
            return (s, null);
        }
    }

    public bool Delete(string id)
    {
        if (!IsValidId(id)) return false;
        lock (_lock)
        {
            var list = Load();
            var s = list.FirstOrDefault(x => x.Id == id);
            if (s is null) return false;
            list.Remove(s);
            Save(list);
            TryDelete(Path.Combine(SamplesDirectory, s.Id + s.Ext));
            TryDelete(Path.Combine(SamplesDirectory, s.Id + ".thumb.jpg"));
            return true;
        }
    }

    public int Clear()
    {
        lock (_lock)
        {
            var list = Load();
            foreach (var s in list)
            {
                TryDelete(Path.Combine(SamplesDirectory, s.Id + s.Ext));
                TryDelete(Path.Combine(SamplesDirectory, s.Id + ".thumb.jpg"));
            }
            Save(new List<PlateSample>());
            return list.Count;
        }
    }

    private List<PlateSample> Load()
    {
        try
        {
            if (!File.Exists(IndexPath)) return new();
            return JsonSerializer.Deserialize<List<PlateSample>>(File.ReadAllText(IndexPath), JsonOpts) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Khong doc duoc bo du lieu train bien so.");
            return new();
        }
    }

    private void Save(List<PlateSample> list)
    {
        Directory.CreateDirectory(SamplesDirectory);
        var tmp = IndexPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, JsonOpts));
        File.Move(tmp, IndexPath, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* bo qua */ }
    }
}
