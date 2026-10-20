using System.Net.NetworkInformation;
using System.Threading.Channels;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Parking;

namespace ParkingGuidance.Web.Services.Led;

public interface ILedDispatcher
{
    string TransportName { get; }
    string? TransportWarning { get; }
    IReadOnlyList<LedCardStatus> GetStatuses();

    /// <summary>Nút "Gửi tới card" của PGS: ép gửi lại toàn bộ nội dung (chữ, ảnh, số) cho card.</summary>
    void ForceSend(int cardNo);

    /// <summary>Kiểm tra card có phản hồi không (nhãn "Kết nối").</summary>
    Task<LedResult> PingAsync(int cardNo, CancellationToken ct);
}

/// <summary>
/// Đẩy dữ liệu lên 100 card LED theo nguyên lý SendtoDHCard.HDMain của PGS:
///   đọc cấu hình card → tổng hợp dữ liệu (thanh ghi A/B/C của các vùng Number) → so với dữ liệu đã gửi thành công → khác thì dựng lại màn hình và gửi;
///   gửi thành công mới ghi nhận "đã gửi"; chữ/ảnh chỉ gửi lần đầu hoặc khi bấm "Gửi tới card".
///
/// Khác PGS (tối ưu), không đổi cách người dùng thao tác:
///  • Chạy theo SỰ KIỆN (bãi xe đổi / thanh ghi đổi / lưu cấu hình card) thay vì quét cả 100 card liên tục; gộp sự kiện dồn dập (debounce).
///  • Đọc giá trị biến MS / MC / project tag mà các vùng Number đang dùng (xem <see cref="LedDataService"/>), không dựng chuỗi/ghép lại cho từng card khi chưa cần.
///  • Giãn cách tối thiểu mỗi card; card mất kết nối thử lại lùi dần (2ⁿ giây, tối đa MaxBackoffSeconds, có nhiễu) và không chặn card khác
///    (PGS thử lại mỗi vòng quét và làm chậm cả hàng đợi).
///  • SDK chạy ở tiến trình riêng, mọi thao tác tuần tự qua 1 khoá (SDK có 1 màn hình toàn cục).
///  • (Tuỳ chọn) gửi lại toàn bộ định kỳ <c>Led:ResyncMinutes</c> (mặc định tắt = như PGS).
/// </summary>
public sealed class LedDispatcher : BackgroundService, ILedDispatcher
{
    private sealed class Rt
    {
        public string? SentKey;
        public string? SentStamp;
        public bool Force = true;          // lần khởi động đầu tiên gửi tất cả (như SrartupCarrd của PGS)
        public bool Dirty;
        public LedState State = LedState.Pending;
        public int Attempts;
        public DateTime NextAttemptUtc = DateTime.MinValue;
        public DateTime LastAttemptUtc = DateTime.MinValue;
        public DateTime? LastSentUtc;
        public int? LastSendMs;
        public string? LastError;
        public long SendCount;
    }

    private readonly IParkingService _parking;
    private readonly ILedCardService _cards;
    private readonly ILedDataService _data;
    private readonly ILedTransport _transport;
    private readonly string _dataDir;
    private readonly ILogger<LedDispatcher> _logger;

    private readonly TimeSpan _debounce, _minInterval, _resync, _maxBackoff, _startupDelay, _poll;
    private readonly bool _precheckPing;   // ping nhanh card truoc khi goi SDK (chi khi dung bridge that)
    private readonly int _pingTimeoutMs;
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly object _stateLock = new();
    private readonly Rt[] _rt = Enumerable.Range(0, HdCardCatalog.CardCount).Select(_ => new Rt()).ToArray();
    private DateTime _nextResyncUtc;

