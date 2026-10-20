using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using ParkingGuidance.Web.Models;

namespace ParkingGuidance.Web.Services.CameraRegistry;

/// <summary>
/// Dò tìm camera IP hỗ trợ chuẩn ONVIF trên mạng LAN bằng WS-Discovery (gửi gói UDP multicast
/// "Probe" tới 239.255.255.250:3702, lắng nghe phản hồi "ProbeMatches" trong thời gian chờ).
/// Đây là giao thức chuẩn công nghiệp — hầu hết camera IP đời sau ~2012 (Hikvision, Dahua, Axis,
/// Bosch, Hanwha, Uniview, Vivotek...) đều hỗ trợ, không phụ thuộc hãng.
///
/// QUAN TRỌNG: gửi gói Probe qua TỪNG CARD MẠNG đang hoạt động (không chỉ 1 lần duy nhất theo
/// route mặc định của hệ điều hành). Đây là lỗi rất hay gặp với multicast UDP trên máy có nhiều
/// card mạng (card LAN thật + card ảo VPN/Hyper-V/VMware/Docker/WSL...) — hệ điều hành có thể chọn
/// gửi gói multicast qua card ảo thay vì card LAN thật nối với camera, khiến "dò không ra" dù
/// camera hoàn toàn bình thường và cùng mạng. Gửi qua mọi card đang hoạt động giải quyết đúng vấn đề này.
///
/// GHI CHÚ KHÁC:
/// - Server chạy ứng dụng này vẫn phải nằm cùng mạng LAN/VLAN vật lý với camera — multicast không
///   đi qua router/NAT khác lớp mạng.
/// - Đa số camera phản hồi UNICAST thẳng về cổng nguồn của gói Probe — code này lắng nghe theo kiểu
///   đó. Một số ít thiết bị phản hồi qua multicast tới cổng 3702 thay vì unicast; trường hợp này cần
///   bind cổng 3702 + join multicast group (chưa triển khai do không có phần cứng để kiểm thử).
/// - Tường lửa Windows/máy chủ có thể chặn phản hồi UDP gửi về — nếu "RawPacketsReceived" trong kết
///   quả chẩn đoán luôn bằng 0 dù chắc chắn camera cùng mạng, kiểm tra tường lửa trước tiên.
/// - Việc nhận diện HÃNG dựa vào chuỗi Scopes camera tự khai báo — không phải lúc nào cũng đúng
///   chuẩn; nếu không nhận diện được, chọn "Khác / ONVIF chuẩn" để xem toàn bộ camera dò được.
/// </summary>
public class OnvifDiscoveryService : IOnvifDiscoveryService
{
    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("239.255.255.250"), 3702);
    private static readonly XNamespace D = "http://schemas.xmlsoap.org/ws/2005/04/discovery";

    private readonly ILogger<OnvifDiscoveryService> _logger;

    public OnvifDiscoveryService(ILogger<OnvifDiscoveryService> logger) => _logger = logger;

    public async Task<DiscoveryResult> DiscoverAsync(string? brandKeyword, TimeSpan timeout, CancellationToken ct)
    {
        var diagnostics = new DiscoveryDiagnostics();
        var found = new Dictionary<string, DiscoveredCamera>(); // key = "ip:port", khu trung lap

        using var udp = new UdpClient(0);
        udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 8);

        var probeBytes = Encoding.UTF8.GetBytes(BuildProbeMessage());

        // Gui qua TUNG card mang dang hoat dong, khong chi 1 lan theo route mac dinh cua he dieu
        // hanh — xem ghi chu dau class ve ly do.
        var localAddresses = GetCandidateLocalAddresses();
        if (localAddresses.Count == 0)
        {
            _logger.LogWarning("Khong tim thay card mang IPv4 nao dang hoat dong. Gui thu theo route mac dinh.");
            try
            {
                await udp.SendAsync(probeBytes, probeBytes.Length, MulticastEndpoint);
                diagnostics.InterfacesUsed.Add("(route mac dinh)");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Khong gui duoc goi ONVIF Probe.");
            }
        }
        else
        {
            foreach (var addr in localAddresses)
            {
                try
                {
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, addr.GetAddressBytes());
                    await udp.SendAsync(probeBytes, probeBytes.Length, MulticastEndpoint);
                    diagnostics.InterfacesUsed.Add(addr.ToString());
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Khong gui duoc goi Probe qua card mang {Addr}", addr);
                }
            }
        }

        if (diagnostics.InterfacesUsed.Count == 0)
        {
            _logger.LogWarning("Khong gui duoc goi ONVIF Probe qua bat ky card mang nao. Tra ve danh sach rong.");
            return new DiscoveryResult { Diagnostics = diagnostics };
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        while (!cts.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                break; // Het thoi gian cho - ket thuc binh thuong
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Loi khi nhan phan hoi ONVIF, bo qua goi nay.");
                continue;
            }

            diagnostics.RawPacketsReceived++;

            var camera = ParseProbeMatch(Encoding.UTF8.GetString(result.Buffer));
            if (camera is not null)
                found[$"{camera.IpAddress}:{camera.Port}"] = camera;
        }

        var list = found.Values.ToList();
        diagnostics.CamerasFoundBeforeBrandFilter = list.Count;

        if (!string.IsNullOrWhiteSpace(brandKeyword))
        {
            list = list.Where(c => (c.Manufacturer ?? "").Contains(brandKeyword, StringComparison.OrdinalIgnoreCase))
                       .ToList();
        }
        diagnostics.CamerasAfterBrandFilter = list.Count;

        _logger.LogInformation(
            "Do ONVIF xong: {Interfaces} card mang, {Raw} goi phan hoi tho, {Before} camera truoc loc, " +
            "{After} camera sau loc hang '{Brand}'.",
            diagnostics.InterfacesUsed.Count, diagnostics.RawPacketsReceived,
            diagnostics.CamerasFoundBeforeBrandFilter, diagnostics.CamerasAfterBrandFilter,
            string.IsNullOrWhiteSpace(brandKeyword) ? "(khong loc)" : brandKeyword);

        return new DiscoveryResult { Cameras = list, Diagnostics = diagnostics };
    }

    /// <summary>Lấy địa chỉ IPv4 của mọi card mạng đang hoạt động (bỏ qua loopback) — để gửi gói
    /// dò qua từng card thay vì chỉ 1 lần theo route mặc định của hệ điều hành.</summary>
    private static List<IPAddress> GetCandidateLocalAddresses()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        result.Add(addr.Address);
                }
            }
        }
        catch
        {
            // Bo qua loi liet ke card mang (hiem gap) - tra ve danh sach rong, ham goi se tu xu ly phuong an du phong
        }
        return result;
    }

    private static string BuildProbeMessage()
    {
        var messageId = Guid.NewGuid();
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope"
                        xmlns:w="http://schemas.xmlsoap.org/ws/2004/08/addressing"
                        xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery"
                        xmlns:dn="http://www.onvif.org/ver10/network/wsdl">
              <e:Header>
                <w:MessageID>uuid:{messageId}</w:MessageID>
                <w:To e:mustUnderstand="1">urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>
                <w:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>
              </e:Header>
              <e:Body>
                <d:Probe>
                  <d:Types>dn:NetworkVideoTransmitter</d:Types>
                </d:Probe>
              </e:Body>
            </e:Envelope>
            """;
    }

    private DiscoveredCamera? ParseProbeMatch(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var probeMatch = doc.Descendants(D + "ProbeMatch").FirstOrDefault();
            if (probeMatch is null) return null;

            var xAddrsText = probeMatch.Element(D + "XAddrs")?.Value ?? "";
            var scopesText = probeMatch.Element(D + "Scopes")?.Value ?? "";

            var firstAddr = xAddrsText.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (firstAddr is null || !Uri.TryCreate(firstAddr, UriKind.Absolute, out var uri))
                return null;

            var manufacturer = ExtractScopeToken(scopesText, "onvif://www.onvif.org/name/")
                             ?? ExtractScopeToken(scopesText, "onvif://www.onvif.org/manufacturer/");
            var model = ExtractScopeToken(scopesText, "onvif://www.onvif.org/hardware/");

            return new DiscoveredCamera
            {
                IpAddress = uri.Host,
                Port = uri.IsDefaultPort ? 80 : uri.Port,
                Manufacturer = manufacturer,
                Model = model,
                OnvifServiceUrl = firstAddr
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Khong parse duoc phan hoi ONVIF, bo qua.");
            return null;
        }
    }

    private static string? ExtractScopeToken(string scopesText, string prefix)
    {
        var token = scopesText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (token is null) return null;

        var value = Uri.UnescapeDataString(token[prefix.Length..]).Replace('_', ' ').Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
