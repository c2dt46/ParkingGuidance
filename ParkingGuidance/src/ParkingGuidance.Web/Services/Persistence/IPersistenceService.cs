namespace ParkingGuidance.Web.Services.Persistence;

/// <summary>
/// Lưu/đọc dữ liệu của ứng dụng ra file JSON trên đĩa. Dữ liệu được TÁCH THÀNH 2 LOẠI:
///
///  • Loại 1 — dữ liệu HỆ THỐNG (bản quyền, dữ liệu train, model đã train, cài đặt nhận diện chung, nhật ký đọc biển...):
///    luôn nằm ở một chỗ cố định <see cref="DataDirectory"/> (Windows: %LOCALAPPDATA%\ParkingGuidance\data), KHÔNG đổi theo dự án.
///
///  • Loại 2 — dữ liệu DỰ ÁN (cảm biến, camera + vùng nhận diện, LED, layout, mặt bằng + ảnh nền, biến MS/MC, tag,
///    Modbus, trạng thái bãi xe): gom hết vào MỘT thư mục dự án <see cref="ProjectDirectory"/> để copy cả dự án sang máy khác.
///    Có thể mở dự án khác lúc đang chạy (xem IProjectService).
/// </summary>
public interface IPersistenceService
{
    /// <summary>Thư mục dữ liệu HỆ THỐNG (loại 1) — cố định, không đổi theo dự án.</summary>
    string DataDirectory { get; }

    /// <summary>Thư mục gốc của dự án đang mở (chứa project.pgs.json, data/, uploads/, led-images/).</summary>
    string ProjectDirectory { get; }

    /// <summary>{ProjectDirectory}/data — các file JSON của dự án.</summary>
    string ProjectDataDirectory { get; }

    /// <summary>{ProjectDirectory}/uploads — ảnh nền mặt bằng (floors/) và ảnh đèn/công tắc của layout (layout/). Được phục vụ ở đường dẫn /uploads/...</summary>
    string ProjectUploadsDirectory { get; }

    /// <summary>{ProjectDirectory}/led-images — ảnh cho vùng Image của bảng LED.</summary>
    string ProjectLedImagesDirectory { get; }

    /// <summary>true = lúc khởi động đã xác định được dự án để CHẠY LẠI: cấu hình <c>Persistence:ProjectPath</c> hoặc dự án mở gần nhất còn nguyên vẹn
    /// (thư mục còn và có project.pgs.json). false = chưa từng mở dự án nào, hoặc dự án mở gần nhất không còn dùng được → chương trình chỉ chạy TẠM
    /// trên thư mục dự án mặc định và chờ người dùng mở / tạo dự án.</summary>
    bool StartedWithProject { get; }

    /// <summary>Đường dẫn dự án mở gần nhất nhưng KHÔNG dùng được lúc khởi động (thư mục bị xoá / đổi tên, ổ đĩa hoặc ổ mạng chưa sẵn sàng, mất project.pgs.json).
    /// null = không có trường hợp này. Con trỏ này được GIỮ NGUYÊN trong app-state.json cho tới khi người dùng mở / tạo dự án khác.</summary>
    string? MissingLastProject { get; }

    /// <summary>Key này thuộc dữ liệu dự án (true) hay hệ thống (false).</summary>
    bool IsProjectKey(string key);

    /// <summary>Đổi sang thư mục dự án khác (tự tạo các thư mục con). Chỉ đổi đường dẫn, không nạp lại dữ liệu — xem IProjectService.</summary>
    void SetProjectDirectory(string projectDir);

    /// <summary>true = tạm bỏ qua mọi lần GHI dữ liệu dự án (dùng khi đang chuyển dự án để dữ liệu dự án cũ không ghi nhầm vào dự án mới).</summary>
    void PauseProjectWrites(bool paused);

    /// <summary>Dữ liệu dự án (trừ trạng thái bãi xe) đã đổi trong bộ nhớ (đang là thông số chạy) nhưng CHƯA ghi vào file dự án.</summary>
    bool HasUnsavedProjectChanges { get; }

    /// <summary>Đánh dấu dữ liệu dự án đang khớp với file (sau khi lưu chung / mở dự án).</summary>
    void MarkProjectSaved();

    /// <summary>Báo dữ liệu đã đổi. Với dữ liệu DỰ ÁN (cảm biến, camera, LED, layout...) chỉ cập nhật cờ "chưa lưu" — dữ liệu đã là thông số chạy trong
    /// bộ nhớ, và CHỈ được ghi vào file khi bấm nút Lưu chung (<see cref="Commit{T}"/>). Dữ liệu hệ thống và trạng thái bãi xe (parking-state) ghi ngay.
    /// Trả về false nếu ghi lỗi (đã ghi log).</summary>
    bool Save<T>(string key, T data);

    /// <summary>Ghi NGAY ra file, không trì hoãn (dùng cho Lưu chung / tạo dự án mới).</summary>
    bool Commit<T>(string key, T data);

    T? Load<T>(string key);
}
