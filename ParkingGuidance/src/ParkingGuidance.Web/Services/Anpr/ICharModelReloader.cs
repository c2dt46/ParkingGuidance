namespace ParkingGuidance.Web.Services.Anpr;

/// <summary>
/// Cho phép NẠP LẠI model CNN đọc ký tự (<c>char_cnn.onnx</c>) khi chương trình đang chạy — dùng sau khi tab Mô phỏng huấn luyện
/// xong model mới, để áp dụng ngay mà không cần khởi động lại ứng dụng.
/// </summary>
public interface ICharModelReloader
{
    /// <summary>Đường dẫn file model ký tự đang được nạp (null nếu chưa nạp model nào).</summary>
    string? CurrentModelPath { get; }

    /// <summary>Nạp model từ file <paramref name="modelPath"/> thay cho model đang dùng. Thất bại thì model cũ vẫn giữ nguyên.</summary>
    bool Reload(string modelPath, out string message);
}
