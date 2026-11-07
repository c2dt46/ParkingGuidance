using System.Text;
using ParkingGuidance.Web.Models;

namespace ParkingGuidance.Web.Services.Led;

/// <summary>Dựng nội dung gửi card và "khoá dữ liệu" so sánh — chuyển thể SendtoDHCard.run / Datacompare / DataNumber của PGS.</summary>
public static class LedFrameBuilder
{
    /// <summary>Giá trị vùng Number = biến MS + biến MC + project tag đã chọn (mục trống = 0), không âm.</summary>
    public static int NumberValue(LedAreaRow r, LedDataSnapshot s)
    {
        long v = LedDataSnapshot.Lookup(s.Ms, r.NMs) + LedDataSnapshot.Lookup(s.Mc, r.NMc) + LedDataSnapshot.Lookup(s.Tags, r.NTag);
        return (int)Math.Clamp(v, 0, 999_999_999);
    }

    /// <summary>Nội dung vùng Chữ: chọn biến string (project tag) thì lấy giá trị hiện tại của tag; chưa chọn / tag không còn thì dùng chữ cố định.</summary>
    public static string TextValue(LedAreaRow r, LedDataSnapshot s) =>
        !string.IsNullOrEmpty(r.TTag) && s.Strings.TryGetValue(r.TTag, out var v) ? v : r.Ttext;

    /// <summary>
    /// Khoá dữ liệu của card — như Data_To_HDCard của PGS: gồm giá trị hiển thị của các vùng Number
    /// và nội dung của các vùng Chữ gắn biến string. Khoá khác lần gửi thành công gần nhất → card được gửi lại;
    /// gửi lỗi thì khoá "đã gửi" không đổi nên lần sau vẫn tự gửi lại (lùi dần, tuần tự từng card).
    /// Chữ cố định / ảnh không nằm trong khoá: chỉ gửi lần đầu, khi bấm "Gửi tới card" hoặc đi kèm lần gửi khi dữ liệu đổi (đúng PGS).
    /// </summary>
    public static string DataKey(LedCard card, LedDataSnapshot s)
    {
        var sb = new StringBuilder();
        foreach (var r in card.Areas)
        {
            if (r.Type == LedAreaType.Number) sb.Append(NumberValue(r, s)).Append(';');
            else if (r.Type == LedAreaType.Text && !string.IsNullOrEmpty(r.TTag))
            {
                var t = TextValue(r, s);
                sb.Append('T').Append(t.Length).Append(':').Append(t).Append(';');   // độ dài đứng trước → không nhập nhằng dù chuỗi chứa ';'
            }
        }
        return sb.ToString();
    }

    /// <summary>Dựng toàn bộ nội dung 1 card. <paramref name="resolveImage"/> đổi đường dẫn lưu trong cấu hình thành đường dẫn thật trên máy.</summary>
    public static LedSendFrame Build(LedCard card, LedDataSnapshot s, Func<string, string> resolveImage)
    {
        var areas = new List<LedSendArea>(card.Areas.Count);
        foreach (var r in card.Areas)
        {
            switch (r.Type)
            {
                case LedAreaType.Text:
                {
                    var (cr, cg, cb) = HdCardCatalog.TextRgb[r.Tcolor];
                    areas.Add(new LedSendArea(r.OriginX, r.OriginY, r.Width, r.Height, false, TextValue(r, s), "",
                        cr, cg, cb, r.Tfont_Type, r.Tfont, r.Teffects, r.Tspeed, r.Tstopover));
                    break;
                }
                case LedAreaType.Number:
                {
                    int value = NumberValue(r, s);
                    var text = value.ToString().PadLeft(r.Nquantity, '0');
                    int color = r.Nmode == 0 ? r.Ncolor : (value < r.NlimitR ? 0 : value < r.NlimitY ? 1 : 2);
                    if (value == 0 && r.NFull == 1) text = "FULL";
                    var (cr, cg, cb) = HdCardCatalog.TextRgb[color];
                    // Vùng số: hiệu ứng 0, tốc độ 0, độ trễ 100 (đúng PGS)
                    areas.Add(new LedSendArea(r.OriginX, r.OriginY, r.Width, r.Height, false, text, "",
                        cr, cg, cb, r.Nfont_Type, r.Nfont, 0, 0, 100));
                    break;
                }
                default: // Image
                    areas.Add(new LedSendArea(r.OriginX, r.OriginY, r.Width, r.Height, true, "", resolveImage(r.Ipath),
                        255, 0, 0, "", 0, r.Ieffects, r.Ispeed, r.Istopover));
                    break;
            }
        }
        return new LedSendFrame(card.CardNo, card.Para_IPAddress, card.Para_Width, card.Para_Height,
            card.Para_Mode, card.Para_Color, card.Para_Gray, areas);
    }
}
