using System.Text;

namespace ParkingGuidance.Web.Services.Licensing;

internal sealed class LedgerEntry
{
    public ulong Id { get; set; }
    /// <summary>Số giây đã sử dụng của giấy phép này (chỉ tăng).</summary>
    public long ConsumedSec { get; set; }
    public long ActivatedUnix { get; set; }
}

internal sealed class StateData
{
    public long Seq { get; set; }
    public string ActiveText { get; set; } = "";
    public List<LedgerEntry> Ledger { get; set; } = new();
}

/// <summary>Bố cục nhị phân (trước khi mã hoá) của yêu cầu, giấy phép và file trạng thái.</summary>
internal static class LicenseFormats
{
    public const string PurposeRequest = "REQ";
    public const string PurposeLicense = "LIC";
    public const string PurposeState = "STATE";

    private const byte Version = 1;
    private const int MaxSerialLen = 40;

    // ------------------------------------------------------------- phần cứng
    private static void WriteHardware(BinaryWriter w, IReadOnlyList<HardwareId> hw)
    {
        w.Write((byte)hw.Count);
        foreach (var h in hw)
        {
            var b = Encoding.ASCII.GetBytes(h.Serial);
            w.Write((byte)h.Kind);
            w.Write((byte)b.Length);
            w.Write(b);
        }
    }

    private static List<HardwareId>? ReadHardware(BinaryReader r)
    {
        int n = r.ReadByte();
        if (n == 0) return null;
        var list = new List<HardwareId>(n);
        for (var k = 0; k < n; k++)
        {
            int kind = r.ReadByte();
            if (kind < 1 || kind > 3) return null;
            int len = r.ReadByte();
            if (len == 0 || len > MaxSerialLen) return null;
            var b = r.ReadBytes(len);
            if (b.Length != len) return null;
            foreach (var c in b)
            {
                var ok = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z');
                if (!ok) return null;
            }
            list.Add(new HardwareId((HardwareKind)kind, Encoding.ASCII.GetString(b)));
        }
        return list;
    }

    // --------------------------------------------------------------- yêu cầu
    public static string BuildRequest(IReadOnlyList<HardwareId> hw)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Version);
            WriteHardware(w, hw);
        }
        return LicenseCodec.Encode(PurposeRequest, ms.ToArray(), groupsPerLine: 8);
    }

    public static bool TryParseRequest(string? text, out List<HardwareId>? hw)
    {
        hw = null;
        if (!LicenseCodec.TryDecode(PurposeRequest, text, out var plain)) return false;
        try
        {
            using var ms = new MemoryStream(plain);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            if (r.ReadByte() != Version) return false;
            var list = ReadHardware(r);
            if (list is null || ms.Position != ms.Length) return false;
            hw = list;
            return true;
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ArgumentException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------ giấy phép
    public static string BuildLicense(LicenseData lic)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Version);
            w.Write((byte)lic.Duration);
            w.Write(lic.IssuedUtc.ToUnixTimeSeconds());
            w.Write(lic.Id);
            WriteHardware(w, lic.Hardware);
        }
        return LicenseCodec.Encode(PurposeLicense, ms.ToArray(), groupsPerLine: 8);
    }

    public static bool TryParseLicense(string? text, out LicenseData? lic)
    {
        lic = null;
        if (!LicenseCodec.TryDecode(PurposeLicense, text, out var plain)) return false;
        try
        {
            using var ms = new MemoryStream(plain);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            if (r.ReadByte() != Version) return false;

            var durByte = r.ReadByte();
            if (durByte < (byte)LicenseDuration.Month1 || durByte > (byte)LicenseDuration.Permanent) return false;

            var issued = r.ReadInt64();
            var id = r.ReadUInt64();
            var hw = ReadHardware(r);
            if (hw is null || ms.Position != ms.Length) return false;

            lic = new LicenseData(id, (LicenseDuration)durByte, DateTimeOffset.FromUnixTimeSeconds(issued), hw);
            return true;
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ArgumentException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------ trạng thái
    public static string BuildState(StateData s)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Version);
            w.Write(s.Seq);
            w.Write(s.ActiveText ?? "");
            w.Write(s.Ledger.Count);
            foreach (var e in s.Ledger)
            {
                w.Write(e.Id);
                w.Write(e.ConsumedSec);
                w.Write(e.ActivatedUnix);
            }
        }
        return LicenseCodec.Encode(PurposeState, ms.ToArray(), groupsPerLine: 10);
    }

    public static StateData? TryParseState(string? text)
    {
        if (!LicenseCodec.TryDecode(PurposeState, text, out var plain)) return null;
        try
        {
            using var ms = new MemoryStream(plain);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            if (r.ReadByte() != Version) return null;

            var st = new StateData { Seq = r.ReadInt64(), ActiveText = r.ReadString() };
            var count = r.ReadInt32();
            if (count < 0 || count > 100000) return null;
            for (var k = 0; k < count; k++)
            {
                st.Ledger.Add(new LedgerEntry
                {
                    Id = r.ReadUInt64(),
                    ConsumedSec = r.ReadInt64(),
                    ActivatedUnix = r.ReadInt64()
                });
            }
            return ms.Position == ms.Length ? st : null;
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ArgumentException or FormatException)
        {
            return null;
        }
    }
}
