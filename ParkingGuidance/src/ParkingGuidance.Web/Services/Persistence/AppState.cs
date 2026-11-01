namespace ParkingGuidance.Web.Services.Persistence;

/// <summary>Trạng thái cấp ứng dụng (loại 1, lưu cạnh bản quyền): dự án mở gần nhất + danh sách dự án mở gần đây.
/// File: {DataDirectory}/app-state.json.</summary>
public class AppState
{
    public string? LastProject { get; set; }
    public List<RecentProject> Recent { get; set; } = new();
}

public class RecentProject
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTime LastOpenedUtc { get; set; }
}

/// <summary>Tên file/thư mục cố định trong thư mục dự án.</summary>
public static class ProjectFiles
{
    /// <summary>File nhận diện 1 thư mục là dự án PGS (tên, ngày tạo, ngày lưu).</summary>
    public const string Manifest = "project.pgs.json";
}
