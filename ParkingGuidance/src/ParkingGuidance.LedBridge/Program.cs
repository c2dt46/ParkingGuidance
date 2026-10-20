using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParkingGuidance.LedBridge;

/// <summary>
/// Tiến trình phụ (x86) gọi HDSDK.dll. Đọc từng dòng JSON từ stdin, xử lý TUẦN TỰ (SDK dùng 1 màn hình toàn cục, không an toàn đa luồng),
/// ghi 1 dòng JSON phản hồi ra stdout; log chẩn đoán ra stderr. Thoát khi stdin đóng (web app tắt).
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // PGS luôn gửi bằng IP cố định: m_nSendType = 0 (SendtoDHCard.setIP)
    private const int SendTypeIp = 0;

    private static int Main()
    {
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

        string? line;
        while ((line = stdin.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            Response resp;
            long id = 0;
            try
            {
                var req = JsonSerializer.Deserialize<Request>(line, Json) ?? throw new InvalidDataException("Yêu cầu rỗng.");
                id = req.Id;
                resp = Handle(req);
            }
            catch (DllNotFoundException ex) { resp = Fail("Không nạp được HDSDK.dll: " + ex.Message, -10); }
            catch (BadImageFormatException ex) { resp = Fail("HDSDK.dll sai kiến trúc (cần tiến trình x86): " + ex.Message, -11); }
            catch (EntryPointNotFoundException ex) { resp = Fail("HDSDK.dll thiếu hàm: " + ex.Message, -12); }
            catch (Exception ex) { resp = Fail(ex.GetType().Name + ": " + ex.Message, -1); }

            resp.Id = id;
            stdout.WriteLine(JsonSerializer.Serialize(resp, Json));
        }
        return 0;
    }

    private static Response Handle(Request r)
    {
        var sw = Stopwatch.StartNew();
        // Chuỗi truyền SDK là con trỏ Unicode — như PGS: Marshal.StringToHGlobalUni(IP)
        IntPtr ip = Marshal.StringToHGlobalUni(r.Ip ?? "");
        try
        {
            var res = r.Cmd switch
            {
                "ping" => Ok(),
                "online" => HdSdk.Cmd_IsCardOnline(SendTypeIp, ip, IntPtr.Zero) == 0 ? Ok() : Fail("Card không phản hồi.", -1),
                "send" => Send(r, ip),
                _ => Fail("Lệnh không hỗ trợ: " + r.Cmd, -20)
            };
            res.Ms = (int)sw.ElapsedMilliseconds;
            return res;
        }
        finally { Marshal.FreeHGlobal(ip); }
    }

    /// <summary>
    /// Luồng gửi — y hệt SendtoDHCard.run của PGS:
    ///   Hd_CreateScreen (0 = OK) → Hd_AddProgram(NULL,1,0) (-1 = lỗi) →
    ///   mỗi vùng: Hd_AddArea (-1 = lỗi) + Hd_AddSimpleTextAreaItem / Hd_AddImageAreaItem (-1 = lỗi) → Hd_SendScreen (0 = OK).
    /// </summary>
    private static Response Send(Request r, IntPtr ip)
    {
        if (r.Screen is null) return Fail("Thiếu thông số màn hình.", -21);
        var s = r.Screen;

        int rc = HdSdk.Hd_CreateScreen(s.Width, s.Height, s.Color, s.Gray, s.CardType, IntPtr.Zero, 0);
        if (rc != 0) return SdkFail("Hd_CreateScreen", rc);

        int program = HdSdk.Hd_AddProgram(IntPtr.Zero, 1, 0, IntPtr.Zero, 0);
        if (program == -1) return SdkFail("Hd_AddProgram", program);

        foreach (var a in r.Areas ?? new())
        {
            int area = HdSdk.Hd_AddArea(program, a.X, a.Y, a.W, a.H, IntPtr.Zero, 0, 0, IntPtr.Zero, 0);
            if (area == -1) return SdkFail($"Hd_AddArea({a.X},{a.Y},{a.W},{a.H})", area);

            int item;
            if (a.Image)
            {
                if (string.IsNullOrEmpty(a.Path) || !File.Exists(a.Path)) return Fail($"Không thấy file ảnh: {a.Path}", -22);
                IntPtr path = Marshal.StringToHGlobalUni(a.Path);
                try { item = HdSdk.Hd_AddImageAreaItem(area, path, a.Effect, a.Speed, 201, a.Stay, IntPtr.Zero, 0); }
                finally { Marshal.FreeHGlobal(path); }
                if (item == -1) return SdkFail("Hd_AddImageAreaItem", item);
            }
            else
            {
                IntPtr text = Marshal.StringToHGlobalUni(a.Text);
                IntPtr font = Marshal.StringToHGlobalUni(a.FontName);
                try
                {
                    int color = HdSdk.Hd_GetColor(a.R, a.G, a.B);
                    int style = 0x0004 | 0x0100 | 0x0000; // như PGS (nTextStyle)
                    item = HdSdk.Hd_AddSimpleTextAreaItem(area, text, color, 0, style, font, a.FontSize, a.Effect, a.Speed, 201, a.Stay, IntPtr.Zero, 0);
                }
                finally { Marshal.FreeHGlobal(text); Marshal.FreeHGlobal(font); }
                if (item == -1) return SdkFail("Hd_AddSimpleTextAreaItem", item);
            }
        }

        rc = HdSdk.Hd_SendScreen(SendTypeIp, ip, IntPtr.Zero, IntPtr.Zero, 0);
        return rc != 0 ? SdkFail("Hd_SendScreen", rc) : Ok();
    }

    private static Response Ok() => new() { Ok = true };
    private static Response Fail(string error, int code) => new() { Ok = false, Code = code, Error = error };
    private static Response SdkFail(string fn, int rc) => Fail($"{fn} trả về {rc}, mã lỗi SDK = {HdSdk.Hd_GetSDKLastError()}", rc);

    // ---- Giao thức (khớp BridgeLedTransport) ----
    private sealed class Request
    {
        public long Id { get; set; }
        public string Cmd { get; set; } = "";
        public string? Ip { get; set; }
        public Screen? Screen { get; set; }
        public List<Area>? Areas { get; set; }
    }
    private sealed class Screen { public int Width { get; set; } public int Height { get; set; } public int Color { get; set; } public int Gray { get; set; } public int CardType { get; set; } }
    private sealed class Area
    {
        public int X { get; set; } public int Y { get; set; } public int W { get; set; } public int H { get; set; }
        public bool Image { get; set; } public string Text { get; set; } = ""; public string Path { get; set; } = "";
        public int R { get; set; } public int G { get; set; } public int B { get; set; }
        public string FontName { get; set; } = ""; public int FontSize { get; set; }
        public int Effect { get; set; } public int Speed { get; set; } public int Stay { get; set; }
    }
    private sealed class Response { public long Id { get; set; } public bool Ok { get; set; } public int Code { get; set; } public string? Error { get; set; } public int Ms { get; set; } }
}
