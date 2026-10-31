using ParkingGuidance.Web.Models;

namespace ParkingGuidance.Web.Services.Parking;

public interface IParkingService
{
    /// <summary>Phát SAU MỖI thao tác làm thay đổi dữ liệu bãi xe (có thể phát cả khi nội dung thực tế không đổi). Trình xử lý phải nhẹ, không chặn
    /// (chạy trong khoá của ParkingService) — vd chỉ đánh dấu/đẩy vào hàng đợi. Dùng cho bảng LED.</summary>
    event Action? StateChanged;

    /// <summary>Duyệt mọi ô đỗ không cần sắp xếp/sao chép danh sách (nhanh, không cấp phát) — để tính thống kê.</summary>
    void VisitSlots(Action<ParkingSlot> visit);

    IReadOnlyList<ParkingSlot> GetSlots(string? floor = null);
    IReadOnlyList<CameraDevice> GetCameras();
    IReadOnlyList<LedBoard> GetLedBoards();
    IReadOnlyList<ParkingEvent> GetRecentEvents(int take = 20);
    IReadOnlyList<ParkingSession> Search(string plateQuery);
    ParkingSession? GetActiveSessionBySlot(string slotId);

    /// <summary>
    /// Ghi nhận 1 kết quả nhận diện từ camera: cập nhật trạng thái ô đỗ,
    /// mở/kết thúc phiên đỗ xe tương ứng, và phát sinh sự kiện.
    /// </summary>
    /// <param name="immediate">true = ảnh nạp tay (chỉ 1 lần xử lý): biển khác hẳn thì đổi xe ngay; false = camera chạy nền, cần nhiều lần đọc xác nhận.</param>
    ParkingEvent RegisterDetection(string cameraId, string slotId, DetectionResult detection, bool immediate = false);

    /// <summary>
    /// Ô <paramref name="slotId"/> có cần đọc biển số ở khung này không? false khi biển của xe đang đỗ đã được chốt (nhiều lần đọc giống nhau)
    /// — bỏ qua OCR để tiết kiệm tải và tránh đọc sai làm đổi biển; vẫn kiểm tra lại định kỳ.
    /// </summary>
    bool NeedsPlateRead(string slotId);

    /// <summary>true nếu có ô đang "Đã đỗ" mà biển số chưa chốt (chưa đủ lần đọc) — khi đó cần xử lý tiếp khung hình dù ảnh không đổi, để gom thêm lần đọc (có giới hạn số khung).</summary>
    bool HasPendingPlateRead(IEnumerable<string> slotIds);

    /// <summary>
    /// Ghi nhận 1 lần xử lý mà vùng của ô <paramref name="slotId"/> KHÔNG có xe. Sau <paramref name="confirmFrames"/> lần liên tiếp
    /// (chỉ tính khi ô đang "Đã đỗ") thì xác nhận xe đã rời: đóng phiên đỗ, ô về "Còn trống", lưu biển + giờ xe vừa ra.
    /// Trả về sự kiện "xe ra" khi vừa xác nhận, ngược lại null.
    /// </summary>
    ParkingEvent? RegisterVacancy(string cameraId, string slotId, int confirmFrames);

    /// <summary>true nếu có ô đang chờ xác nhận "xe đã rời" — khi đó cần xử lý tiếp khung hình dù ảnh không đổi.</summary>
    bool HasPendingVacancy(IEnumerable<string> slotIds);

    /// <summary>Camera mất kết nối: đặt các ô về trạng thái Lỗi (giữ nguyên phiên đỗ đang mở). Trả về true nếu có ô thay đổi.</summary>
    bool SetCameraError(IEnumerable<string> slotIds, string message);

    /// <summary>Camera hoạt động lại: ô đang Lỗi quay về "Đã đỗ" (nếu còn phiên đang mở) hoặc "Còn trống". Trả về true nếu có ô thay đổi.</summary>
    bool RecoverFromError(IEnumerable<string> slotIds);

    /// <summary>Vùng nhận diện của ô bị xoá: đóng phiên đang mở và đưa ô về "Còn trống" (không tính là xe ra).</summary>
    void ResetSlot(string slotId);

    /// <summary>Đặt (hoặc xoá bằng null/rỗng) biển số được chỉ định cho ô, rồi đối chiếu lại với xe đang đỗ. Gọi lại nhiều lần với cùng giá trị là vô hại.</summary>
    void SetAssignedPlate(string slotId, string? plate);

    ParkingSlot? FindSlot(string slotId);

    /// <summary>Xoá TOÀN BỘ dữ liệu bãi xe của 1 mặt bằng (tầng): ô đỗ, phiên đỗ (đang mở + lịch sử), sự kiện, camera/LED gắn với tầng đó.
    /// Dùng khi xoá mặt bằng, để tạo lại cùng mã sau này thì như mặt bằng hoàn toàn mới.</summary>
    void RemoveFloorData(string floor);

    /// <summary>Lấy ô đỗ theo Id, hoặc tạo mới (trạng thái Empty) nếu chưa tồn tại — dùng cho Layout Editor.</summary>
    ParkingSlot EnsureSlot(string slotId, string floor, string? zone);

    /// <summary>
    /// Cập nhật trạng thái 1 ô đỗ thủ công (không qua AI) — dùng để test giao diện/Layout Editor
    /// khi chưa có camera/model AI thật, hoặc để admin sửa tay khi AI nhận nhầm.
    /// </summary>
    ParkingSlot UpdateSlotManually(string slotId, SlotStatus status, string? plateNumber, DateTime? parkedAt, string? errorMessage);
}
