namespace ParkingGuidance.Web.Services.Persistence;

/// <summary>
/// Lưu/đọc dữ liệu của ứng dụng ra file JSON trên đĩa, để dữ liệu (mặt bằng, layout, ô đỗ,
/// phiên đỗ xe...) KHÔNG bị mất khi tắt/khởi động lại server — khác với việc chỉ giữ trong bộ
/// nhớ (in-memory) như trước đây.
/// </summary>
public interface IPersistenceService
{
    /// <summary>Thư mục thực tế đang lưu dữ liệu (hiển thị cho người dùng biết cần sao lưu ở đâu).</summary>
    string DataDirectory { get; }

    void Save<T>(string key, T data);
    T? Load<T>(string key);
}
