using System.Diagnostics;
using System.Security.Cryptography;
using ParkingGuidance.Web.Services.Persistence;

namespace ParkingGuidance.Web.Services.Licensing;

public interface ILicenseService
{
    /// <summary>true khi giấy phép đang dùng được (còn hạn / vĩnh viễn, đủ phần cứng). Đọc không khoá — gọi thoải mái, không tốn tài nguyên.</summary>
    bool IsOperational { get; }

    LicenseStatus GetStatus();

    /// <summary>Tab "Tạo yêu cầu": đọc số seri máy này và mã hoá thành chuỗi yêu cầu.</summary>
    Task<RequestResult> CreateRequestAsync(CancellationToken ct = default);

    /// <summary>Tab "Tạo giấy phép" (cấp 0/1): giải mã yêu cầu để xem các số seri trong đó.</summary>
    RequestInfoResult InspectRequest(string? requestText);

    /// <summary>Tab "Tạo giấy phép" (cấp 0/1): yêu cầu + thời hạn → giấy phép mới với quy luật mã hoá mới.</summary>
    IssueResult Issue(string? requestText, string? durationCode);

    /// <summary>Tab "Nhận giấy phép": giải mã giấy phép về chuỗi gốc để hiện các số seri + thời hạn (chưa kích hoạt).</summary>
    Task<PreviewResult> PreviewAsync(string? licenseText, CancellationToken ct = default);

    /// <summary>Nút "Kích hoạt".</summary>
    ActivateResult Activate(string? licenseText);
}

/// <summary>
/// Giữ trạng thái bản quyền + đếm thời gian sử dụng. Toàn bộ việc nặng (đọc phần cứng bằng WMI, ghi file) chạy ở
/// luồng nền <see cref="LicenseWorker"/>; các request web chỉ đọc 1 biến bool (IsOperational) nên không bị chậm.
///
/// Cách tính thời hạn: giấy phép cấp một lượng THỜI GIAN SỬ DỤNG (1/3/6 tháng, 1/3 năm, vĩnh viễn). Bộ đếm chỉ
/// chạy khi chương trình đang chạy VÀ có ít nhất 2 số seri trong giấy phép trùng với máy này. Bộ đếm dùng đồng hồ
/// đơn điệu (Stopwatch) nên chỉnh giờ Windows không làm thay đổi thời gian còn lại.
/// </summary>
public sealed class LicenseService : ILicenseService
{
    /// <summary>Giấy phép phải được kích hoạt trong vòng bấy nhiêu ngày kể từ ngày cấp (chống dùng lại giấy phép cũ).</summary>
    private const int ActivationWindowDays = 90;
    private const int MinMatchedSerials = 2;
    /// <summary>Chưa có giấy phép: mỗi lần khởi động ứng dụng được chạy thử chừng này thời gian.</summary>
    private static readonly TimeSpan TrialDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly IHardwareIdProvider _hwProvider;
    private readonly ILogger<LicenseService> _log;
    private readonly string _primaryPath;
    private readonly string? _secondaryPath;

    private LicenseData? _active;
    private string? _activeText;
    private double _consumed;                       // giây đã dùng của giấy phép đang kích hoạt
    private readonly Dictionary<ulong, LedgerEntry> _ledger = new();
    private long _seq;
    private HashSet<HardwareId> _hw = new();
    private bool _hwReady;
    private bool _tampered;
    private bool _dirty;
    private TimeSpan _lastTick;
    private TimeSpan _lastSave;
    private LicenseState _state = LicenseState.Initializing;
    private volatile bool _operational;

    public LicenseService(IPersistenceService persistence, IHardwareIdProvider hwProvider, ILogger<LicenseService> log)
    {
        _hwProvider = hwProvider;
        _log = log;

        // 2 bản lưu ở 2 nơi khác nhau; khi nạp lấy bản mới nhất + số giây đã dùng lớn nhất.
        _primaryPath = Path.Combine(persistence.DataDirectory, "license.dat");
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        _secondaryPath = string.IsNullOrWhiteSpace(common) ? null : Path.Combine(common, "ParkingGuidance", "license.sys");

        Load();
        _lastTick = _clock.Elapsed;
        lock (_gate) RecomputeLocked();
    }

    public bool IsOperational => _operational;

