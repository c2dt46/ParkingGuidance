using System.Collections.Concurrent;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Persistence;

namespace ParkingGuidance.Web.Services.CameraRegistry;

/// <summary>Lưu trữ danh sách camera vật lý đã khai báo — tự tải lại khi khởi động và tự lưu sau
/// mỗi thay đổi qua IPersistenceService (file data/camera-registry.json).</summary>
public class CameraRegistryService : ICameraRegistryService
{
    private readonly ConcurrentDictionary<string, ManagedCamera> _cameras = new();
    private readonly IPersistenceService _persistence;
    private readonly object _lock = new();
    private const string StateKey = "camera-registry";

    public event Action? Changed;
    private void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { /* nguoi nghe loi khong duoc lam hong thao tac luu camera */ }
    }

    public CameraRegistryService(IPersistenceService persistence)
    {
        _persistence = persistence;
        var saved = _persistence.Load<List<ManagedCamera>>(StateKey);
        if (saved is not null)
        {
            foreach (var cam in saved)
                _cameras[cam.Id] = cam;
            MigrateCameraZones();
            MigrateZoneNumbers();
        }
    }

    /// <summary>
    /// Dữ liệu cũ chưa có Zone: camera đã gán tầng được xếp vào Zone mặc định (1) — dữ liệu cũ vốn đã không trùng số trong cả tầng
    /// nên xếp chung 1 Zone vẫn không trùng. Zone đã hợp lệ được chuẩn hoá (bỏ số 0 đứng đầu); Zone sai định dạng bị đặt lại.
    /// </summary>
    private void MigrateCameraZones()
    {
        bool changed = false;
        foreach (var cam in _cameras.Values)
        {
            if (SlotKey.TryNormalizeNumber(cam.Zone, out var z))
            {
                if (cam.Zone != z) { cam.Zone = z; changed = true; }
                continue;
            }

            var target = string.IsNullOrWhiteSpace(cam.Floor) ? null : SlotKey.DefaultZone;
            if (cam.Zone != target) { cam.Zone = target; changed = true; }
        }
        if (changed) SaveState();
    }

    /// <summary>
    /// Chuyển số thứ tự vị trí cũ ("B1-001", "A", ...) sang số tự nhiên: ưu tiên lấy dãy chữ số ở cuối ("B1-001" → 1); nếu số đó đã bị
    /// vị trí khác trong cùng Zone (cùng tầng) dùng hoặc không có chữ số thì cấp số nhỏ nhất còn trống của Zone. Vị trí đã hợp lệ giữ nguyên.
    /// </summary>
    private void MigrateZoneNumbers()
    {
        bool changed = false;

        foreach (var group in _cameras.Values
                     .GroupBy(c => (Floor: SlotKey.NormalizeFloor(c.Floor), Zone: SlotKey.NormalizeZone(c.Zone))))
        {
            var cams = group.OrderBy(c => c.AddedAt).ToList();
            var used = new HashSet<string>(StringComparer.Ordinal);
            var pending = new List<DetectionZone>();

            // Luot 1: giu cac vung da hop le (so nguyen duong, dang chuan, khong trung)
            foreach (var z in cams.SelectMany(c => c.Zones))
            {
                if (SlotKey.TryNormalizeNumber(z.SlotId, out var n) && n == (z.SlotId ?? "").Trim() && used.Add(n)) continue;
                pending.Add(z);
            }

            // Luot 2: sua cac vung con lai
            foreach (var z in pending)
            {
                string number;
                if (SlotKey.TryExtractLegacyNumber(z.SlotId, out var legacy) && !used.Contains(legacy)) number = legacy;
                else
                {
                    int next = 1;
                    while (used.Contains(next.ToString())) next++;
                    number = next.ToString();
                }
                used.Add(number);
                z.SlotId = number;
                z.Name = $"Ô {number}";
                changed = true;
            }
        }

        if (changed) SaveState();
    }

    private void SaveState() => _persistence.Save(StateKey, _cameras.Values.ToList());

    public IReadOnlyList<ManagedCamera> GetAll() =>
        _cameras.Values.OrderByDescending(c => c.AddedAt).ToList();

    public ManagedCamera? Get(string id) => _cameras.TryGetValue(id, out var c) ? c : null;

    public bool IsIpRegistered(string ipAddress, int port) =>
        _cameras.Values.Any(c =>
            string.Equals(c.IpAddress, ipAddress, StringComparison.OrdinalIgnoreCase) && c.Port == port);

    public ManagedCamera Add(string ipAddress, int port, string brand, string? model, string? onvifServiceUrl)
    {
        lock (_lock)
        {
            if (IsIpRegistered(ipAddress, port))
                throw new InvalidOperationException($"Camera co dia chi {ipAddress}:{port} da ton tai trong danh sach.");

            var camera = new ManagedCamera
            {
                IpAddress = ipAddress,
                Port = port,
                Brand = brand,
                Model = model,
                OnvifServiceUrl = onvifServiceUrl
            };
            _cameras[camera.Id] = camera;
            SaveState();
            return camera;
        }
    }

    public ManagedCamera? UpdateInfo(string id, string? floor, string? zone, string? notes, string? videoSource)
    {
        if (!_cameras.TryGetValue(id, out var camera)) return null;
        camera.Floor = floor;
        camera.Zone = zone;
        camera.Notes = notes;
        camera.VideoSource = videoSource;
        SaveState();
        RaiseChanged();
        return camera;
    }

    public ManagedCamera? UpdateZones(string id, List<DetectionZone> zones)
    {
        if (!_cameras.TryGetValue(id, out var camera)) return null;
        camera.Zones = zones ?? new List<DetectionZone>();
        SaveState();
        RaiseChanged();
        return camera;
    }

    public ManagedCamera? UpdateMotionThreshold(string id, double? motionThreshold)
    {
        if (!_cameras.TryGetValue(id, out var camera)) return null;
        camera.MotionThreshold = motionThreshold;
        SaveState();
        return camera;
    }

    public ManagedCamera? UpdateDetectionMode(string id, string mode)
    {
        if (!_cameras.TryGetValue(id, out var camera)) return null;
        camera.DetectionMode = mode == "Manual" ? "Manual" : "Automatic";
        SaveState();
        return camera;
    }

    public ManagedCamera? UpdateUiSettings(string id, string sourceMode, int captureIntervalSeconds, bool autoCapture, bool autoDetect, bool registerToParking)
    {
        if (!_cameras.TryGetValue(id, out var camera)) return null;
        camera.UiSourceMode = sourceMode == "Camera" ? "Camera" : "Pc";
        camera.UiCaptureIntervalSeconds = Math.Clamp(captureIntervalSeconds, 2, 600);
        camera.UiAutoCapture = autoCapture;
        camera.UiAutoDetect = autoDetect;
        camera.UiRegisterToParking = registerToParking;
        SaveState();
        return camera;
    }

    public ManagedCamera? Commit(string id)
    {
        if (!_cameras.TryGetValue(id, out var camera)) return null;
        camera.IsCommitted = true;
        SaveState();
        return camera;
    }

    public bool Delete(string id)
    {
        var ok = _cameras.TryRemove(id, out _);
        if (ok) { SaveState(); RaiseChanged(); }
        return ok;
    }
}
