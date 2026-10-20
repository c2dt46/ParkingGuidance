using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace ParkingGuidance.Web.Models;

/// <summary>Loại cảm biến đầu vào (giống KTB.Sensor_Type của PGS: 0 = MGT200, 1 = Keytop). MGT200 là mặc định.</summary>
public enum SensorType
{
    Pm200 = 0,
    Keytop = 1
}

/// <summary>
/// Cài đặt cảm biến (lưu data/sensors.json), theo hai trang cài đặt PM200Setting và Keytop của PGS:
///  • MGT200: PC làm "client" — kết nối TCP tới converter (IP/port), lần lượt hỏi từng ZCU được kích hoạt (tối đa 32 ZCU × 160 cảm biến).
///  • Keytop: PC làm "server" — mở cổng TCP lắng nghe, các Keytop (tối đa 85, mỗi cái 60 cảm biến) tự kết nối tới và gửi trạng thái.
/// </summary>
public class SensorSettings
{
    public const int Pm200MaxZcu = 32;
    public const int Pm200SensorsPerZcu = 160;
    public const int KeytopMax = 85;
    public const int KeytopSensorsPerDevice = 60;

    public SensorType Type { get; set; } = SensorType.Pm200;

    // ---------------- MGT200 ----------------
    /// <summary>IP của converter MGT200 (mặc định của PGS: 192.168.0.7).</summary>
    public string Pm200Ip { get; set; } = "192.168.0.7";
    /// <summary>Port của converter MGT200 (mặc định của PGS: 8888).</summary>
    public int Pm200Port { get; set; } = 8888;
    /// <summary>Thời gian tối đa chờ ZCU trả lời sau khi gửi lệnh hỏi (ms); PGS chờ 250 ms.</summary>
    public int Pm200ReplyWaitMs { get; set; } = 250;
    /// <summary>Tab "Kích hoạt": ZCU nào được phép quét (32 phần tử, chỉ số 0 = ZCU 1).</summary>
    public bool[] Pm200Enabled { get; set; } = DefaultEnabled();

    // ---------------- Keytop ----------------
    /// <summary>Cổng TCP mà PC lắng nghe để nhận dữ liệu từ các Keytop (mặc định của PGS: 7777).</summary>
    public int KeytopPort { get; set; } = 7777;
    /// <summary>IP của Keytop đầu tiên (ZCU0); số thứ tự Keytop = octet cuối của IP gửi tới − octet cuối của IP này.</summary>
    public string KeytopZcu0Ip { get; set; } = "192.168.0.100";
    /// <summary>Số lượng Keytop (ZCU), 1–85.</summary>
    public int KeytopCount { get; set; } = 1;
    /// <summary>Quá thời gian này không nhận được dữ liệu từ 1 Keytop thì coi là mất kết nối (giây). PGS: 10 nhịp × 5 giây = 50 giây.</summary>
    public int KeytopTimeoutSeconds { get; set; } = 50;

    private static bool[] DefaultEnabled()
    {
        var a = new bool[Pm200MaxZcu];
        a[0] = true; // ZCU 1 bật sẵn để chạy được ngay; các ZCU còn lại bật ở danh sách "Kích hoạt"
        return a;
    }

    /// <summary>Bản sao đã chuẩn hoá (cắt khoảng trắng, đưa các giá trị số về khoảng cho phép).</summary>
    public SensorSettings Sanitized()
    {
        var enabled = new bool[Pm200MaxZcu];
        if (Pm200Enabled != null)
            Array.Copy(Pm200Enabled, enabled, Math.Min(Pm200Enabled.Length, enabled.Length));

        return new SensorSettings
        {
            Type = Enum.IsDefined(typeof(SensorType), Type) ? Type : SensorType.Pm200,
            Pm200Ip = (Pm200Ip ?? "").Trim(),
            Pm200Port = Pm200Port,
            Pm200ReplyWaitMs = Math.Clamp(Pm200ReplyWaitMs, 100, 2000),
            Pm200Enabled = enabled,
            KeytopPort = KeytopPort,
            KeytopZcu0Ip = (KeytopZcu0Ip ?? "").Trim(),
            KeytopCount = Math.Clamp(KeytopCount, 1, KeytopMax),
            KeytopTimeoutSeconds = Math.Clamp(KeytopTimeoutSeconds, 5, 3600)
        };
    }

    /// <summary>Thông báo lỗi (tiếng Việt) nếu cài đặt không hợp lệ, ngược lại null. Gọi trên bản đã <see cref="Sanitized"/>.</summary>
    public string? Validate()
    {
        if (!IsIpv4(Pm200Ip)) return "IP converter MGT200 không hợp lệ (ví dụ 192.168.0.7).";
        if (Pm200Port is < 1 or > 65535) return "Port converter MGT200 phải từ 1 đến 65535.";
        if (!IsIpv4(KeytopZcu0Ip)) return "IP của Keytop đầu tiên (ZCU0) không hợp lệ (ví dụ 192.168.0.100).";
        if (KeytopPort is < 1 or > 65535) return "Port server Keytop phải từ 1 đến 65535.";
        return null;
    }

    /// <summary>Dấu vân tay cấu hình — dùng để biết lần lưu có thật sự thay đổi gì không.</summary>
    public string Stamp() => JsonSerializer.Serialize(this);

    public static bool IsIpv4(string? s) =>
        !string.IsNullOrWhiteSpace(s)
        && s.Split('.').Length == 4
        && IPAddress.TryParse(s, out var a)
        && a.AddressFamily == AddressFamily.InterNetwork;
}
