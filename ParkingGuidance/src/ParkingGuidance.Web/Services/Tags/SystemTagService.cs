using ParkingGuidance.Web.Models;

namespace ParkingGuidance.Web.Services.Tags;

/// <summary>Khai báo 1 System tag (chỉ đọc).</summary>
public sealed record SystemTagDef(int No, string Name, TagDataType Type, string Description, string Scope);

/// <summary>1 System tag kèm giá trị tại thời điểm đọc.</summary>
public sealed record SystemTagItem(SystemTagDef Def, long Value);

/// <summary>Tình trạng luồng chạy System tags (để trang Tags hiển thị).</summary>
public sealed class SystemTagStatus
{
    public bool Enabled { get; init; }
    public bool Running { get; init; }
    public int TickMs { get; init; }
    public int BlinkSlowMs { get; init; }
    public int BlinkFastMs { get; init; }
    public long TickCount { get; init; }
    public long ErrorCount { get; init; }
    public DateTime? LastTickUtc { get; init; }
    public string? LastError { get; init; }
}

public interface ISystemTagService
{
    IReadOnlyList<SystemTagItem> GetAll();

    /// <summary>Đọc giá trị 1 System tag theo tên (không phân biệt hoa/thường). Boolean: 0/1.</summary>
    bool TryGetValue(string name, out long value);

    SystemTagStatus GetStatus();
}

/// <summary>
/// System tags: Hour, Minute, Second, Day, Month, Year, DayOfYear, Weekday, Yesterday, Tomorrow, BlinkSlow, BlinkFast.
///
/// CÔ LẬP KHỎI LUỒNG CHÍNH: lớp này KHÔNG phụ thuộc camera / cảm biến / LED / bãi xe. Giá trị do <see cref="SystemTagWorker"/>
/// (luồng nền riêng) cập nhật và thay thế nguyên khối (đổi tham chiếu), nên người đọc không bao giờ bị khoá hay thấy dữ liệu dở dang.
/// Lỗi bên trong chỉ được ghi nhận vào trạng thái, không lan ra ngoài.
/// </summary>
public sealed class SystemTagService : ISystemTagService
{
    // Thứ tự = thứ tự hiển thị. Mô tả giữ nguyên theo bảng System tags.
    public static readonly SystemTagDef[] Defs =
    {
        new(1,  "Hour",      TagDataType.Integer, "hour ( 0 - 24 )", "Local"),
        new(2,  "Minute",    TagDataType.Integer, "minute ( 0 - 59 )", "Local"),
        new(3,  "Second",    TagDataType.Integer, "second ( 0 - 59 )", "Local"),
        new(4,  "Day",       TagDataType.Integer, "day (0 - 31)", "Local"),
        new(5,  "Month",     TagDataType.Integer, "month( 1 - 12 )", "Local"),
        new(6,  "Year",      TagDataType.Integer, "year (1994 - )", "Local"),
        new(7,  "DayOfYear", TagDataType.Integer, "day of the year (0 - 365)", "Local"),
        new(8,  "Weekday",   TagDataType.Integer, "day of the week (0=Sunday to 6=Saturday)", "Local"),
        new(9,  "Yesterday", TagDataType.Integer, "yesterday ( 1 - 31 )", "Local"),
        new(10, "Tomorrow",  TagDataType.Integer, "tommorrow ( 1 - 31 )", "Local"),
        new(11, "BlinkSlow", TagDataType.Boolean, "Slow blink trigger", "Local"),
        new(12, "BlinkFast", TagDataType.Boolean, "Fast blink trigger", "Local"),
    };

    private const int IHour = 0, IMinute = 1, ISecond = 2, IDay = 3, IMonth = 4, IYear = 5,
        IDayOfYear = 6, IWeekday = 7, IYesterday = 8, ITomorrow = 9, IBlinkSlow = 10, IBlinkFast = 11;

    private static readonly Dictionary<string, int> ByName =
        Defs.Select((d, i) => (d.Name, i)).ToDictionary(x => x.Name, x => x.i, StringComparer.OrdinalIgnoreCase);

    /// <summary>Tên có phải System tag không (Project tags không được đặt trùng).</summary>
    public static bool IsReserved(string? name) => !string.IsNullOrEmpty(name) && ByName.ContainsKey(name);

    // Ảnh chụp giá trị: luôn thay nguyên mảng, KHÔNG sửa tại chỗ.
    private long[] _values = new long[Defs.Length];

