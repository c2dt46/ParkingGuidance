using System.Text;

namespace ParkingGuidance.Web.Services.Sensor;

/// <summary>
/// Giao thức của hai loại cảm biến — chuyển nguyên từ PGS (TCPIP.cs / Main.cs). Chỉ chứa hàm thuần (không I/O) để dễ kiểm tra.
/// Mã trạng thái một cảm biến: 0 = trống, 1 = có xe, 2 = lỗi, 255 = chưa có dữ liệu.
/// </summary>
public static class SensorProtocol
{
    public const byte Free = 0;
    public const byte Occupied = 1;
    public const byte Error = 2;
    public const byte Unknown = 255;

    /// <summary>MGT200: PC kết nối tới converter và hỏi lần lượt từng ZCU (TCPIP.DataSendToZCU / Main.Bam_DataZCU).</summary>
    public static class Pm200
    {
        /// <summary>Độ dài dữ liệu còn lại (sau khi bỏ "Mnn" và "099") của 1 khung hợp lệ.</summary>
        public const int FrameLength = 185;
        public const int SensorsPerZcu = 160;

        /// <summary>
        /// Lệnh hỏi 1 ZCU (zcu = 0..31): ký tự 'p' + ID 2 chữ số + "0011900000" + CR.
        /// Ví dụ ZCU 0 → "p000011900000\r" (PGS gửi dạng hex 70 3030 30303131393030303030 0D).
        /// </summary>
        public static string BuildRequest(int zcu) => "p" + zcu.ToString("00") + "0011900000" + "\r";

        /// <summary>
        /// Lệnh CÀI ĐẶT 1 cảm biến (trang "Cài đặt cảm biến" của PGS, PM200Setting.thanhlaplenh):
        /// 'p' + ZCU(2 chữ số, 0–31) + Line(1 chữ số: 1–5, 9 = tất cả) + ID cảm biến(2 chữ số, 0–31, 99 = tất cả)
        /// + mã lệnh(2 chữ số, 01–07) + dữ liệu(3 chữ số) + "00" (PGS luôn gửi 00 ở vị trí checksum) + CR.
        /// Mã lệnh: 1 Ground auto set · 2 Ground manual set (data 100–600 cm) · 3 Saved Ground Time Report ·
        /// 4 Ground threshold set (data 0–200 cm) · 5 Saved Active Time Report · 6 Lamp Mode Set (data 0–9) · 7 Report Detect_Distance.
        /// </summary>
        public static string BuildCommand(int zcu, int channel, int sensor, int command, int data) =>
            "p" + zcu.ToString("00") + channel.ToString() + sensor.ToString("00") + command.ToString("00") + data.ToString("000") + "00" + "\r";

        /// <summary>Lệnh 7 — đọc khoảng cách đo được của 32 cảm biến trên 1 Line (PM200Setting.Doc_Khoang_cach_Tick): 'p' + ZCU(2) + "0012" + Line(1) + "00000" + CR.</summary>
        public static string BuildDistanceRequest(int zcu, int channel) =>
            "p" + zcu.ToString("00") + "0012" + channel.ToString() + "00000" + "\r";

        /// <summary>
        /// Phân tích trả lời lệnh 7: dài đúng 139 ký tự và chứa "M" + ZCU(2) + "0992". Bỏ 8 ký tự đầu, rồi mỗi cảm biến chiếm 4 ký tự
        /// (1 ký tự ngăn + 3 chữ số cm). Giá trị 0 = không đọc được; −1 = không đọc ra số.
        /// </summary>
        public static bool TryParseDistances(string? raw, int zcu, out int[] cm)
        {
            cm = Array.Empty<int>();
            if (raw is null || raw.Length != 139) return false;
            if (!raw.Contains("M" + zcu.ToString("00") + "0992")) return false;

            var s = raw.Substring(8);
            var result = new int[32];
            for (int i = 0; i < 32; i++)
                result[i] = int.TryParse(s.Substring(4 * i + 1, 3), out var v) ? v : -1;
            cm = result;
            return true;
        }

