namespace ParkingGuidance.Web.Services.Anpr;

/// <summary>
/// Bước CHUẨN HOÁ CUỐI CÙNG cho chuỗi biển số, chạy sau khi hệ thống đã đọc + bỏ phiếu xong và ngay trước khi trả kết quả ra ngoài
/// (xem <c>TesseractPlateRecognitionService</c>: hai chỗ <c>return (PlateNormalizer.Apply(best.Pretty), ...)</c>).
///
/// Bạn chỉ cần viết nội dung hàm <see cref="Normalize"/>. Hàm <see cref="Apply"/> bọc bên ngoài để nếu <see cref="Normalize"/> lỡ
/// ném lỗi hoặc trả về chuỗi rỗng thì hệ thống tự dùng lại chuỗi gốc, không làm hỏng việc nhận diện.
/// </summary>
public static class PlateNormalizer
{
    /// <summary>
    /// ========================  VIẾT CODE CỦA BẠN Ở ĐÂY  ========================
    ///
    /// Đầu vào  <paramref name="plate"/>: biển số đã được định dạng bởi <see cref="VietnamPlateFormatter"/>, ví dụ:
    ///     "51L-152.17"   (seri 1 chữ + 5 số, có dấu chấm)
    ///     "29NN-691.33"  (seri 2 ký tự + 5 số)
    ///     "30Y9-6850"    (seri chữ+số + 4 số, không có dấu chấm)
    /// Đầu ra: biển số sau khi chuẩn hoá, ví dụ "308-234.56" → "30B-234.56".
    ///
    /// Lưu ý: giữ nguyên định dạng (dấu "-" và ".") nếu phần còn lại của ứng dụng đang dựa vào nó.
    /// Hàm này được gọi liên tục khi nhận diện nên giữ nhẹ, không truy cập mạng/ổ đĩa.
    /// </summary>
    /// 

    // Chương trình sửa lại biển số ở đây-----------------------------------
    // Chương trình sửa lại biển số ở đây-----------------------------------
    // Chương trình sửa lại biển số ở đây-----------------------------------
    // Chương trình sửa lại biển số ở đây-----------------------------------
    // Chương trình sửa lại biển số ở đây-----------------------------------
    // Chương trình sửa lại biển số ở đây-----------------------------------
    // Chương trình sửa lại biển số ở đây-----------------------------------
    public static string Normalize(string plate)
    {
        // TODO: code chuẩn hoá của bạn.
        // Hiển thị hộp thoại alert đơn giản

        return plate;
    }






    /// <summary>Gọi <see cref="Normalize"/> an toàn. Không cần sửa hàm này.</summary>
    public static string Apply(string plate)
    {
        if (string.IsNullOrWhiteSpace(plate)) return plate;
        try
        {
            var result = Normalize(plate);
            return string.IsNullOrWhiteSpace(result) ? plate : result;
        }
        catch
        {
            return plate;
        }
    }
}
