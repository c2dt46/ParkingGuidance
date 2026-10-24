namespace ParkingGuidance.Web.Services.Anpr;

/// <summary>
/// NƠI DUY NHẤT quyết định một chuỗi biển số đọc được có HỢP LỆ hay không và sửa các nhầm lẫn (vd "308-10370" → "30B-10370").
///
/// Mọi bộ đọc (model OCR, bộ phân loại ký tự, YOLO ký tự, Tesseract) đều đưa từng kết quả đọc được qua <see cref="Apply"/> TRƯỚC khi bỏ phiếu;
/// kết quả gộp từ nhiều lần đọc cũng được đưa qua lần nữa. Quy ước:
///   • <see cref="Normalize"/> trả về chuỗi biển (đã sửa / định dạng) → kết quả đọc đó được tính phiếu.
///   • <see cref="Normalize"/> trả về <c>null</c> hoặc <c>""</c> → kết quả đọc đó bị coi là KHÔNG nhận diện được, bị loại.
///     Nếu mọi kết quả đọc đều bị loại thì xe không có biển số (không nhận diện được ký tự).
/// </summary>
public static class PlateNormalizer
{
    /// <summary>
    /// ========================  VIẾT CODE CỦA BẠN Ở ĐÂY  ========================
    ///
    /// <paramref name="raw"/>: chuỗi ký tự đọc được, CHƯA kiểm tra và CHƯA sửa gì, ví dụ "30810370", "51L15217", "30B-10370", "AA1234"
    /// (có thể lẫn khoảng trắng, dấu; bộ đọc ký tự thì chỉ gồm chữ/số). Mọi loại biển (ô tô, xe máy, quân đội, công an, ngoại giao...) đều đi vào đây.
    /// <paramref name="lineLengths"/>: số ký tự mỗi dòng nếu biết chắc đây là biển 2 dòng (vd {3, 5}); <c>null</c> nếu biển 1 dòng hoặc không biết.
    ///
    /// Trả về: biển đã chuẩn hoá (dạng bạn muốn hiển thị, vd "30B-103.70"), hoặc <c>null</c> / <c>""</c> nếu coi là không nhận diện được.
    /// Chuỗi trả về sẽ được dùng NGUYÊN VĂN làm kết quả; bộ gộp kết quả so khớp các lần đọc theo phần chữ + số (A–Z, 0–9) của nó, không phân biệt hoa/thường.
    ///
    /// MẶC ĐỊNH (tạm thời, chưa kiểm tra cú pháp): chỉ giữ chữ và số (viết hoa, Đ → D); CHỈ CẦN TỪ 3 KÝ TỰ TRỞ LÊN là hợp lệ, dưới 3 ký tự thì trả null.
    /// Riêng biển có dạng ô tô / xe máy quen thuộc (vd "30810370", "51L15217") vẫn được sửa theo vị trí (8→B ở chữ cái seri...) và định dạng đẹp
    /// ("30B-103.70") bằng <see cref="VietnamPlateFormatter"/>; các biển khác (quân đội, công an, ngoại giao, biển đặc biệt...) được giữ nguyên chuỗi chữ/số đọc được.
    /// Khi có quy tắc chính xác cho từng loại biển, hãy thay thân hàm này.
    /// </summary>
    public static string? Normalize(string raw, IReadOnlyList<int>? lineLengths)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw.ToUpperInvariant())
        {
            var c = ch == 'Đ' ? 'D' : ch;
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') sb.Append(c);
        }
        var cleaned = sb.ToString();
        if (cleaned.Length < 3) return null;                       // hợp lệ chỉ cần tìm được từ 3 ký tự

        var (text, valid) = VietnamPlateFormatter.Normalize(cleaned, lineLengths);
        return valid ? text : cleaned;
    }

    /// <summary>
    /// Trọng số khi bỏ phiếu cho một kết quả đã chuẩn hoá (nhân với độ tin cậy của lần đọc): nhỏ hơn 1 = ít tin hơn.
    /// MẶC ĐỊNH: chỉ giảm (0.4) những chuỗi BẮT ĐẦU BẰNG 2 SỐ mà 2 số đó không phải mã tỉnh thường gặp — thường là đọc sai 1–2 ký tự đầu của biển thường.
    /// Biển không bắt đầu bằng 2 số (quân đội, ngoại giao, đặc biệt...) luôn có trọng số 1.0.
    /// </summary>
    public static double Weight(string plate)
    {
        var p = plate.Trim();
        bool startsWithTwoDigits = p.Length >= 2 && char.IsAsciiDigit(p[0]) && char.IsAsciiDigit(p[1]);
        return !startsWithTwoDigits || VietnamPlateFormatter.IsKnownProvinceCode(p) ? 1.0 : 0.4;
    }

    /// <summary>
    /// Gọi <see cref="Normalize"/> một cách an toàn. KHÔNG cần sửa hàm này.
    /// Trả về <c>null</c> khi chuỗi rỗng hoặc <see cref="Normalize"/> trả về null / "" (= không nhận diện được).
    /// Nếu <see cref="Normalize"/> ném lỗi (lỗi trong code của bạn) thì dùng tạm quy tắc mặc định cũ để hệ thống không ngừng nhận diện.
    /// </summary>
    public static string? Apply(string? raw, IReadOnlyList<int>? lineLengths = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var result = Normalize(raw, lineLengths);
            return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("PlateNormalizer.Normalize loi: " + ex.Message);
            var (text, valid) = VietnamPlateFormatter.Normalize(raw, lineLengths);
            return valid ? text : null;
        }
    }
}
