using System.Collections.Concurrent;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Persistence;

namespace ParkingGuidance.Web.Services.Parking;

/// <summary>
/// Lưu trữ trạng thái bãi xe trong bộ nhớ (in-memory). Phù hợp cho demo/khởi đầu;
/// khi triển khai thật nên thay bằng EF Core + cơ sở dữ liệu (SQL Server/PostgreSQL...).
/// </summary>
public class ParkingService : IParkingService
{
    private readonly ConcurrentDictionary<string, ParkingSlot> _slots = new();
    private readonly ConcurrentDictionary<string, CameraDevice> _cameras = new();
    private readonly ConcurrentDictionary<string, LedBoard> _leds = new();
    private readonly ConcurrentDictionary<string, ParkingSession> _activeSessions = new(); // key = slotId
    private readonly List<ParkingSession> _history = new();
    private readonly ConcurrentQueue<ParkingEvent> _events = new();
    private readonly object _lock = new();

    private readonly IPersistenceService _persistence;
    private const string StateKey = "parking-state";

    public ParkingService(IPersistenceService persistence)
    {
        _persistence = persistence;

        var saved = _persistence.Load<ParkingStateSnapshot>(StateKey);
        if (saved is not null)
        {
            Restore(saved);
            PurgeLegacyDemoData();
            MigrateSlotKeys();
        }
        else
        {
            SaveState(); // lan dau chay: bat dau voi bai do TRONG, o do se sinh ra tu cac vung nhan dien da ve
        }
    }

    private void Restore(ParkingStateSnapshot snapshot)
    {
        foreach (var slot in snapshot.Slots) _slots[slot.SlotId] = slot;
        foreach (var cam in snapshot.Cameras) _cameras[cam.CameraId] = cam;
        foreach (var led in snapshot.Leds) _leds[led.LedId] = led;
        foreach (var session in snapshot.ActiveSessions) _activeSessions[session.SlotId] = session;
        _history.AddRange(snapshot.History);
        foreach (var evt in snapshot.Events) _events.Enqueue(evt);
    }

    public event Action? StateChanged;

    public void VisitSlots(Action<ParkingSlot> visit)
    {
        foreach (var slot in _slots.Values) visit(slot); // ConcurrentDictionary: duyệt an toàn, không khoá
    }

    /// <summary>Ghi lại toàn bộ trạng thái ra đĩa. Gọi sau MỌI thao tác làm thay đổi dữ liệu.</summary>
    private void SaveState()
    {
        try { SaveStateCore(); }
        finally
        {
            try { StateChanged?.Invoke(); } catch { /* trình nghe lỗi không được làm hỏng ghi nhận bãi xe */ }
        }
    }

    private void SaveStateCore()
    {
        _persistence.Save(StateKey, new ParkingStateSnapshot
        {
            Slots = _slots.Values.ToList(),
            Cameras = _cameras.Values.ToList(),
            Leds = _leds.Values.ToList(),
            ActiveSessions = _activeSessions.Values.ToList(),
            History = _history.ToList(),
            Events = _events.ToList()
        });
    }

