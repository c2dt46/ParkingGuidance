using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using OpenCvSharp;
using ParkingGuidance.Web.Hubs;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Anpr;
using ParkingGuidance.Web.Services.Detection;
using ParkingGuidance.Web.Services.Parking;
using ParkingGuidance.Web.Services.Settings;

namespace ParkingGuidance.Web.Services.Camera;

public class CameraFrameProcessor : ICameraFrameProcessor
{
    private readonly IVehicleDetectionService _detector;
    private readonly IPlateRecognitionService _anpr;
    private readonly IParkingService _parking;
    private readonly IHubContext<MonitoringHub> _hub;
    private readonly ISettingsService _settings;

    /// <summary>Khung xe của từng ô ở lần xử lý trước — chỉ đọc biển khi xe ĐÃ ĐỨNG YÊN (khung gần như không đổi),
    /// tránh đọc lúc xe còn đang chạy vào ô (mờ, nghiêng) rồi chốt nhầm biển.</summary>
    private readonly ConcurrentDictionary<string, BoundingBox> _lastBoxes = new();
    private const float StableIou = 0.80f;

    public CameraFrameProcessor(
        IVehicleDetectionService detector, IPlateRecognitionService anpr,
        IParkingService parking, IHubContext<MonitoringHub> hub, ISettingsService settings)
    {
        _settings = settings;
        _detector = detector;
        _anpr = anpr;
        _parking = parking;
        _hub = hub;
    }

    public async Task<int> ProcessAsync(
        string cameraId, Mat frame, IReadOnlyList<DetectionZone> zones, string? legacySlotId, string? floor, string? zone,
        CancellationToken ct, bool manual = false)
    {
        // Model chua nap duoc: KHONG the ket luan "vung khong co xe" (neu khong, moi xe dang do se bi coi la da roi di)
        if (!_detector.IsReady) return 0;

        // Do xe va do khung bien SONG SONG tren ca khung; bien nam hoan toan ngoai moi xe thi khong hop le (bi bo)
        var st = _settings.Current;
        var (detections, plateBoxes) = FrameAnalysis.DetectParallel(_detector, _anpr, frame, st, detectPlates: true);
        var plateAssign = FrameAnalysis.AssignPlates(
            detections.Select(d => d.Box).ToList(), plateBoxes, st.PlateMinOverlap ?? 0.1f);
        int registered = 0;

        if (zones.Count > 0)
        {
            // Moi vung toi da 1 xe (xe lon/gan nhat trong vung thang), so xe nhan duoc <= so vung. Xe thua (xe nho/xa/
            // bi che cung lot trong vung) bi bo qua HOAN TOAN, khong doc bien so de tiet kiem chi phi OCR.
            var assignments = ZoneUtils.AssignVehiclesToZones(
                detections.Select(d => d.Box).ToList(), zones, frame.Width, frame.Height);

            var occupiedZoneIds = new HashSet<string>();
            foreach (var a in assignments)
            {
                // Khoa o = "{tang}-{zone}-{so thu tu}" ; vung co so thu tu khong hop le (khong phai so nguyen duong) thi bo qua
                var key = SlotKey.ForZone(floor, zone, a.Zone.SlotId);
                if (key is null) continue;
                occupiedZoneIds.Add(a.Zone.Id);

                _parking.EnsureSlot(key, floor ?? "B1", zone);
                plateAssign.TryGetValue(a.VehicleIndex, out var plateBox);
                await RegisterOne(cameraId, key, frame, detections[a.VehicleIndex], plateBox, ct, manual);
                registered++;
            }

            // Vung KHONG co xe: sau N lan xu ly lien tiep van khong thay xe thi xac nhan xe da roi (o ve "Con trong",
            // luu bien + gio xe vua ra). Chay ca khi khung hinh khong phat hien duoc xe nao.
            var confirm = manual ? 1 : _settings.Current.VacancyConfirmFrames;
            foreach (var vz in zones)
            {
                var key = SlotKey.ForZone(floor, zone, vz.SlotId);
                if (key is null || occupiedZoneIds.Contains(vz.Id)) continue;

                _lastBoxes.TryRemove(key, out _); // xe moi vao o nay sau do phai dung yen roi moi doc bien
                var exit = _parking.RegisterVacancy(cameraId, key, confirm);
                if (exit is not null)
                {
                    await _hub.Clients.All.SendAsync("ParkingEvent", new
                    {
                        exit.CameraId,
                        exit.SlotId,
                        exit.PlateNumber,
                        exit.EventType,
                        Timestamp = exit.Timestamp
                    }, ct);
                }
            }
        }
        else if (legacySlotId is not null && detections.Count > 0)
        {
            // Kieu cu (khong co vung): don gian hoa, chi gan xe co do tin cay cao nhat vao 1 o co dinh
            var best = detections.OrderByDescending(d => d.Confidence).First();
            plateAssign.TryGetValue(detections.IndexOf(best), out var bestPlateBox);
            await RegisterOne(cameraId, legacySlotId, frame, best, bestPlateBox, ct, manual);
            registered = 1;
        }

        return registered;
    }

