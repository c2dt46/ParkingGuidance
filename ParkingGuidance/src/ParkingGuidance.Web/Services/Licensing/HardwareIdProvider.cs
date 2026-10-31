using System.Management;
using System.Net.NetworkInformation;

namespace ParkingGuidance.Web.Services.Licensing;

public interface IHardwareIdProvider
{
    /// <summary>Đọc số seri ổ cứng (cố định, không tính USB), tất cả card mạng vật lý và main của máy này.
    /// Chạy chậm (WMI) nên chỉ gọi từ luồng nền / khi người dùng bấm nút.</summary>
    IReadOnlyList<HardwareId> Read();
}

public sealed class HardwareIdProvider : IHardwareIdProvider
{
    // Giá trị "điền tạm" mà nhà sản xuất hay để lại — không dùng làm số seri.
    private static readonly HashSet<string> Junk = new(StringComparer.Ordinal)
    {
        "NONE", "NA", "NULL", "UNKNOWN", "DEFAULTSTRING", "TOBEFILLEDBYOEM", "SERIALNUMBER", "SYSTEMSERIALNUMBER",
        "BASEBOARDSERIALNUMBER", "NOTAPPLICABLE", "NOTSPECIFIED", "123456789", "1234567890", "0123456789"
    };

    private readonly ILogger<HardwareIdProvider> _log;

    public HardwareIdProvider(ILogger<HardwareIdProvider> log) => _log = log;

    public IReadOnlyList<HardwareId> Read()
    {
        var result = new List<HardwareId>();

        TryAdd(result, HardwareKind.Disk, ReadDisks, "ổ cứng");
        TryAdd(result, HardwareKind.Network, ReadNetworkCards, "card mạng");
        TryAdd(result, HardwareKind.Board, ReadBoard, "main");

        return result
            .Distinct()
            .OrderBy(h => h.Kind)
            .ThenBy(h => h.Serial, StringComparer.Ordinal)
            .Take(24)
            .ToList();
    }

    private void TryAdd(List<HardwareId> into, HardwareKind kind, Func<IEnumerable<string?>> read, string what)
    {
        try
        {
            foreach (var raw in read())
            {
                var s = Clean(raw);
                if (s is not null) into.Add(new HardwareId(kind, s));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Không đọc được số seri {What}.", what);
        }
    }

    /// <summary>Chuẩn hoá: chỉ giữ 0-9 A-Z (viết hoa); bỏ giá trị rỗng / điền tạm / lặp 1 ký tự.</summary>
    internal static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var chars = new List<char>(raw.Length);
        foreach (var c in raw)
            if (char.IsAsciiLetterOrDigit(c)) chars.Add(char.ToUpperInvariant(c));
        if (chars.Count < 4 || chars.Count > 40) return null;

        var s = new string(chars.ToArray());
        if (Junk.Contains(s)) return null;
        if (s.Distinct().Count() == 1) return null;
        return s;
    }

    // ---------------------------------------------------------------- ổ cứng
    private IEnumerable<string?> ReadDisks()
    {
        if (OperatingSystem.IsWindows())
        {
            var rows = Wmi("SELECT SerialNumber, InterfaceType, MediaType FROM Win32_DiskDrive",
                "SerialNumber", "InterfaceType", "MediaType");
            foreach (var r in rows)
            {
                var iface = r["InterfaceType"] ?? "";
                var media = r["MediaType"] ?? "";
                if (iface.Equals("USB", StringComparison.OrdinalIgnoreCase)) continue;      // ổ cắm ngoài
                if (media.Contains("Removable", StringComparison.OrdinalIgnoreCase)) continue;
                yield return r["SerialNumber"];
            }
            yield break;
        }

        if (!Directory.Exists("/sys/block")) yield break;
        foreach (var dir in Directory.GetDirectories("/sys/block"))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith("loop") || name.StartsWith("ram") || name.StartsWith("dm-") ||
                name.StartsWith("sr") || name.StartsWith("zram")) continue;

            string? serial = null;
            try
            {
                var removable = Path.Combine(dir, "removable");
                if (File.Exists(removable) && File.ReadAllText(removable).Trim() == "1") continue;
                var f = Path.Combine(dir, "device", "serial");
                if (File.Exists(f)) serial = File.ReadAllText(f).Trim();
            }
            catch (IOException) { /* bỏ qua ổ này */ }
            catch (UnauthorizedAccessException) { /* bỏ qua ổ này */ }
            if (serial is not null) yield return serial;
        }
    }

    // -------------------------------------------------------------- card mạng
    private IEnumerable<string?> ReadNetworkCards()
    {
        var found = new List<string?>();

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var rows = Wmi("SELECT MACAddress, Name, PNPDeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE",
                    "MACAddress", "Name", "PNPDeviceID");
                foreach (var r in rows)
                {
                    var name = r["Name"] ?? "";
                    var pnp = r["PNPDeviceID"] ?? "";
                    if (pnp.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase) ||
                        pnp.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase)) continue;
                    if (IsVirtualName(name)) continue;
                    found.Add(r["MACAddress"]);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "WMI card mạng lỗi, dùng NetworkInterface.");
            }
            if (found.Count > 0) return found;
        }

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or
                NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or
                NetworkInterfaceType.Wireless80211)) continue;
            if (IsVirtualName(nic.Description) || IsVirtualName(nic.Name)) continue;

            var mac = nic.GetPhysicalAddress().GetAddressBytes();
            if (mac.Length != 6) continue;
            if ((mac[0] & 0x02) != 0) continue;                 // địa chỉ do phần mềm đặt (card ảo)
            found.Add(Convert.ToHexString(mac));
        }
        return found;
    }

    private static bool IsVirtualName(string name)
    {
        var n = name.ToLowerInvariant();
        string[] bad = { "virtual", "vmware", "vbox", "hyper-v", "vethernet", "vpn", "tap-", "tunnel", "bluetooth",
                         "loopback", "pseudo", "docker", "wsl", "miniport", "npcap", "wintun", "tailscale", "zerotier" };
        return bad.Any(b => n.Contains(b));
    }

    // ------------------------------------------------------------------- main
    private IEnumerable<string?> ReadBoard()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var r in Wmi("SELECT SerialNumber FROM Win32_BaseBoard", "SerialNumber"))
                yield return r["SerialNumber"];
            yield break;
        }

        string? serial = null;
        try
        {
            const string f = "/sys/class/dmi/id/board_serial";
            if (File.Exists(f)) serial = File.ReadAllText(f).Trim();
        }
        catch (IOException) { /* thường cần quyền root */ }
        catch (UnauthorizedAccessException) { /* thường cần quyền root */ }
        if (serial is not null) yield return serial;
    }

    // -------------------------------------------------------------------- WMI
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<Dictionary<string, string?>> Wmi(string wql, params string[] props)
    {
        var rows = new List<Dictionary<string, string?>>();
        using var searcher = new ManagementObjectSearcher(wql);
        using var collection = searcher.Get();
        foreach (ManagementBaseObject mo in collection)
        {
            using (mo)
            {
                var row = new Dictionary<string, string?>();
                foreach (var p in props) row[p] = mo[p]?.ToString();
                rows.Add(row);
            }
        }
        return rows;
    }
}
