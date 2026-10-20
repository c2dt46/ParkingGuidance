using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Persistence;

namespace ParkingGuidance.Web.Services.Led;

public interface ILedCardService
{
    /// <summary>Đủ 100 card (card chưa cấu hình là card trống, tắt).</summary>
    IReadOnlyList<LedCard> GetAll();
    LedCard Get(int cardNo);
    /// <summary>Dấu vân tay cấu hình hiện tại của card.</summary>
    string GetStamp(int cardNo);
    (LedCard? Saved, string? Error) Save(int cardNo, LedCard card);
    /// <summary>Phát khi 1 card được lưu (tham số = số thứ tự card).</summary>
    event Action<int>? Changed;
}

/// <summary>100 card LED + bảng vùng hiển thị — lưu data/led-cards.json (gộp CardPara + c0..c99 của PGS thành 1 file, chỉ lưu card đã cấu hình).</summary>
public sealed class LedCardService : ILedCardService
{
    private const string StateKey = "led-cards";
    private readonly IPersistenceService _persistence;
    private readonly object _lock = new();
    private volatile LedCard[] _cards;
    private volatile string[] _stamps;

    public event Action<int>? Changed;

    public LedCardService(IPersistenceService persistence)
    {
        _persistence = persistence;
        var cards = Enumerable.Range(1, HdCardCatalog.CardCount).Select(LedCard.Blank).ToArray();
        foreach (var c in _persistence.Load<List<LedCard>>(StateKey) ?? new())
            if (c.CardNo is >= 1 and <= HdCardCatalog.CardCount)
                cards[c.CardNo - 1] = c.Sanitized(c.CardNo);
        _cards = cards;
        _stamps = cards.Select(c => c.ConfigStamp()).ToArray();
    }

    public IReadOnlyList<LedCard> GetAll() => _cards;
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
            if (stamps[cardNo - 1] == _stamps[cardNo - 1]) return (clean, null); // không đổi gì: không ghi đĩa
            _cards = cards;
            _stamps = stamps;
            _persistence.Save(StateKey, cards.Where(IsConfigured).ToList());
        }
        Changed?.Invoke(cardNo);
        return (clean, null);
    }

    private static bool IsConfigured(LedCard c) => c.Para_Enable || c.Areas.Count > 0 || c.Para_IPAddress.Length > 0;
}
