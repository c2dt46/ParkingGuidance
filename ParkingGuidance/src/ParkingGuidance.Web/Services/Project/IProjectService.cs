using ParkingGuidance.Web.Services.Persistence;

namespace ParkingGuidance.Web.Services.Project;

/// <summary>Một phần dữ liệu của dự án (cảm biến, camera, LED, layout...) do 1 service giữ trong bộ nhớ.
/// Service nào có dữ liệu loại 2 phải cài interface này để ProjectService có thể LƯU CHUNG và MỞ DỰ ÁN KHÁC.</summary>
public interface IProjectScoped
{
    /// <summary>Tên hiển thị của phần dữ liệu (dùng trong thông báo lỗi).</summary>
    string ProjectPartName { get; }

    /// <summary>Ghi toàn bộ dữ liệu đang giữ trong bộ nhớ ra thư mục dự án. false = ghi lỗi.</summary>
    bool FlushProject();

    /// <summary>Bỏ dữ liệu đang giữ, nạp lại từ thư mục dự án hiện tại (đã được IPersistenceService trỏ sang) và báo cho các bên đang nghe (Changed...).</summary>
    void ReloadProject();
}

public class ProjectManifest
{
    public string App { get; set; } = "ParkingGuidance";
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime SavedUtc { get; set; }
}

public class ProjectInfo
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime SavedUtc { get; set; }
}

/// <summary>Quản lý DỰ ÁN: thư mục gom toàn bộ dữ liệu loại 2. Lưu chung, mở dự án khác, tạo mới, lưu thành bản sao.</summary>
public interface IProjectService
{
    ProjectInfo Current { get; }
    IReadOnlyList<RecentProject> Recent { get; }

    /// <summary>true = đã có dự án được mở / tạo / khôi phục từ lần chạy trước. false = chưa từng mở dự án nào (hoặc dự án gần nhất không còn dùng được):
    /// chương trình đang chạy TẠM trên thư mục dự án mặc định và giao diện nên mời người dùng mở / tạo dự án.</summary>
    bool IsProjectOpen { get; }

    /// <summary>Dự án mở gần nhất nhưng không dùng được lúc khởi động (xem IPersistenceService.MissingLastProject). null nếu không có hoặc đã mở dự án khác.</summary>
    string? MissingLastProject { get; }

    /// <summary>Dùng luôn thư mục dự án đang chạy (mặc định) làm dự án đang mở: ghi project.pgs.json và nhớ làm dự án mở gần nhất.</summary>
    ProjectInfo UseCurrent();

    /// <summary>Ghi toàn bộ dữ liệu dự án + cập nhật nhãn dự án. Ném InvalidOperationException (thông báo tiếng Việt) nếu có phần ghi lỗi.</summary>
    ProjectInfo Save();

    /// <summary>Mở dự án trong thư mục <paramref name="path"/> (phải có project.pgs.json). Lưu dự án đang mở trước khi chuyển.</summary>
    ProjectInfo Open(string path);

    /// <summary>Tạo dự án MỚI (trống) trong thư mục <paramref name="path"/> (chưa tồn tại hoặc đang rỗng) rồi mở nó.</summary>
    ProjectInfo Create(string path, string? name);

    /// <summary>Lưu dự án đang mở thành bản sao ở thư mục mới rồi chuyển sang bản sao đó (giống "Save As").</summary>
    ProjectInfo SaveAs(string path, string? name);

    /// <summary>Xảy ra sau khi đổi dự án (mở / tạo / lưu thành).</summary>
    event Action? Changed;
}