        /// <summary>
        /// Phân tích khung trả lời của ZCU: bỏ thẻ "Mnn" (M00..M31) và "099"; dữ liệu còn lại phải dài đúng 185 ký tự:
        /// 2 ký tự đầu bỏ đi, rồi 5 dòng × 32 cảm biến, mỗi dòng cách nhau 5 ký tự. Ký tự '-' = lỗi, '1' = có xe, còn lại = trống.
        /// </summary>
        public static bool TryParse(string? raw, out byte[] states)
        {
            states = Array.Empty<byte>();
            if (string.IsNullOrEmpty(raw)) return false;

            var s = raw;
            for (int i = 0; i < 32; i++)
            {
                var tag = "M" + i.ToString("00");
                if (s.Contains(tag))
                {
                    s = s.Replace(tag, "").Replace("099", "");
                    break;
                }
            }
            if (s.Length != FrameLength) return false;

            s = s.Substring(2);
            var sb = new StringBuilder(SensorsPerZcu);
            for (int line = 0; line < 5; line++)
            {
                sb.Append(s, 0, 32);
                if (line <= 3) s = s.Substring(37);
            }

            var all = sb.ToString();
            if (all.Length != SensorsPerZcu) return false;

            var result = new byte[SensorsPerZcu];
            for (int i = 0; i < SensorsPerZcu; i++)
                result[i] = all[i] == '-' ? Error : all[i] == '1' ? Occupied : Free;
            states = result;
            return true;
        }
    }

    /// <summary>Keytop: thiết bị tự gửi tới PC (Main.vitrikitu): nhiều nhóm "(Xs)" nối nhau, X = cảm biến 1–60 ('A'=1 … '|'=60), s = 0/1/2.</summary>
    public static class Keytop
    {
        public const int SensorsPerDevice = 60;
        public const int MaxPayload = 240;

        /// <summary>
        /// Phân tích 1 gói dữ liệu, ví dụ "(f2)(g2)(h2)(i2)(j2)". Gói hợp lệ khi dài chia hết cho 4, từ 4 đến 240 ký tự và mọi nhóm đúng dạng.
        /// Khác PGS ở một điểm: gói chỉ được áp dụng khi hợp lệ TOÀN BỘ (PGS áp dụng cả các nhóm đứng trước nhóm lỗi).
        /// </summary>
        public static bool TryParse(string? payload, out List<(int Sensor, byte State)> items)
        {
            items = new List<(int Sensor, byte State)>();
            var s = (payload ?? "").Trim('\0', ' ', '\r', '\n', '\t');
            if (s.Length < 4 || s.Length % 4 != 0 || s.Length > MaxPayload) return false;

            var list = new List<(int Sensor, byte State)>();
            for (int i = 0; i < s.Length; i += 4)
            {
                if (s[i] != '(' || s[i + 3] != ')') return false;
                int sensor = s[i + 1] - 64;
                if (sensor < 1 || sensor > SensorsPerDevice) return false;
                var st = s[i + 2];
                if (st != '0' && st != '1' && st != '2') return false;
                list.Add((sensor, (byte)(st - '0')));
            }
            items = list;
            return true;
        }

        /// <summary>Số thứ tự Keytop (0..) = octet cuối của IP gửi tới − octet cuối của IP ZCU0; −1 nếu khác dải /24 hoặc IP sai.</summary>
        public static int DeviceIndexFromIp(string? ip, string? zcu0Ip)
        {
            if (!TryOctets(ip, out var a) || !TryOctets(zcu0Ip, out var b)) return -1;
            if (a[0] != b[0] || a[1] != b[1] || a[2] != b[2]) return -1;
            return a[3] - b[3];
        }

        private static bool TryOctets(string? ip, out int[] octets)
        {
            octets = new int[4];
            var parts = (ip ?? "").Split('.');
            if (parts.Length != 4) return false;
            for (int i = 0; i < 4; i++)
                if (!int.TryParse(parts[i], out octets[i]) || octets[i] < 0 || octets[i] > 255) return false;
            return true;
        }
    }
}