    public LedDispatcher(
        IParkingService parking, ILedCardService cards, ILedDataService data, ILedTransport transport,
        Persistence.IPersistenceService persistence, IConfiguration cfg, ILogger<LedDispatcher> logger)
    {
        _parking = parking;
        _cards = cards;
        _data = data;
        _transport = transport;
        _dataDir = persistence.DataDirectory;
        _logger = logger;

        _debounce = TimeSpan.FromMilliseconds(Math.Clamp(cfg.GetValue("Led:DebounceMs", 400), 0, 10_000));
        _minInterval = TimeSpan.FromMilliseconds(Math.Clamp(cfg.GetValue("Led:MinIntervalMs", 1000), 0, 600_000));
        _resync = TimeSpan.FromMinutes(Math.Clamp(cfg.GetValue("Led:ResyncMinutes", 0), 0, 1440));
        _maxBackoff = TimeSpan.FromSeconds(Math.Clamp(cfg.GetValue("Led:MaxBackoffSeconds", 300), 5, 86_400));
        _startupDelay = TimeSpan.FromSeconds(Math.Clamp(cfg.GetValue("Led:StartupDelaySeconds", 3), 0, 120));
        // Bien MS / MC / project tag khong phat su kien khi doi gia tri -> vong lap tu doc lai moi chu ky nay (Led:PollMs) va chi gui khi so hien thi doi.
        _poll = TimeSpan.FromMilliseconds(Math.Clamp(cfg.GetValue("Led:PollMs", 1000), 200, 60_000));
        _nextResyncUtc = _resync > TimeSpan.Zero ? DateTime.UtcNow + _resync : DateTime.MaxValue;
        // Card mat mang: SDK co the cho het timeout (15s) roi bridge bi kill -> moi card sau do trong luot gui bi tre theo.
        // Ping nhanh truoc de loai card mat mang ngay (<1s) ma KHONG goi SDK. Mang chan ICMP thi dat Led:PrecheckPing = false.
        _precheckPing = transport is BridgeLedTransport && cfg.GetValue("Led:PrecheckPing", true);
        _pingTimeoutMs = Math.Clamp(cfg.GetValue("Led:PingTimeoutMs", 800), 100, 5000);

        _parking.StateChanged += Notify;
        _cards.Changed += OnCardChanged;
    }

    public string TransportName => _transport.Name;
    public string? TransportWarning => _transport.Warning;

    private void Notify() => _signal.Writer.TryWrite(true);
    private void OnCardChanged(int _) => Notify();

