using ParkingGuidance.Web.Models;

namespace ParkingGuidance.Web.Services.CameraRegistry;

public interface IOnvifDiscoveryService
{
    /// <summary>Dò tìm camera IP hỗ trợ ONVIF trên mạng LAN (WS-Discovery, UDP multicast).
    /// brandKeyword: lọc theo hãng (vd "hikvision"); null/rỗng = không lọc, trả về mọi camera tìm được.</summary>
    Task<DiscoveryResult> DiscoverAsync(string? brandKeyword, TimeSpan timeout, CancellationToken ct);
}
