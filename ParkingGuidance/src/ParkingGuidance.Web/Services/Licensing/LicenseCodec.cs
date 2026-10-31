using System.Security.Cryptography;
using System.Text;

namespace ParkingGuidance.Web.Services.Licensing;

/// <summary>
/// Mã hoá / giải mã chuỗi giấy phép, yêu cầu và file trạng thái. 3 lớp bảo vệ:
///
///  1. CHỌN QUY LUẬT: mỗi lần mã hoá chọn ngẫu nhiên 1 bảng loại 1 (i = 0..9) và 1 bảng loại 2 (j = 0..9)
///     trong 20 bảng cố định ở <see cref="LicenseTables"/> → 100 quy luật. Cặp (i, j) KHÔNG được ghi trong chuỗi;
///     bên nhận thử lần lượt cả 100 cặp, đúng 1 cặp làm lớp 2 xác thực thành công.
///  2. MÃ HOÁ + XÁC THỰC AES-256-GCM: khoá sinh bằng HKDF-SHA256 từ (bí mật gốc lấy từ 20 bảng, mục đích, i, j).
///     Thẻ xác thực 96 bit phủ toàn bộ nội dung: chỉ cần sửa / thiếu / thừa 1 ký tự là giải mã thất bại.
///  3. XÁO KÝ TỰ BẰNG BẢNG: mỗi byte tách thành 2 ký tự trong 36 ký tự; ký tự ở vị trí chẵn đi qua bảng loại 1[i],
///     vị trí lẻ đi qua bảng loại 2[j], cộng thêm ký tự đứng trước và vị trí (kiểu CBC) nên sửa 1 ký tự
///     làm hỏng toàn bộ phần phía sau, và cùng nội dung nhưng khác cặp (i, j) cho chuỗi hoàn toàn khác.
///
/// "Mục đích" (REQ / LIC / STATE) nằm trong khoá và trong dữ liệu xác thực nên chuỗi yêu cầu không thể đem dùng làm
/// giấy phép và ngược lại.
/// </summary>
internal static class LicenseCodec
{
    private const string A = LicenseTables.Alphabet;
    private const int N = 36;
    private const int NonceLen = 12;
    private const int TagLen = 12;
    private const int Pairs = 10;

    private static readonly int[][] Fwd1;
    private static readonly int[][] Inv1;
    private static readonly int[][] Fwd2;
    private static readonly int[][] Inv2;
    private static readonly byte[] Master;

    static LicenseCodec()
    {
        Fwd1 = BuildForward(LicenseTables.Type1);
        Fwd2 = BuildForward(LicenseTables.Type2);
        Inv1 = BuildInverse(Fwd1);
        Inv2 = BuildInverse(Fwd2);

        var seed = string.Join("|", LicenseTables.Type1) + "#" + string.Join("|", LicenseTables.Type2) + "#PGS-LICENSE-v1";
        Master = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
    }

    private static int[][] BuildForward(string[] tables)
    {
        var res = new int[tables.Length][];
        for (var k = 0; k < tables.Length; k++)
        {
            var t = tables[k];
            if (t.Length != N) throw new InvalidOperationException("Bảng giấy phép phải đủ 36 ký tự.");
            var row = new int[N];
            for (var x = 0; x < N; x++)
            {
                row[x] = A.IndexOf(t[x]);
                if (row[x] < 0) throw new InvalidOperationException("Bảng giấy phép có ký tự ngoài 0-9 A-Z.");
            }
            res[k] = row;
        }
        return res;
    }

    private static int[][] BuildInverse(int[][] fwd)
    {
        var res = new int[fwd.Length][];
        for (var k = 0; k < fwd.Length; k++)
        {
            var inv = new int[N];
            for (var x = 0; x < N; x++) inv[fwd[k][x]] = x;
            res[k] = inv;
        }
        return res;
    }