    /// <summary>
    /// Chuyển dữ liệu cũ sang quy ước "{TẦNG}-{ZONE}-{SỐ}": khoá ô cũ ("1", "B1-001", "B1-1"...) → khoá mới với Zone mặc định
    /// (vd "B1-1" → "B1-1-1") để các tầng/Zone có thể dùng lại cùng số. Khoá đã ở dạng mới chỉ được đồng bộ lại trường Zone/Number.
    /// Cập nhật luôn phiên đỗ, lịch sử và sự kiện đang trỏ tới khoá cũ. Chạy lặp lại không đổi gì thêm.
    /// </summary>
    private void MigrateSlotKeys()
    {
        bool changed = false;
        var remap = new Dictionary<string, string>();

        foreach (var slot in _slots.Values.OrderBy(sl => sl.SlotId, StringComparer.Ordinal).ToList())
        {
            // Khoa da o dang moi "{tang}-{zone}-{so}": chi dong bo lai Zone/So hien thi
            if (SlotKey.TryParseForFloor(slot.SlotId, slot.Floor, out var keyZone, out var keyNumber))
            {
                if (slot.Zone != keyZone) { slot.Zone = keyZone; changed = true; }
                if (slot.Number != keyNumber) { slot.Number = keyNumber; changed = true; }
                continue;
            }

            if (!SlotKey.TryExtractLegacyNumber(slot.SlotId, out var n)) continue;

            if (slot.Number != n) { slot.Number = n; changed = true; }

            var newKey = SlotKey.Make(slot.Floor, SlotKey.DefaultZone, n);
            if (newKey == slot.SlotId) continue;
            if (_slots.ContainsKey(newKey)) continue; // trung khoa moi (2 o cu cung so trong 1 tang): giu o dau, bo qua o sau

            _slots.TryRemove(slot.SlotId, out _);
            remap[slot.SlotId] = newKey;
            slot.SlotId = newKey;
            slot.Floor = SlotKey.NormalizeFloor(slot.Floor);
            slot.Zone = SlotKey.DefaultZone;
            _slots[newKey] = slot;
            changed = true;
        }

        if (remap.Count > 0)
        {
            foreach (var (oldKey, newKey) in remap)
            {
                if (_activeSessions.TryRemove(oldKey, out var active))
                {
                    active.SlotId = newKey;
                    _activeSessions[newKey] = active;
                }
            }

            void Fix(ParkingSession session)
            {
                if (remap.TryGetValue(session.SlotId, out var nk)) session.SlotId = nk;
                foreach (var e in session.Events)
                    if (e.SlotId is not null && remap.TryGetValue(e.SlotId, out var nk2)) e.SlotId = nk2;
            }
            foreach (var h in _history) Fix(h);
            foreach (var a in _activeSessions.Values) Fix(a);
            foreach (var e in _events)
                if (e.SlotId is not null && remap.TryGetValue(e.SlotId, out var nk3)) e.SlotId = nk3;
        }

        if (changed) SaveState();
    }

    // Du lieu demo cua cac ban cu (7 o do B1-001..B1-007 gan 4 camera mau C142/C153/C183/C143 kem bien so gia)
    private static readonly Dictionary<string, string> LegacyDemoPlates = new()
    {
        ["B1-001"] = "51K16327", ["B1-002"] = "51M71498", ["B1-003"] = "51L15217", ["B1-004"] = "51D29406",
        ["B1-005"] = "51M68616", ["B1-006"] = "51K20769", ["B1-007"] = ""
    };
    private static readonly string[] LegacyDemoCameraIds = { "C142", "C153", "C183", "C143" };

    /// <summary>Xoá dữ liệu demo cũ còn sót trong file đã lưu (biển số giả làm ô hiện "đã đỗ" sai). CHỈ xoá ô còn
    /// NGUYÊN VẸN đúng như lúc tạo demo (đúng khu/camera/LED/biển số mẫu, chưa bị sửa tay) — ô đã bị chỉnh được giữ nguyên.</summary>
    private void PurgeLegacyDemoData()
    {
        bool changed = false;
        foreach (var (id, plate) in LegacyDemoPlates)
        {
            if (!_slots.TryGetValue(id, out var slot)) continue;
            bool pristine = slot.Zone == "ZONE3" && slot.LedId == "LED1"
                            && LegacyDemoCameraIds.Contains(slot.CameraId)
                            && slot.ErrorMessage is null && slot.LastExitPlate is null
                            && (slot.PlateNumber ?? "") == plate;
            if (!pristine) continue;

            _slots.TryRemove(id, out _);
            _activeSessions.TryRemove(id, out _);
            changed = true;
        }

        foreach (var camId in LegacyDemoCameraIds)
        {
            if (_cameras.TryGetValue(camId, out var cam) && cam.CoveredSlotIds.All(sid => !_slots.ContainsKey(sid)))
            {
                _cameras.TryRemove(camId, out _);
                changed = true;
            }
        }

        if (_leds.ContainsKey("LED1") && !_slots.Values.Any(sl => sl.LedId == "LED1"))
        {
            _leds.TryRemove("LED1", out _);
            changed = true;
        }

        if (changed) SaveState();
    }