    // -----------------------------------------------------------------------------------------
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(_startupDelay, ct); } catch (OperationCanceledException) { return; }
        Notify();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    wait.CancelAfter(NextWakeDelay());
                    try
                    {
                        if (await _signal.Reader.WaitToReadAsync(wait.Token))
                        {
                            _signal.Reader.TryRead(out _);
                            if (_debounce > TimeSpan.Zero) await Task.Delay(_debounce, ct); // gộp loạt thay đổi dồn dập
                            _signal.Reader.TryRead(out _);
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* đến hạn thử lại / gửi lại định kỳ */ }
                }

                if (DateTime.UtcNow >= _nextResyncUtc) ForceAll();
                var snapshot = Recompute();
                await SendDueAsync(snapshot, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Loi vong lap LED; thu lai sau 5 giay.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private TimeSpan NextWakeDelay()
    {
        var next = _nextResyncUtc;
        lock (_stateLock)
            foreach (var r in _rt)
                if (r.Dirty)
                {
                    var due = Max(r.NextAttemptUtc, r.LastAttemptUtc + _minInterval);
                    if (due < next) next = due;
                }
        var d = next - DateTime.UtcNow;
        if (d < TimeSpan.FromMilliseconds(50)) d = TimeSpan.FromMilliseconds(50);
        return d > _poll ? _poll : d;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private void ForceAll()
    {
        lock (_stateLock) foreach (var r in _rt) r.Force = true;
        _nextResyncUtc = _resync > TimeSpan.Zero ? DateTime.UtcNow + _resync : DateTime.MaxValue;
    }

    private static bool Eligible(LedCard c) => c.Para_Enable && c.Areas.Count > 0 && LedCard.IsValidIp(c.Para_IPAddress);

    /// <summary>Tổng hợp dữ liệu và đánh dấu card nào cần gửi (khoá dữ liệu khác lần đã gửi thành công, hoặc bị ép gửi).</summary>
    private LedDataSnapshot Recompute()
    {
        var cards = _cards.GetAll();
        var snap = _data.GetSnapshot(cards);
        lock (_stateLock)
        {
            for (int i = 0; i < cards.Count; i++)
            {
                var c = cards[i];
                var r = _rt[i];
                if (!Eligible(c)) { r.Dirty = false; r.State = LedState.Disabled; continue; }
                if (r.State == LedState.Disabled) { r.State = LedState.Pending; r.Force = true; } // vừa được bật: gửi ngay
                r.Dirty = r.Force || LedFrameBuilder.DataKey(c, snap) != r.SentKey;
            }
        }
        return snap;
    }

    private async Task SendDueAsync(LedDataSnapshot snap, CancellationToken ct)
    {
        var due = new List<int>();
        var now = DateTime.UtcNow;
        lock (_stateLock)
            for (int i = 0; i < _rt.Length; i++)
            {
                var r = _rt[i];
                if (r.Dirty && now >= r.NextAttemptUtc && now >= r.LastAttemptUtc + _minInterval) due.Add(i);
            }

        // Card đang tốt gửi trước; card đã lỗi nhiều lần (có thể treo lâu) gửi sau để không làm chậm card khác
        foreach (var i in due.OrderBy(i => _rt[i].Attempts))
        {
            ct.ThrowIfCancellationRequested();
            var card = _cards.Get(i + 1);
            if (!Eligible(card)) continue;

            var key = LedFrameBuilder.DataKey(card, snap);
            var stamp = _cards.GetStamp(card.CardNo);
            // Card khong tra loi ping: coi la mat ket noi ngay, khong dua vao SDK (khong cho timeout, khong kill bridge)
            if (_precheckPing && !await IsReachableAsync(card.Para_IPAddress, ct))
            {
                Complete(i, key, stamp, LedResult.Fail("Card không trả lời ping (mất mạng?)", -4));
                continue;
            }

            var frame = LedFrameBuilder.Build(card, snap, ResolveImage);

            LedResult res;
            await _io.WaitAsync(ct);
            try { res = await _transport.SendAsync(frame, ct); }
            finally { _io.Release(); }
            Complete(i, key, stamp, res);
        }
    }

    private async Task<bool> IsReachableAsync(string ip, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, _pingTimeoutMs).WaitAsync(ct);
            return reply.Status == IPStatus.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    private void Complete(int i, string key, string stamp, LedResult res)
    {
        lock (_stateLock)
        {
            var r = _rt[i];
            var now = DateTime.UtcNow;
            r.LastAttemptUtc = now;
            r.LastSendMs = res.ElapsedMs;

            if (res.Ok)
            {
                r.SentKey = key; r.SentStamp = stamp;
                r.Force = false; r.Dirty = false;
                r.State = LedState.Online;
                r.Attempts = 0; r.NextAttemptUtc = DateTime.MinValue;
                r.LastSentUtc = now; r.LastError = null;
                r.SendCount++;
            }
            else
            {
                r.Attempts++;
                r.State = LedState.Offline;
                r.LastError = res.Error;
                // 2^n giây (tối đa _maxBackoff) ± 10%: tránh nhiều card cùng thử lại một lúc
                var secs = Math.Min(_maxBackoff.TotalSeconds, Math.Pow(2, Math.Min(r.Attempts, 16)));
                r.NextAttemptUtc = now + TimeSpan.FromSeconds(secs * (0.9 + Random.Shared.NextDouble() * 0.2));
                _logger.LogWarning("Gui LED card {No} that bai (lan {N}): {Err}. Thu lai sau {S:0}s.", i + 1, r.Attempts, res.Error, secs);
            }
        }
    }

    /// <summary>Ảnh lưu tương đối trong thư mục dữ liệu (led-images/...) hoặc đường dẫn tuyệt đối như PGS.</summary>
    private string ResolveImage(string path) =>
        string.IsNullOrWhiteSpace(path) ? "" : Path.IsPathRooted(path) ? path : Path.Combine(_dataDir, path);

    // -----------------------------------------------------------------------------------------
    public IReadOnlyList<LedCardStatus> GetStatuses()
    {
        var list = new List<LedCardStatus>(_rt.Length);
        lock (_stateLock)
            for (int i = 0; i < _rt.Length; i++)
            {
                var r = _rt[i];
                list.Add(new LedCardStatus
                {
                    CardNo = i + 1,
                    State = r.State,
                    Dirty = r.Dirty,
                    ConfigChanged = r.SentStamp is not null && r.SentStamp != _cards.GetStamp(i + 1),
                    LastSentUtc = r.LastSentUtc,
                    LastSendMs = r.LastSendMs,
                    Attempts = r.Attempts,
                    NextAttemptUtc = r.Attempts > 0 ? r.NextAttemptUtc : null,
                    LastError = r.LastError,
                    SendCount = r.SendCount
                });
            }
        return list;
    }

    public void ForceSend(int cardNo)
    {
        if (cardNo is < 1 or > HdCardCatalog.CardCount) return;
        lock (_stateLock)
        {
            var r = _rt[cardNo - 1];
            r.Force = true; r.NextAttemptUtc = DateTime.MinValue; r.LastAttemptUtc = DateTime.MinValue;
        }
        Notify();
    }

    public async Task<LedResult> PingAsync(int cardNo, CancellationToken ct)
    {
        var card = _cards.Get(cardNo);
        if (!LedCard.IsValidIp(card.Para_IPAddress)) return LedResult.Fail("Card chưa có địa chỉ IP hợp lệ.");
        await _io.WaitAsync(ct);
        try { return await _transport.IsOnlineAsync(card.Para_IPAddress, ct); }
        finally { _io.Release(); }
    }

    public override void Dispose()
    {
        _parking.StateChanged -= Notify;
        _cards.Changed -= OnCardChanged;
        (_transport as IDisposable)?.Dispose();
        base.Dispose();
    }
}