    // ------------------------------------------------------------ nạp / lưu
    private void Load()
    {
        var copies = new List<StateData>();
        var anyFile = false;
        var anyBad = false;

        foreach (var path in new[] { _primaryPath, _secondaryPath })
        {
            if (path is null) continue;
            string text;
            try
            {
                if (!File.Exists(path)) continue;
                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Không đọc được file bản quyền {Path}.", path);
                continue;
            }

            anyFile = true;
            var st = LicenseFormats.TryParseState(text);
            if (st is null)
            {
                anyBad = true;
                _log.LogWarning("File bản quyền {Path} bị hỏng hoặc đã bị chỉnh sửa.", path);
            }
            else copies.Add(st);
        }

        if (copies.Count == 0)
        {
            if (anyFile) _tampered = true;      // có file nhưng không bản nào hợp lệ
            return;
        }

        var best = copies.OrderByDescending(c => c.Seq).First();
        _seq = best.Seq;

        foreach (var c in copies)
        {
            foreach (var e in c.Ledger)
            {
                if (_ledger.TryGetValue(e.Id, out var cur))
                {
                    if (e.ConsumedSec > cur.ConsumedSec) cur.ConsumedSec = e.ConsumedSec;
                }
                else
                {
                    _ledger[e.Id] = new LedgerEntry { Id = e.Id, ConsumedSec = e.ConsumedSec, ActivatedUnix = e.ActivatedUnix };
                }
            }
        }

        if (!string.IsNullOrEmpty(best.ActiveText))
        {
            if (LicenseFormats.TryParseLicense(best.ActiveText, out var lic) && lic is not null)
            {
                _active = lic;
                _activeText = best.ActiveText;
                _consumed = _ledger.TryGetValue(lic.Id, out var entry) ? entry.ConsumedSec : 0;
            }
            else
            {
                _tampered = true;
            }
        }

        if (anyBad) _dirty = true;               // 1 bản hỏng, 1 bản tốt → ghi lại cho lành
    }