    public IReadOnlyList<ParkingSlot> GetSlots(string? floor = null) =>
        _slots.Values.Where(s => floor == null || s.Floor == floor).OrderBy(s => s.SlotId).ToList();

    public IReadOnlyList<CameraDevice> GetCameras() => _cameras.Values.ToList();

    public IReadOnlyList<LedBoard> GetLedBoards() => _leds.Values.ToList();

    public IReadOnlyList<ParkingEvent> GetRecentEvents(int take = 20) =>
        _events.Reverse().Take(take).ToList();

    private const string UnknownPlate = "UNKNOWN";

    /// <summary>Chỉ giữ chữ + số, viết hoa: "51K-163.27", "51k16327" và "163 27" đều so khớp được với nhau.</summary>
    private static string PlateKey(string? plate) =>
        new string((plate ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    /// <summary>Cùng độ dài và lệch tối đa 1 ký tự → nhiều khả năng là CÙNG 1 xe bị OCR đọc sai 1 ký tự.</summary>
    private static bool IsLikelySamePlate(string a, string b)
    {
        var x = PlateKey(a); var y = PlateKey(b);
        if (x.Length == 0 || x.Length != y.Length) return false;
        int diff = 0;
        for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) diff++;
        return diff <= 1;
    }

    public IReadOnlyList<ParkingSession> Search(string plateQuery)
    {
        // Biển OCR được lưu dạng "51K-163.27" (có dấu), còn người dùng thường gõ "6327" hoặc "51K16327" →
        // so khớp theo chuỗi đã bỏ dấu ở CẢ 2 phía, nếu không sẽ không tìm thấy xe do camera nhận diện.
        var q = PlateKey(plateQuery);
        if (q.Length == 0) return new List<ParkingSession>();

        var active = _activeSessions.Values.Where(s => PlateKey(s.PlateNumber).Contains(q));
        var past = _history.Where(s => PlateKey(s.PlateNumber).Contains(q));
        return active.Concat(past).OrderByDescending(s => s.EntryTime).ToList();
    }

    public ParkingSession? GetActiveSessionBySlot(string slotId) =>
        _activeSessions.TryGetValue(slotId, out var s) ? s : null;

    // Theo doi cac lan doc bien cua phien dang mo tung o (CHI trong bo nho, khong luu xuong dia). Chi truy cap trong _lock.
    private readonly Dictionary<string, PlateTracker> _trackers = new();

    /// <summary>Lấy tracker của phiên; tạo mới (gieo sẵn biển đã lưu, vd sau khi khởi động lại) nếu chưa có hoặc thuộc phiên cũ.</summary>
    private PlateTracker TrackerFor(string slotId, ParkingSession session)
    {
        if (_trackers.TryGetValue(slotId, out var t) && t.SessionId == session.SessionId) return t;

        t = new PlateTracker(session.SessionId);
        if (!string.IsNullOrWhiteSpace(session.PlateNumber) && session.PlateNumber != UnknownPlate)
            t.AddRead(PlateRead.Create(session.PlateNumber, session.PlateConfidence ?? 0.5f));
        _trackers[slotId] = t;
        return t;
    }

    public bool NeedsPlateRead(string slotId)
    {
        lock (_lock)
        {
            if (!_activeSessions.TryGetValue(slotId, out var session)) return true;
            if (!_trackers.TryGetValue(slotId, out var t) || t.SessionId != session.SessionId) return true;
            if (session.PlateNumber == UnknownPlate || t.HasPending || !t.IsLocked(session.PlateNumber)) return true;

            // Bien da chot: bo qua OCR, chi kiem tra lai dinh ky (phat hien doi xe khong qua trang thai trong)
            if (++t.SkippedSinceRead >= PlateTracker.RecheckEveryFrames)
            {
                t.SkippedSinceRead = 0;
                return true;
            }
            return false;
        }
    }

    public bool HasPendingPlateRead(IEnumerable<string> slotIds)
    {
        lock (_lock)
        {
            foreach (var id in slotIds)
            {
                if (!_slots.TryGetValue(id, out var slot) || slot.Status != SlotStatus.Occupied) continue;
                if (!_activeSessions.TryGetValue(id, out var session)) continue;
                if (!_trackers.TryGetValue(id, out var t) || t.SessionId != session.SessionId) return true;
                if (!t.IsLocked(session.PlateNumber) && t.Frames < PlateTracker.MaxFramesWhileUnlocked) return true;
            }
            return false;
        }
    }

    /// <summary>Đối chiếu biển xe đang đỗ với biển được chỉ định cho ô. Chỉ truy cập trong _lock.
    /// Trùng → Valid ngay. Khác → Invalid khi biển đã xác thực (>= 2 lần đọc giống nhau) hoặc ảnh nạp tay; chưa chắc thì Pending để 1 lần OCR sai không báo nhầm.</summary>
    private void RefreshPlateCheck(ParkingSlot slot, bool immediate = false)
    {
        var assigned = PlateKey(slot.AssignedPlate);
        if (assigned.Length == 0 || slot.Status == SlotStatus.Empty) { slot.PlateCheck = null; return; }
        if (slot.Status == SlotStatus.Error) return; // camera loi: giu ket qua truoc do

        if (string.IsNullOrWhiteSpace(slot.PlateNumber) || slot.PlateNumber == UnknownPlate) { slot.PlateCheck = "Pending"; return; }
        if (PlateKey(slot.PlateNumber) == assigned) { slot.PlateCheck = "Valid"; return; }

        bool decisive = immediate || (_activeSessions.TryGetValue(slot.SlotId, out var s) && s.IsVerified);
        slot.PlateCheck = decisive ? "Invalid" : "Pending";
    }

    public void SetAssignedPlate(string slotId, string? plate)
    {
        lock (_lock)
        {
            if (!_slots.TryGetValue(slotId, out var slot)) return;
            var normalized = PlateKey(plate);
            var value = normalized.Length == 0 ? null : normalized;
            var before = (slot.AssignedPlate, slot.PlateCheck);
            slot.AssignedPlate = value;
            RefreshPlateCheck(slot);
            if (before != (slot.AssignedPlate, slot.PlateCheck)) SaveState();
        }
    }

    public ParkingEvent RegisterDetection(string cameraId, string slotId, DetectionResult detection, bool immediate = false)
    {
        lock (_lock)
        {
            if (!_slots.TryGetValue(slotId, out var slot))
                throw new InvalidOperationException($"Khong tim thay o do '{slotId}'");

            _vacancyMisses.TryRemove(slotId, out _); // thay xe trong vung -> huy cho xac nhan "xe da roi"

            var detected = string.IsNullOrWhiteSpace(detection.PlateNumber) ? null : detection.PlateNumber;
            var conf = detection.PlateConfidence ?? 0f;
            _activeSessions.TryGetValue(slotId, out var session);

            // Bo phieu theo thoi gian: 1 lan doc don le hay sai 1-2 ky tu; bien cua phien chi doi khi cac lan doc sau
            // cho ket qua tot hon ro ret, va chi tach phien (xe khac) khi 1 bien la lap lai du so lan lien tiep.
            // immediate = true (anh nap tay, chi 1 lan): bien khac han -> doi xe ngay, khong cho xac nhan.
            PlateTracker tracker;
            if (session is null)
            {
                session = NewSession(slotId, detected, conf);
                tracker = new PlateTracker(session.SessionId) { Frames = 1 };
                if (detected is not null) tracker.AddRead(PlateRead.Create(detected, conf));
                _trackers[slotId] = tracker;
            }
            else
            {
                tracker = TrackerFor(slotId, session);
                tracker.Frames++;

                if (detected is null)
                {
                    // Lan nay OCR khong doc duoc: GIU bien da biet cua xe dang do (khong ghi de bang UNKNOWN,
                    // khong ket thuc phien) — 1 khung hinh mo/che khuat khong co nghia la xe da doi.
                }
                else
                {
                    tracker.SkippedSinceRead = 0;
                    var read = PlateRead.Create(detected, conf);

                    if (session.PlateNumber == UnknownPlate)
                    {
                        // Truoc do chua doc duoc, gio da doc duoc: bo sung vao CHINH phien nay
                        session.PlateNumber = detected;
                        session.PlateConfidence = conf;
                        tracker.AddRead(read);
                    }
                    else
                    {
                        var age = (DateTime.Now - session.EntryTime).TotalSeconds;
                        var outcome = tracker.Observe(read, session.PlateNumber, immediate, age, out var plate);
                        switch (outcome)
                        {
                            case PlateOutcome.Updated:
                                session.PlateNumber = plate;
                                session.PlateConfidence = Math.Max(session.PlateConfidence ?? 0f, conf);
                                break;

                            case PlateOutcome.ReplacedInPlace:
                                // Bien dau tien (chi 1 lan doc) co ve da bi doc sai: sua ngay trong CUNG phien
                                session.PlateNumber = plate;
                                session.PlateConfidence = conf;
                                break;

                            case PlateOutcome.NewSession:
                                // Bien khac han da duoc xac nhan: xe khac -> ket thuc phien cu (xe cu = "xe gan nhat roi o do"), mo phien moi
                                var now = DateTime.Now;
                                session.ExitTime = now;
                                _history.Add(session);
                                slot.LastExitPlate = session.PlateNumber;
                                slot.LastExitTime = now;
                                session = NewSession(slotId, plate, conf);
                                tracker.Rebind(session.SessionId);
                                break;

                            default: // Same: chi cap nhat do tin cay neu lan doc nay trung bien hien tai va tot hon
                                if (PlateKey(session.PlateNumber) == PlateKey(detected) && conf > (session.PlateConfidence ?? 0f))
                                    session.PlateConfidence = conf;
                                break;
                        }
                    }
                }
            }

            // "Da xac thuc" = >= 2 lan doc giong nhau (hoac 1 lan nhung do tin cay rat cao)
            if (detected is not null && session.PlateNumber != UnknownPlate)
            {
                int agree = tracker.AgreeCount(PlateConsensus.KeyOf(session.PlateNumber));
                session.IsVerified = agree >= 2 || (agree == 1 && (session.PlateConfidence ?? 0f) > 0.85f);
            }

            _activeSessions[slotId] = session;

            slot.Status = SlotStatus.Occupied;
            slot.ErrorMessage = null;
            slot.PlateNumber = session.PlateNumber;
            slot.ParkedAt = session.EntryTime;
            RefreshPlateCheck(slot, immediate);

            var evt = new ParkingEvent
            {
                CameraId = cameraId,
                EventType = "TRAFFIC_MANUALSNAP",
                PlateNumber = session.PlateNumber,
                SlotId = slotId
            };
            session.Events.Add(evt);
            _events.Enqueue(evt);
            while (_events.Count > 200 && _events.TryDequeue(out _)) { }

            SaveState();
            return evt;
        }
    }

    // Dem so lan xu ly lien tiep KHONG thay xe trong vung cua o dang "Da do" (khong luu xuong dia)
    private readonly ConcurrentDictionary<string, int> _vacancyMisses = new();

    public bool HasPendingVacancy(IEnumerable<string> slotIds) => slotIds.Any(id => _vacancyMisses.ContainsKey(id));

    public ParkingEvent? RegisterVacancy(string cameraId, string slotId, int confirmFrames)
    {
        lock (_lock)
        {
            if (!_slots.TryGetValue(slotId, out var slot)) return null;

            // Chi co y nghia khi o dang "Da do" va co phien dang mo (o Trong/Loi thi khong co xe nao de "roi di")
            if (slot.Status != SlotStatus.Occupied || !_activeSessions.TryGetValue(slotId, out var session))
            {
                _vacancyMisses.TryRemove(slotId, out _);
                return null;
            }

            var misses = _vacancyMisses.AddOrUpdate(slotId, 1, (_, n) => n + 1);
            if (misses < Math.Max(1, confirmFrames)) return null; // chua du so lan lien tiep -> tiep tuc cho xac nhan

            _vacancyMisses.TryRemove(slotId, out _);
            var now = DateTime.Now;

            session.ExitTime = now;
            _activeSessions.TryRemove(slotId, out _);
            _history.Add(session);

            slot.Status = SlotStatus.Empty;
            slot.PlateNumber = null;
            slot.ParkedAt = null;
            slot.ErrorMessage = null;
            slot.LastExitPlate = session.PlateNumber;
            slot.LastExitTime = now;
            RefreshPlateCheck(slot);

            var evt = new ParkingEvent
            {
                Timestamp = now,
                CameraId = cameraId,
                EventType = "VEHICLE_EXIT",
                PlateNumber = session.PlateNumber,
                SlotId = slotId
            };
            session.Events.Add(evt);
            _events.Enqueue(evt);
            while (_events.Count > 200 && _events.TryDequeue(out _)) { }

            SaveState();
            return evt;
        }
    }

    public bool SetCameraError(IEnumerable<string> slotIds, string message)
    {
        lock (_lock)
        {
            bool changed = false;
            foreach (var id in slotIds)
            {
                if (!_slots.TryGetValue(id, out var slot) || slot.Status == SlotStatus.Error) continue;

                // Giu nguyen bien so/phien dang mo: khi camera hoat dong lai se khoi phuc dung trang thai truoc do
                slot.Status = SlotStatus.Error;
                slot.ErrorMessage = message;
                _vacancyMisses.TryRemove(id, out _);
                changed = true;
            }
            if (changed) SaveState();
            return changed;
        }
    }

    public bool RecoverFromError(IEnumerable<string> slotIds)
    {
        lock (_lock)
        {
            bool changed = false;
            foreach (var id in slotIds)
            {
                if (!_slots.TryGetValue(id, out var slot) || slot.Status != SlotStatus.Error) continue;

                if (_activeSessions.TryGetValue(id, out var session))
                {
                    slot.Status = SlotStatus.Occupied;
                    slot.PlateNumber = session.PlateNumber;
                    slot.ParkedAt = session.EntryTime;
                }
                else
                {
                    slot.Status = SlotStatus.Empty;
                    slot.PlateNumber = null;
                    slot.ParkedAt = null;
                }
                slot.ErrorMessage = null;
                RefreshPlateCheck(slot);
                changed = true;
            }
            if (changed) SaveState();
            return changed;
        }
    }

    public void ResetSlot(string slotId)
    {
        lock (_lock)
        {
            if (!_slots.TryGetValue(slotId, out var slot)) return;

            if (_activeSessions.TryRemove(slotId, out var session))
            {
                session.ExitTime = DateTime.Now;
                _history.Add(session);
            }

            slot.Status = SlotStatus.Empty;
            slot.PlateNumber = null;
            slot.ParkedAt = null;
            slot.ErrorMessage = null;
            slot.AssignedPlate = null;   // vung bi xoa -> chi dinh bien cung het hieu luc
            slot.PlateCheck = null;
            _vacancyMisses.TryRemove(slotId, out _);
            SaveState();
        }
    }

    public ParkingSlot? FindSlot(string slotId) => _slots.TryGetValue(slotId, out var s) ? s : null;

    public void RemoveFloorData(string floor)
    {
        var norm = SlotKey.NormalizeFloor(floor);
        lock (_lock)
        {
            var slotIds = _slots.Values
                .Where(s => SlotKey.NormalizeFloor(s.Floor) == norm)
                .Select(s => s.SlotId)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var id in slotIds)
            {
                _slots.TryRemove(id, out _);
                _activeSessions.TryRemove(id, out _);
                _vacancyMisses.TryRemove(id, out _);
                _trackers.Remove(id);
            }

            _history.RemoveAll(h => slotIds.Contains(h.SlotId));

            var keepEvents = _events.Where(e => !slotIds.Contains(e.SlotId)).ToList();
            while (_events.TryDequeue(out _)) { }
            foreach (var e in keepEvents) _events.Enqueue(e);

            foreach (var c in _cameras.Values.Where(c => SlotKey.NormalizeFloor(c.Floor) == norm).ToList())
                _cameras.TryRemove(c.CameraId, out _);
            foreach (var l in _leds.Values.Where(l => SlotKey.NormalizeFloor(l.Floor) == norm).ToList())
                _leds.TryRemove(l.LedId, out _);

            SaveState();
        }
    }

