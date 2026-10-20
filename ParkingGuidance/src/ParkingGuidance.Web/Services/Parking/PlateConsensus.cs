using System.Text;

namespace ParkingGuidance.Web.Services.Parking;

/// <summary>1 lần đọc biển số từ 1 khung hình. <see cref="Key"/> = chỉ chữ + số, viết hoa; <see cref="Weight"/> = trọng số lần đọc (≈ 1 + độ tin cậy).</summary>
public readonly record struct PlateRead(string Pretty, string Key, double Weight)
{
    public static PlateRead Create(string pretty, float confidence)
    {
        var key = PlateConsensus.KeyOf(pretty);
        return new PlateRead(pretty, key, 1.0 + Math.Clamp(confidence, 0f, 1f));
    }
}

/// <summary>Kết quả hợp nhất nhiều lần đọc: biển số được chọn, tổng điểm ủng hộ và số lần đọc ủng hộ.</summary>
public sealed record PlateConsensusResult(string Pretty, string Key, double Support, int Reads);

/// <summary>
/// Hợp nhất NHIỀU lần đọc biển số (nhiều khung hình của cùng 1 xe đang đỗ) thành 1 kết quả đáng tin hơn.
/// Một lần đọc đơn lẻ của Tesseract hay sai 1-2 ký tự (hoặc rơi mất ký tự cuối); các lần sai thường KHÁC NHAU
/// còn lần đúng thì lặp lại, nên bỏ phiếu theo thời gian loại được phần lớn lỗi ngẫu nhiên.
/// 1) Gom theo chuỗi: chuỗi nào được ≥ 2 lần đọc giống hệt nhau thì thắng (điểm cao nhất).
/// 2) Nếu chưa chuỗi nào lặp lại nhưng có ≥ 3 lần đọc cùng độ dài, lệch ≤ 2 ký tự: bỏ phiếu TỪNG VỊ TRÍ ký tự,
///    chỉ nhận nếu từng vị trí vẫn đúng loại (2 số đầu = số, ký tự đầu seri = chữ, phần cuối = số).
/// </summary>
public static class PlateConsensus
{
    /// <summary>Chỉ giữ chữ + số, viết hoa: "51K-163.27", "51k16327" và "163 27" so khớp được với nhau.</summary>
    public static string KeyOf(string? plate) =>
        new string((plate ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    /// <summary>Số ký tự khác nhau giữa 2 khoá cùng độ dài; khác độ dài → int.MaxValue (không so khớp được).</summary>
    public static int Hamming(string a, string b)
    {
        if (a.Length != b.Length) return int.MaxValue;
        int d = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) d++;
        return d;
    }

    private sealed class Group
    {
        public string Key = "";
        public string Pretty = "";
        public double Score;
        public int Count;
    }

    public static PlateConsensusResult? Resolve(IReadOnlyList<PlateRead> reads)
    {
        if (reads.Count == 0) return null;

        var groups = new Dictionary<string, Group>();
        foreach (var r in reads)
        {
            if (!groups.TryGetValue(r.Key, out var g))
                groups[r.Key] = g = new Group { Key = r.Key, Pretty = r.Pretty };
            g.Score += r.Weight;
            g.Count++;
        }

        var leader = groups.Values.OrderByDescending(g => g.Score).ThenByDescending(g => g.Count).First();
        if (leader.Count >= 2)
            return new PlateConsensusResult(leader.Pretty, leader.Key, leader.Score, leader.Count);

        // Chưa có chuỗi nào lặp lại: thử bỏ phiếu từng vị trí giữa các lần đọc gần giống nhau
        var cluster = reads.Where(r => r.Key.Length == leader.Key.Length && Hamming(r.Key, leader.Key) <= 2).ToList();
        if (cluster.Count >= 3)
        {
            var chars = new char[leader.Key.Length];
            for (int i = 0; i < chars.Length; i++)
            {
                var weights = new Dictionary<char, double>();
                foreach (var r in cluster)
                    weights[r.Key[i]] = weights.GetValueOrDefault(r.Key[i]) + r.Weight;

                char best = leader.Key[i];
                double bestW = weights.GetValueOrDefault(best);
                foreach (var (c, w) in weights)
                    if (w > bestW) { best = c; bestW = w; }
                chars[i] = best;
            }

            var key = new string(chars);
            if (key != leader.Key && StructureOk(leader.Pretty, key))
                return new PlateConsensusResult(ApplyToPretty(leader.Pretty, key), key, cluster.Sum(r => r.Weight), cluster.Count);
        }

        return new PlateConsensusResult(leader.Pretty, leader.Key, leader.Score, leader.Count);
    }

    /// <summary>Mỗi vị trí còn đúng loại ký tự theo cấu trúc biển VN (dựa vào độ dài phần đầu trước dấu '-').</summary>
    private static bool StructureOk(string pretty, string key)
    {
        int dash = pretty.IndexOf('-');
        if (dash < 0) return false;
        int headLen = pretty[..dash].Count(char.IsLetterOrDigit);

        for (int i = 0; i < key.Length; i++)
        {
            char c = key[i];
            bool ok;
            if (i < 2) ok = char.IsDigit(c);                       // mã tỉnh
            else if (i == 2) ok = char.IsLetter(c);                // ký tự đầu của seri
            else if (i < headLen) ok = char.IsLetterOrDigit(c);    // ký tự thứ 2 của seri: chữ hoặc số
            else ok = char.IsDigit(c);                             // dãy số cuối
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>Thay lần lượt các ký tự chữ/số của <paramref name="pretty"/> bằng <paramref name="key"/>, giữ nguyên dấu '-' và '.'.</summary>
    private static string ApplyToPretty(string pretty, string key)
    {
        var sb = new StringBuilder(pretty.Length);
        int k = 0;
        foreach (var ch in pretty)
            sb.Append(char.IsLetterOrDigit(ch) && k < key.Length ? key[k++] : ch);
        return sb.ToString();
    }
}

public enum PlateOutcome
{
    /// <summary>Không đổi biển của phiên (đọc trùng, hoặc lần đọc lạ chưa đủ để tin).</summary>
    Same,
    /// <summary>Cùng xe: biển của phiên được cập nhật sang kết quả hợp nhất tốt hơn.</summary>
    Updated,
    /// <summary>Biển đầu tiên (chỉ 1 lần đọc) nhiều khả năng bị đọc sai: thay ngay trong CÙNG phiên, không tách phiên.</summary>
    ReplacedInPlace,
    /// <summary>Xe khác hẳn (đủ số lần đọc xác nhận): đóng phiên cũ, mở phiên mới.</summary>
    NewSession
}

/// <summary>
/// Theo dõi các lần đọc biển số của 1 phiên đỗ (CHỈ trong bộ nhớ, không lưu xuống đĩa) và quyết định khi nào đổi biển.
/// Không an toàn đa luồng: <see cref="ParkingService"/> luôn gọi trong khoá của nó.
/// </summary>
public sealed class PlateTracker
{
    /// <summary>Số lần đọc trùng khớp để coi biển đã "chốt" — từ đó chỉ kiểm tra lại định kỳ, không đọc OCR mỗi khung.</summary>
    public const int LockReads = 3;
    /// <summary>Số lần đọc liên tiếp ra CÙNG 1 biển lạ mới tin là xe khác.</summary>
    public const int ConfirmNewPlateReads = 2;
    /// <summary>Biển chốt rồi vẫn kiểm tra lại sau mỗi chừng này khung (phát hiện đổi xe không qua trạng thái trống).</summary>
    public const int RecheckEveryFrames = 10;
    /// <summary>Số khung tối đa còn "ép xử lý tiếp" để gom thêm lần đọc khi biển chưa chốt (hết mức thì thôi, tránh tốn tải mãi).</summary>
    public const int MaxFramesWhileUnlocked = 12;
    /// <summary>Biển mới phải hơn biển hiện tại chừng này điểm (≈ 1 lần đọc thêm) mới đổi.</summary>
    private const double SwitchMargin = 0.9;
    private const int MaxReads = 24;
    private const double InPlaceWindowSeconds = 90;

    public string SessionId { get; private set; }
    public PlateTracker(string sessionId) => SessionId = sessionId;

    /// <summary>Gắn tracker (cùng các lần đọc đã gom) sang phiên mới — khi xác nhận xe khác thì giữ lại các lần đọc đã xác nhận biển mới.</summary>
    public void Rebind(string sessionId)
    {
        SessionId = sessionId;
        Frames = 1;
        SkippedSinceRead = 0;
    }

    private readonly List<PlateRead> _reads = new();
    private readonly List<PlateRead> _pending = new();

    /// <summary>Số khung đã xử lý có xe trong ô kể từ khi mở phiên.</summary>
    public int Frames { get; set; }
    public int SkippedSinceRead { get; set; }
    public bool HasPending => _pending.Count > 0;

    public void AddRead(PlateRead r)
    {
        _reads.Add(r);
        if (_reads.Count > MaxReads) _reads.RemoveAt(0);
    }

    /// <summary>Số lần đọc giống hệt <paramref name="key"/>.</summary>
    public int AgreeCount(string key) => _reads.Count(r => r.Key == key);

    /// <summary>Biển đã chốt: ≥ <see cref="LockReads"/> lần đọc giống nhau và không có biển lạ nào đang chờ xác nhận.</summary>
    public bool IsLocked(string currentPlate) => _pending.Count == 0 && AgreeCount(PlateConsensus.KeyOf(currentPlate)) >= LockReads;

    /// <summary>Cùng xe nếu cùng độ dài và lệch ≤ 2 ký tự (OCR hay sai 1-2 ký tự, không tách phiên vì thế).</summary>
    private static bool IsNear(string a, string b) => PlateConsensus.Hamming(a, b) <= 2;

    /// <summary>
    /// Ghi nhận 1 lần đọc mới. <paramref name="immediate"/> = true (ảnh nạp tay, chỉ 1 lần): biển khác hẳn → đổi xe ngay.
    /// <paramref name="newPlate"/> = biển của phiên sau khi xử lý.
    /// </summary>
    public PlateOutcome Observe(PlateRead read, string currentPlate, bool immediate, double sessionAgeSeconds, out string newPlate)
    {
        var curKey = PlateConsensus.KeyOf(currentPlate);
        newPlate = currentPlate;

        if (IsNear(read.Key, curKey))
        {
            _pending.Clear();
            AddRead(read);

            var best = PlateConsensus.Resolve(_reads);
            if (best is not null && best.Key != curKey)
            {
                double curScore = _reads.Where(r => r.Key == curKey).Sum(r => r.Weight);
                if (best.Support > curScore + SwitchMargin)
                {
                    newPlate = best.Pretty;
                    return PlateOutcome.Updated;
                }
            }
            return PlateOutcome.Same;
        }

        // Biển lạ (khác độ dài hoặc lệch ≥ 3 ký tự): chỉ tin khi lặp lại đủ số lần liên tiếp
        if (_pending.Count > 0 && IsNear(_pending[0].Key, read.Key)) _pending.Add(read);
        else { _pending.Clear(); _pending.Add(read); }

        int needed = immediate ? 1 : ConfirmNewPlateReads;
        if (_pending.Count < needed) return PlateOutcome.Same;

        var pend = PlateConsensus.Resolve(_pending)!;
        bool inPlace = !immediate && AgreeCount(curKey) <= 1 && sessionAgeSeconds < InPlaceWindowSeconds;

        _reads.Clear();
        _reads.AddRange(_pending);
        _pending.Clear();
        newPlate = pend.Pretty;
        return inPlace ? PlateOutcome.ReplacedInPlace : PlateOutcome.NewSession;
    }
}