    private LedgerEntry EntryLocked(ulong id)
    {
        if (!_ledger.TryGetValue(id, out var e))
        {
            e = new LedgerEntry { Id = id, ConsumedSec = 0, ActivatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            _ledger[id] = e;
        }
        return e;
    }

    private void SaveLocked()
    {
        if (_active is not null)
        {
            var e = EntryLocked(_active.Id);
            e.ConsumedSec = Math.Max(e.ConsumedSec, (long)_consumed);
        }

        _seq++;
        var text = LicenseFormats.BuildState(new StateData
        {
            Seq = _seq,
            ActiveText = _activeText ?? "",
            Ledger = _ledger.Values.ToList()
        });

        WriteAtomic(_primaryPath, text);
        if (_secondaryPath is not null) WriteAtomic(_secondaryPath, text);

        _dirty = false;
        _lastSave = _clock.Elapsed;
    }

    private void WriteAtomic(string path, string content)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Không ghi được file bản quyền {Path}.", path);
        }
    }

    /// <summary>Ghi nốt khi tắt chương trình.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_dirty) SaveLocked();
        }
    }

    // ----------------------------------------------------------- phần cứng
    public async Task RefreshHardwareAsync(CancellationToken ct = default)
    {
        var read = Task.Run(() => _hwProvider.Read(), CancellationToken.None);
        var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(30), ct));

        IReadOnlyList<HardwareId>? list = null;
        if (finished == read)
        {
            try { list = await read; }
            catch (Exception ex) { _log.LogWarning(ex, "Đọc phần cứng lỗi."); }
        }
        else
        {
            ct.ThrowIfCancellationRequested();
            _log.LogWarning("Đọc phần cứng quá 30 giây, bỏ qua lần này.");
        }

        lock (_gate)
        {
            // Lần đọc rỗng (WMI trục trặc tạm thời) không được xoá kết quả tốt đã có.
            if (list is not null && (list.Count > 0 || _hw.Count == 0)) _hw = new HashSet<HardwareId>(list);
            _hwReady = true;
            RecomputeLocked();
        }
    }

    // ---------------------------------------------------- tính thời gian dùng
    /// <summary>Gọi định kỳ từ <see cref="LicenseWorker"/>: cộng thời gian đã dùng, cập nhật trạng thái, ghi file mỗi ~60 giây.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            var now = _clock.Elapsed;
            var dt = (now - _lastTick).TotalSeconds;
            _lastTick = now;
            if (dt < 0) dt = 0;

            if (_active is not null && !_tampered && _hwReady && _active.Duration != LicenseDuration.Permanent)
            {
                var total = LicenseDurations.Seconds(_active.Duration);
                if (MatchedLocked() >= RequiredLocked() && _consumed < total)
                {
                    _consumed = Math.Min(total, _consumed + dt);
                    _dirty = true;
                }
            }

            var before = _state;
            RecomputeLocked();
            var changed = _state != before;

            if (_dirty && (changed || now - _lastSave >= SaveEvery)) SaveLocked();
        }
    }

    private int MatchedLocked() => _active is null ? 0 : _active.Hardware.Count(h => _hw.Contains(h));

    private int RequiredLocked() => _active is null ? 0 : Math.Min(MinMatchedSerials, _active.Hardware.Count);

    private LicenseState EvaluateLocked()
    {
        if (_tampered) return LicenseState.Tampered;
        if (_active is null) return _clock.Elapsed < TrialDuration ? LicenseState.Trial : LicenseState.NotActivated;
        if (!_hwReady) return LicenseState.Initializing;
        if (MatchedLocked() < RequiredLocked()) return LicenseState.HardwareMismatch;
        if (_active.Duration == LicenseDuration.Permanent) return LicenseState.Permanent;
        if (_consumed >= LicenseDurations.Seconds(_active.Duration)) return LicenseState.Expired;
        return LicenseState.Active;
    }

    private void RecomputeLocked()
    {
        var s = EvaluateLocked();
        if (s != _state)
        {
            _log.LogInformation("Trạng thái bản quyền: {Old} → {New}", _state, s);
            _state = s;
            if (s == LicenseState.Expired) _dirty = true;
        }
        _operational = s is LicenseState.Active or LicenseState.Permanent or LicenseState.Trial;
    }

    // -------------------------------------------------------------- trạng thái
    public LicenseStatus GetStatus()
    {
        lock (_gate)
        {
            var total = _active is null ? 0 : LicenseDurations.Seconds(_active.Duration);
            var permanent = _active?.Duration == LicenseDuration.Permanent;
            var consumed = (long)_consumed;
            long remaining = permanent ? -1 : Math.Max(0, total - consumed);
            if (_state == LicenseState.Trial)
            {
                total = (long)TrialDuration.TotalSeconds;
                consumed = (long)_clock.Elapsed.TotalSeconds;
                remaining = Math.Max(0, total - consumed);
            }

            DateTimeOffset? activated = null;
            if (_active is not null && _ledger.TryGetValue(_active.Id, out var e))
                activated = DateTimeOffset.FromUnixTimeSeconds(e.ActivatedUnix);

            return new LicenseStatus(
                _state,
                _operational,
                Describe(_state),
                _active is null ? null : LicenseDurations.Code(_active.Duration),
                _active is null ? null : LicenseDurations.Label(_active.Duration),
                total,
                consumed,
                remaining,
                MatchedLocked(),
                RequiredLocked(),
                activated,
                MatchesLocked(_active?.Hardware));
        }
    }

    private static string Describe(LicenseState s) => s switch
    {
        LicenseState.Initializing => "Đang đọc thông tin phần cứng…",
        LicenseState.NotActivated => "Chưa kích hoạt giấy phép (đã hết 15 phút chạy thử của lần khởi động này).",
        LicenseState.Trial => "Chưa có giấy phép — đang chạy thử 15 phút kể từ lúc khởi động ứng dụng.",
        LicenseState.Active => "Giấy phép còn hạn.",
        LicenseState.Permanent => "Giấy phép vĩnh viễn.",
        LicenseState.Expired => "Giấy phép đã hết thời hạn sử dụng.",
        LicenseState.HardwareMismatch => "Phần cứng máy không còn khớp giấy phép (cần ít nhất 2 số seri trùng). Thời gian sử dụng đang tạm dừng.",
        LicenseState.Tampered => "Dữ liệu bản quyền bị hỏng hoặc bị chỉnh sửa. Cần kích hoạt lại giấy phép.",
        _ => ""
    };

    private IReadOnlyList<HardwareMatch> MatchesLocked(IReadOnlyList<HardwareId>? list) =>
        list is null
            ? Array.Empty<HardwareMatch>()
            : list.Select(h => new HardwareMatch(h.Kind, h.Serial, _hw.Contains(h))).ToList();

    // ----------------------------------------------------------- tạo yêu cầu
    public async Task<RequestResult> CreateRequestAsync(CancellationToken ct = default)
    {
        await RefreshHardwareAsync(ct);

        List<HardwareId> hw;
        lock (_gate)
        {
            hw = _hw.OrderBy(h => h.Kind).ThenBy(h => h.Serial, StringComparer.Ordinal).ToList();
        }

        if (hw.Count == 0)
            return new RequestResult(false, null, hw, "Không đọc được số seri nào của máy này (ổ cứng / card mạng / main). Thử chạy chương trình bằng quyền Administrator.");

        return new RequestResult(true, LicenseFormats.BuildRequest(hw), hw, null);
    }

    public RequestInfoResult InspectRequest(string? requestText)
    {
        if (!LicenseFormats.TryParseRequest(requestText, out var hw) || hw is null)
            return new RequestInfoResult(false, Array.Empty<HardwareId>(), BadRequestMessage);
        return new RequestInfoResult(true, hw, null);
    }

    private const string BadRequestMessage = "Yêu cầu không hợp lệ: sai, thiếu, thừa hoặc đã bị sửa ít nhất 1 ký tự.";
    private const string BadLicenseMessage = "Giấy phép không hợp lệ: sai, thiếu, thừa hoặc đã bị sửa ít nhất 1 ký tự.";

    // ---------------------------------------------------------- tạo giấy phép
    public IssueResult Issue(string? requestText, string? durationCode)
    {
        if (!LicenseDurations.TryParse(durationCode, out var duration))
            return new IssueResult(false, null, null, Array.Empty<HardwareId>(), "Thời hạn không hợp lệ.");

        if (!LicenseFormats.TryParseRequest(requestText, out var hw) || hw is null)
            return new IssueResult(false, null, null, Array.Empty<HardwareId>(), BadRequestMessage);

        var id = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8), 0);
        var lic = new LicenseData(id, duration, DateTimeOffset.UtcNow, hw);
        return new IssueResult(true, LicenseFormats.BuildLicense(lic), LicenseDurations.Label(duration), hw, null);
    }

    // -------------------------------------------------------- nhận giấy phép
    public async Task<PreviewResult> PreviewAsync(string? licenseText, CancellationToken ct = default)
    {
        if (!LicenseFormats.TryParseLicense(licenseText, out var lic) || lic is null)
            return Fail(BadLicenseMessage);

        if (!_hwReady) await RefreshHardwareAsync(ct);

        lock (_gate)
        {
            var matches = MatchesLocked(lic.Hardware);
            var matched = matches.Count(m => m.Match);
            var required = Math.Min(MinMatchedSerials, lic.Hardware.Count);

            var problem = CheckActivatableLocked(lic, matched, required);
            return new PreviewResult(true, null, LicenseDurations.Code(lic.Duration), LicenseDurations.Label(lic.Duration),
                matches, matched, required, problem is null,
                problem ?? "Giấy phép hợp lệ. Bấm Kích hoạt để sử dụng.");
        }

        static PreviewResult Fail(string error) =>
            new(false, error, null, null, Array.Empty<HardwareMatch>(), 0, 0, false, null);
    }

    /// <summary>null nếu kích hoạt được; ngược lại là lý do.</summary>
    private string? CheckActivatableLocked(LicenseData lic, int matched, int required)
    {
        if (!_hwReady) return "Đang đọc thông tin phần cứng, thử lại sau vài giây.";
        if (matched < required)
            return $"Giấy phép không dành cho máy này: chỉ {matched}/{required} số seri trùng khớp (cần ít nhất {required}).";

        if (_ledger.TryGetValue(lic.Id, out var used) && lic.Duration != LicenseDuration.Permanent &&
            used.ConsumedSec >= LicenseDurations.Seconds(lic.Duration))
            return "Giấy phép này đã dùng hết thời hạn, không thể kích hoạt lại.";

        if (!(_active is not null && _active.Id == lic.Id) &&
            !_ledger.ContainsKey(lic.Id) &&
            DateTimeOffset.UtcNow - lic.IssuedUtc > TimeSpan.FromDays(ActivationWindowDays))
            return $"Giấy phép được cấp quá {ActivationWindowDays} ngày trước mà chưa kích hoạt. Hãy xin giấy phép mới.";

        return null;
    }

    public ActivateResult Activate(string? licenseText)
    {
        if (!LicenseFormats.TryParseLicense(licenseText, out var lic) || lic is null)
            return new ActivateResult(false, BadLicenseMessage);

        lock (_gate)
        {
            var matched = lic.Hardware.Count(h => _hw.Contains(h));
            var required = Math.Min(MinMatchedSerials, lic.Hardware.Count);
            var problem = CheckActivatableLocked(lic, matched, required);
            if (problem is not null) return new ActivateResult(false, problem);

            // Lưu phần đã dùng của giấy phép đang chạy trước khi chuyển sang giấy phép mới.
            if (_active is not null)
            {
                var cur = EntryLocked(_active.Id);
                cur.ConsumedSec = Math.Max(cur.ConsumedSec, (long)_consumed);
            }

            var entry = EntryLocked(lic.Id);          // đã có (kích hoạt lại) → giữ nguyên số giây đã dùng
            _active = lic;
            _activeText = LicenseCodec.Normalize(licenseText ?? "");
            _consumed = entry.ConsumedSec;
            _tampered = false;
            _lastTick = _clock.Elapsed;

            RecomputeLocked();
            SaveLocked();

            _log.LogInformation("Đã kích hoạt giấy phép {Id:X16}, thời hạn {Duration}.", lic.Id, LicenseDurations.Label(lic.Duration));
            return new ActivateResult(true, $"Đã kích hoạt giấy phép ({LicenseDurations.Label(lic.Duration)}).");
        }
    }
}
