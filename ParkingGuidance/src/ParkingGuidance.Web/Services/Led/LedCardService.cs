using ParkingGuidance.Web.Services.Project;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Persistence;

namespace ParkingGuidance.Web.Services.Led;

public interface ILedCardService
{
    /// <summary>Toàn bộ chỗ chứa (tối đa 100 card; card chưa dùng là card trống, tắt). Dùng cho bộ gửi; giao diện chỉ hiện <see cref="Count"/> card đầu.</summary>
    IReadOnlyList<LedCard> GetAll();
    /// <summary>Số card đang dùng (mặc định 1, tăng khi "Thêm card").</summary>
    int Count { get; }
    /// <summary>Thêm 1 card mới ở cuối (card trống, tắt). Lỗi khi đã đủ số card tối đa.</summary>
    (LedCard? Added, string? Error) AddCard();
    /// <summary>Xóa card <paramref name="cardNo"/>: các card phía sau dồn lên (đánh số lại), tổng số giảm 1; luôn giữ tối thiểu 1 card. Trả về số card mới.</summary>
    (int NewCount, string? Error) DeleteCard(int cardNo);
    LedCard Get(int cardNo);
    /// <summary>Dấu vân tay cấu hình hiện tại của card.</summary>
    string GetStamp(int cardNo);
    (LedCard? Saved, string? Error) Save(int cardNo, LedCard card);
    /// <summary>Phát khi 1 card được lưu (tham số = số thứ tự card).</summary>
    event Action<int>? Changed;
}

/// <summary>Các card LED + bảng vùng hiển thị — lưu data/led-cards.json (gộp CardPara + c0..c99 của PGS thành 1 file). Mặc định 1 card, thêm dần theo nhu cầu (tối đa 100);
/// file lưu card 1..Count nên số card được giữ nguyên sau khi mở lại / chuyển dự án.</summary>
public sealed class LedCardService : ILedCardService, IProjectScoped
{
    private const string StateKey = "led-cards";
    private readonly IPersistenceService _persistence;
    private readonly object _lock = new();
    private volatile LedCard[] _cards;
    private volatile string[] _stamps;
    private volatile int _count = 1;

    public event Action<int>? Changed;

    public LedCardService(IPersistenceService persistence)
    {
        _persistence = persistence;
        var cards = LoadCards(out var count);
        _cards = cards;
        _count = count;
        _stamps = cards.Select(c => c.ConfigStamp()).ToArray();
    }

    /// <summary>Nạp từ file; số card = số thứ tự lớn nhất đã lưu (tối thiểu 1).</summary>
    private LedCard[] LoadCards(out int count)
    {
        var cards = Enumerable.Range(1, HdCardCatalog.CardCount).Select(LedCard.Blank).ToArray();
        count = 1;
        foreach (var c in _persistence.Load<List<LedCard>>(StateKey) ?? new())
            if (c.CardNo is >= 1 and <= HdCardCatalog.CardCount)
            {
                cards[c.CardNo - 1] = c.Sanitized(c.CardNo);
                count = Math.Max(count, c.CardNo);
            }
        return cards;
    }

    /// <summary>Danh sách ghi ra file: card 1..count (kể cả card trống vừa thêm, để không mất khi mở lại).</summary>
    private static List<LedCard> ToPersist(LedCard[] cards, int count) => cards.Take(count).ToList();

    public int Count => _count;
    public IReadOnlyList<LedCard> GetAll() => _cards;

    public (int NewCount, string? Error) DeleteCard(int cardNo)
    {
        int oldCount;
        lock (_lock)
        {
            oldCount = _count;
            if (cardNo < 1 || cardNo > oldCount) return (oldCount, "Card không tồn tại.");
            if (oldCount <= 1) return (oldCount, "Phải giữ lại ít nhất 1 card.");
            var cards = (LedCard[])_cards.Clone();
            var stamps = (string[])_stamps.Clone();
            for (int i = cardNo; i < oldCount; i++)                  // card i+1 -> vị trí i (đánh số lại)
            {
                cards[i - 1] = cards[i].Sanitized(i);
                stamps[i - 1] = cards[i - 1].ConfigStamp();
            }
            cards[oldCount - 1] = LedCard.Blank(oldCount);
            stamps[oldCount - 1] = cards[oldCount - 1].ConfigStamp();
            _cards = cards;
            _stamps = stamps;
            _count = oldCount - 1;
            _persistence.Save(StateKey, ToPersist(cards, _count));
        }
        for (int no = cardNo; no <= oldCount; no++) Changed?.Invoke(no);
        return (oldCount - 1, null);
    }

    public (LedCard? Added, string? Error) AddCard()
    {
        LedCard card;
        lock (_lock)
        {
            if (_count >= HdCardCatalog.CardCount) return (null, $"Đã đủ {HdCardCatalog.CardCount} card (tối đa).");
            _count++;
            card = _cards[_count - 1];
            _persistence.Save(StateKey, ToPersist(_cards, _count));
        }
        return (card, null);
    }
    public LedCard Get(int cardNo) => _cards[Math.Clamp(cardNo, 1, HdCardCatalog.CardCount) - 1];
    public string GetStamp(int cardNo) => _stamps[Math.Clamp(cardNo, 1, HdCardCatalog.CardCount) - 1];

    public (LedCard? Saved, string? Error) Save(int cardNo, LedCard card)
    {
        if (cardNo is < 1 or > HdCardCatalog.CardCount) return (null, "Card số phải từ 1 đến 100.");
        var clean = card.Sanitized(cardNo);
        var error = clean.Validate();
        if (error is not null) return (null, error);

        lock (_lock)
        {
            var cards = (LedCard[])_cards.Clone();
            var stamps = (string[])_stamps.Clone();
            cards[cardNo - 1] = clean;
            stamps[cardNo - 1] = clean.ConfigStamp();
            var grew = cardNo > _count;
            if (grew) _count = cardNo;                                              // lưu card số lớn hơn số card hiện có → mở rộng danh sách
            if (!grew && stamps[cardNo - 1] == _stamps[cardNo - 1]) return (clean, null); // không đổi gì: không ghi đĩa
            _cards = cards;
            _stamps = stamps;
            _persistence.Save(StateKey, ToPersist(cards, _count));
        }
        Changed?.Invoke(cardNo);
        return (clean, null);
    }

    // ---- IProjectScoped: lưu chung / mở dự án khác ----
    public string ProjectPartName => "LED";

    public bool FlushProject()
    {
        lock (_lock) return _persistence.Commit(StateKey, ToPersist(_cards, _count));
    }

    public void ReloadProject()
    {
        var cards = LoadCards(out var count);
        var stamps = cards.Select(c => c.ConfigStamp()).ToArray();

        var changed = new List<int>();
        lock (_lock)
        {
            var old = _stamps;
            for (int i = 0; i < stamps.Length; i++)
                if (old[i] != stamps[i]) changed.Add(i + 1);
            _cards = cards;
            _stamps = stamps;
            _count = count;
        }
        foreach (var no in changed) Changed?.Invoke(no);          // LedDispatcher gửi lại các card có cấu hình khác
    }
}