    private static ParkingSession NewSession(string slotId, string? plate, float conf) => new()
    {
        PlateNumber = plate ?? UnknownPlate,
        SlotId = slotId,
        EntryTime = DateTime.Now,
        IsVerified = plate is not null && conf > 0.7f,
        PlateConfidence = plate is null ? null : conf
    };

    public ParkingSlot EnsureSlot(string slotId, string floor, string? zone)
    {
        bool isNew = !_slots.ContainsKey(slotId);
        var slot = _slots.GetOrAdd(slotId, id => new ParkingSlot
        {
            SlotId = id,
            Floor = SlotKey.NormalizeFloor(floor),
            Number = SlotKey.NumberOfKey(id) ?? "",
            Zone = SlotKey.ZoneOf(id, zone),
            Status = SlotStatus.Empty
        });
        if (isNew) SaveState();
        return slot;
    }

    public ParkingSlot UpdateSlotManually(
        string slotId, SlotStatus status, string? plateNumber, DateTime? parkedAt, string? errorMessage)
    {
        lock (_lock)
        {
            if (!_slots.TryGetValue(slotId, out var slot))
                throw new InvalidOperationException($"Khong tim thay o do '{slotId}'");

            // Xe dang do truoc khi chinh tay: neu bi doi/xoa thi no la "xe gan nhat roi o do"
            var previousPlate = slot.Status == SlotStatus.Occupied ? slot.PlateNumber : null;
            if (!string.IsNullOrWhiteSpace(previousPlate) &&
                (status != SlotStatus.Occupied || PlateKey(previousPlate) != PlateKey(plateNumber)))
            {
                slot.LastExitPlate = previousPlate;
                slot.LastExitTime = DateTime.Now;
            }

            _vacancyMisses.TryRemove(slotId, out _);
            slot.Status = status;
            slot.PlateNumber = status == SlotStatus.Occupied ? plateNumber : null;
            slot.ParkedAt = status == SlotStatus.Occupied ? (parkedAt ?? DateTime.Now) : null;
            slot.ErrorMessage = status == SlotStatus.Error ? errorMessage : null;
            RefreshPlateCheck(slot, immediate: true);

            // Dong bo phien do xe (de trang "Tim xe" van tra cuu dung) cho thao tac thu cong nay
            if (status == SlotStatus.Occupied && !string.IsNullOrWhiteSpace(plateNumber))
            {
                if (!_activeSessions.TryGetValue(slotId, out var session) || session.PlateNumber != plateNumber)
                {
                    if (session is not null)
                    {
                        session.ExitTime = DateTime.Now;
                        _history.Add(session);
                    }

                    _activeSessions[slotId] = new ParkingSession
                    {
                        PlateNumber = plateNumber,
                        SlotId = slotId,
                        EntryTime = slot.ParkedAt!.Value,
                        IsVerified = false
                    };
                }
            }
            else if (_activeSessions.TryRemove(slotId, out var closedSession))
            {
                closedSession.ExitTime = DateTime.Now;
                _history.Add(closedSession);
            }

            SaveState();
            return slot;
        }
    }
}
