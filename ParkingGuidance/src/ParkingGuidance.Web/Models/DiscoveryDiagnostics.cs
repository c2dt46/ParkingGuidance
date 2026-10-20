namespace ParkingGuidance.Web.Models;

/// <summary>Thông tin chẩn đoán 1 lần dò ONVIF — giúp phân biệt "không có camera nào phản hồi"
/// (vấn đề mạng/tường lửa) với "có phản hồi nhưng không parse được" (vấn đề định dạng) hay
/// "có camera nhưng bị bộ lọc hãng loại" (chọn sai hãng).</summary>
public class DiscoveryDiagnostics
{
    public List<string> InterfacesUsed { get; set; } = new();
    public int RawPacketsReceived { get; set; }
    public int CamerasFoundBeforeBrandFilter { get; set; }
    public int CamerasAfterBrandFilter { get; set; }
}

public class DiscoveryResult
{
    public List<DiscoveredCamera> Cameras { get; set; } = new();
    public DiscoveryDiagnostics Diagnostics { get; set; } = new();
}
