using ParkingGuidance.Web.Models;

namespace ParkingGuidance.Web.Services.Settings;

public interface ISettingsService
{
    /// <summary>Bản chụp cài đặt hiện tại (bất biến — đọc ở mỗi vòng lặp để thay đổi có hiệu lực ngay).</summary>
    SystemSettings Current { get; }

    /// <summary>Kiểm tra giá trị, lưu xuống đĩa và áp dụng ngay. Trả về giá trị đã chuẩn hoá.</summary>
    SystemSettings Update(SystemSettings settings);
}