    private long _tickCount;
    private long _errorCount;
    private long _lastTickTicks;           // DateTime.UtcNow.Ticks, 0 = chưa có
    private volatile string? _lastError;
    private volatile bool _running;

    public bool Enabled { get; }
    /// <summary>Chu kỳ quét của luồng nền (ms).</summary>
    public int TickMs { get; }
    /// <summary>Nửa chu kỳ nháy chậm (ms): đổi trạng thái 0↔1 sau mỗi khoảng này.</summary>
    public int BlinkSlowMs { get; }
    /// <summary>Nửa chu kỳ nháy nhanh (ms).</summary>
    public int BlinkFastMs { get; }

    public SystemTagService(IConfiguration config)
    {
        Enabled = !bool.TryParse(config["Tags:SystemEnabled"], out var en) || en; // mặc định bật
        BlinkSlowMs = ReadMs(config["Tags:BlinkSlowMs"], 1000);
        BlinkFastMs = ReadMs(config["Tags:BlinkFastMs"], 250);
        TickMs = Math.Clamp(Math.Min(50, Math.Min(BlinkSlowMs, BlinkFastMs)), 10, 50);

        try { Refresh(DateTime.Now, Environment.TickCount64); }   // có giá trị đúng ngay từ đầu, không đợi luồng nền
        catch (Exception ex) { ReportError(ex); }
    }

    private static int ReadMs(string? raw, int def) =>
        int.TryParse(raw, out var v) ? Math.Clamp(v, 50, 60_000) : def;

    // ---------------------------------------------------------------- đọc
    public IReadOnlyList<SystemTagItem> GetAll()
    {
        var v = Volatile.Read(ref _values);
        var list = new SystemTagItem[Defs.Length];
        for (var i = 0; i < Defs.Length; i++) list[i] = new SystemTagItem(Defs[i], v[i]);
        return list;
    }

    public bool TryGetValue(string name, out long value)
    {
        value = 0;
        if (string.IsNullOrEmpty(name) || !ByName.TryGetValue(name, out var i)) return false;
        value = Volatile.Read(ref _values)[i];
        return true;
    }

    public SystemTagStatus GetStatus()
    {
        var ticks = Interlocked.Read(ref _lastTickTicks);
        return new SystemTagStatus
        {
            Enabled = Enabled,
            Running = _running,
            TickMs = TickMs,
            BlinkSlowMs = BlinkSlowMs,
            BlinkFastMs = BlinkFastMs,
            TickCount = Interlocked.Read(ref _tickCount),
            ErrorCount = Interlocked.Read(ref _errorCount),
            LastTickUtc = ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc),
            LastError = _lastError
        };
    }

    // ------------------------------------------------- ghi (chỉ SystemTagWorker gọi)
    /// <summary>Tính lại toàn bộ giá trị. Nháy lấy theo đồng hồ đơn điệu nên không lệch dù luồng nền bị trễ.</summary>
    public void Refresh(DateTime now, long monotonicMs)
    {
        var v = new long[Defs.Length];
        v[IHour] = now.Hour;
        v[IMinute] = now.Minute;
        v[ISecond] = now.Second;
        v[IDay] = now.Day;
        v[IMonth] = now.Month;
        v[IYear] = now.Year;
        v[IDayOfYear] = now.DayOfYear - 1;          // bảng quy định 0 - 365
        v[IWeekday] = (int)now.DayOfWeek;           // 0 = Chủ nhật … 6 = Thứ bảy
        v[IYesterday] = now.AddDays(-1).Day;
        v[ITomorrow] = now.AddDays(1).Day;
        v[IBlinkSlow] = (monotonicMs / BlinkSlowMs) % 2;
        v[IBlinkFast] = (monotonicMs / BlinkFastMs) % 2;

        if (!Volatile.Read(ref _values).SequenceEqual(v))   // không đổi thì khỏi thay
            Volatile.Write(ref _values, v);
    }

    public void ReportTick()
    {
        Interlocked.Increment(ref _tickCount);
        Interlocked.Exchange(ref _lastTickTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>Ghi nhận lỗi; trả về tổng số lỗi để luồng nền tự giảm tần suất ghi log.</summary>
    public long ReportError(Exception ex)
    {
        _lastError = ex.Message;
        return Interlocked.Increment(ref _errorCount);
    }

    public void MarkRunning(bool running) => _running = running;
}