    private static byte[] DeriveKey(string purpose, int i, int j) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Master, 32,
            Encoding.UTF8.GetBytes("PGS-SALT-" + purpose), new[] { (byte)i, (byte)j });

    // ------------------------------------------------------------------ mã hoá
    /// <summary>Mã hoá <paramref name="plain"/> thành chuỗi chữ-số (nhóm 5 ký tự cách nhau bằng gạch ngang).</summary>
    public static string Encode(string purpose, byte[] plain, int groupsPerLine = 0)
    {
        var i = RandomNumberGenerator.GetInt32(Pairs);
        var j = RandomNumberGenerator.GetInt32(Pairs);
        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagLen];

        using (var gcm = new AesGcm(DeriveKey(purpose, i, j), TagLen))
            gcm.Encrypt(nonce, plain, cipher, tag, Encoding.ASCII.GetBytes(purpose));

        var blob = new byte[NonceLen + TagLen + cipher.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceLen);
        Buffer.BlockCopy(tag, 0, blob, NonceLen, TagLen);
        Buffer.BlockCopy(cipher, 0, blob, NonceLen + TagLen, cipher.Length);

        return Format(Scramble(blob, i, j), groupsPerLine);
    }

    private static int[] Scramble(byte[] blob, int i, int j)
    {
        var outIdx = new int[blob.Length * 2];
        var prev = 0;
        for (var b = 0; b < blob.Length; b++)
        {
            for (var h = 0; h < 2; h++)
            {
                var p = b * 2 + h;
                var v = h == 0 ? blob[b] / N : blob[b] % N;
                var table = (p % 2 == 0) ? Fwd1[i] : Fwd2[j];
                var o = table[(v + prev + p * 7) % N];
                outIdx[p] = o;
                prev = o;
            }
        }
        return outIdx;
    }

    private static string Format(int[] idx, int groupsPerLine)
    {
        var sb = new StringBuilder(idx.Length + idx.Length / 5 + 8);
        for (var p = 0; p < idx.Length; p++)
        {
            if (p > 0 && p % 5 == 0)
            {
                var group = p / 5;
                if (groupsPerLine > 0 && group % groupsPerLine == 0) sb.Append('\n');
                else sb.Append('-');
            }
            sb.Append(A[idx[p]]);
        }
        return sb.ToString();
    }

    // --------------------------------------------------------------- giải mã
    /// <summary>Đưa văn bản người dùng dán vào về dạng chuẩn: viết hoa, bỏ khoảng trắng / gạch ngang. Null nếu có ký tự lạ.</summary>
    public static int[]? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var list = new List<int>(text.Length);
        foreach (var raw in text)
        {
            if (char.IsWhiteSpace(raw) || raw == '-' || raw == '\u2010' || raw == '\u2013' || raw == '\u2014') continue;
            var k = A.IndexOf(char.ToUpperInvariant(raw));
            if (k < 0) return null;
            list.Add(k);
        }
        return list.ToArray();
    }

    /// <summary>Chuỗi ở dạng chuẩn (chỉ 0-9 A-Z, không gạch) — dùng để lưu / so sánh.</summary>
    public static string Normalize(string text)
    {
        var idx = Parse(text);
        if (idx is null) return "";
        var sb = new StringBuilder(idx.Length);
        foreach (var k in idx) sb.Append(A[k]);
        return sb.ToString();
    }

    /// <summary>Thử cả 100 quy luật; trả về nội dung gốc nếu đúng 1 quy luật xác thực thành công.</summary>
    public static bool TryDecode(string purpose, string? text, out byte[] plain)
    {
        plain = Array.Empty<byte>();
        var idx = Parse(text);
        if (idx is null || idx.Length % 2 != 0 || idx.Length < 2 * (NonceLen + TagLen)) return false;

        var ad = Encoding.ASCII.GetBytes(purpose);
        for (var i = 0; i < Pairs; i++)
        {
            for (var j = 0; j < Pairs; j++)
            {
                var blob = Unscramble(idx, i, j);
                if (blob is null) continue;

                var nonce = new byte[NonceLen];
                var tag = new byte[TagLen];
                var cipher = new byte[blob.Length - NonceLen - TagLen];
                Buffer.BlockCopy(blob, 0, nonce, 0, NonceLen);
                Buffer.BlockCopy(blob, NonceLen, tag, 0, TagLen);
                Buffer.BlockCopy(blob, NonceLen + TagLen, cipher, 0, cipher.Length);

                try
                {
                    var result = new byte[cipher.Length];
                    using var gcm = new AesGcm(DeriveKey(purpose, i, j), TagLen);
                    gcm.Decrypt(nonce, cipher, tag, result, ad);
                    plain = result;
                    return true;
                }
                catch (CryptographicException)
                {
                    // sai quy luật (hoặc chuỗi đã bị sửa) — thử cặp kế tiếp
                }
            }
        }
        return false;
    }

    private static byte[]? Unscramble(int[] y, int i, int j)
    {
        var n = y.Length / 2;
        var bytes = new byte[n];
        for (var b = 0; b < n; b++)
        {
            var hi = 0;
            var lo = 0;
            for (var h = 0; h < 2; h++)
            {
                var p = b * 2 + h;
                var prev = p == 0 ? 0 : y[p - 1];
                var inv = (p % 2 == 0) ? Inv1[i] : Inv2[j];
                var v = (((inv[y[p]] - prev - p * 7) % N) + N) % N;
                if (h == 0) hi = v; else lo = v;
            }
            var val = hi * N + lo;
            if (val > 255) return null;
            bytes[b] = (byte)val;
        }
        return bytes;
    }
}