    private async Task RegisterOne(
        string cameraId, string slotId, Mat frame, DetectionResult vehicle, BoundingBox? plateBox, CancellationToken ct, bool manual = false)
    {
        // Chi doc bien khi (1) xe da dung yen so voi lan xu ly truoc, va (2) bien cua xe dang do chua duoc chot.
        // Khong doc thi PlateNumber = null -> RegisterDetection giu nguyen bien da biet cua phien.
        // Thu cong (nut "Nhan dien ngay"): nguoi dung chu dong chup 1 khung -> doc bien NGAY, khong doi xe dung yen,
        // khong bo qua OCR du bien cu da chot (du lieu cu da bi xoa truoc khi chay).
        bool stable = manual || (_lastBoxes.TryGetValue(slotId, out var prev) && BoxUtils.Iou(prev, vehicle.Box) >= StableIou);
        _lastBoxes[slotId] = vehicle.Box;
        if (stable && (manual || _parking.NeedsPlateRead(slotId)))
        {
            if (plateBox is not null)
            {
                // Co khung bien nam trong khung xe: chi doc ky tu tai khung do (nhanh, khong tim lai tren ca xe)
                var (plate, conf, pb) = _anpr.ReadPlateAt(frame, plateBox);
                vehicle.PlateNumber = plate;
                vehicle.PlateConfidence = conf;
                vehicle.PlateBox = pb ?? plateBox;
            }
            else if ((_settings.Current.PlateFullSearch ?? 0) != 0)
            {
                // Tuy chon (mac dinh tat): xe khong co khung bien thi van tim bien day du tren anh xe — cham, toi vai giay moi xe
                var rect = BoxUtils.ClampToImage(frame, vehicle.Box, BoxUtils.PlateSearchExpand);
                if (rect.Width > 0 && rect.Height > 0)
                {
                    using var crop = new Mat(frame, rect);
                    var (plate, conf, _) = _anpr.Recognize(crop);
                    vehicle.PlateNumber = plate;
                    vehicle.PlateConfidence = conf;
                }
            }
            // Mac dinh: khong co khung bien nam trong vung xe -> khong doc, PlateNumber = null (giu bien da biet cua phien)
        }

        var evt = _parking.RegisterDetection(cameraId, slotId, vehicle, immediate: manual);

        await _hub.Clients.All.SendAsync("ParkingEvent", new
        {
            evt.CameraId,
            evt.SlotId,
            evt.PlateNumber,
            evt.EventType,
            Timestamp = evt.Timestamp,
            VehicleType = vehicle.VehicleType.ToString(),
            vehicle.Color,
            Confidence = vehicle.Confidence
        }, ct);
    }
}
