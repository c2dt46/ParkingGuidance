namespace ParkingGuidance.Web.Services.Parking;

/// <summary>Đối chiếu biển số đọc được với biển số được chỉ định cho 1 ô đỗ (dùng khi chỉ có 1 lần đọc, vd ảnh thử nhận diện).</summary>
public static class PlateMatch
{
    /// <summary>Chỉ giữ chữ + số, viết hoa: "51K-163.27" và "51k16327" là cùng 1 biển.</summary>
    public static string Normalize(string? plate) =>
        new string((plate ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    /// <summary>null = ô không có biển chỉ định (xe nào cũng được); "Valid" = biển trùng; "Invalid" = biển khác;
    /// "Pending" = chưa đọc được biển nên chưa kết luận.</summary>
    public static string? Check(string? assigned, string? detected)
    {
        var a = Normalize(assigned);
        if (a.Length == 0) return null;
        var d = Normalize(detected);
        if (d.Length == 0 || d == "UNKNOWN") return "Pending";
        return d == a ? "Valid" : "Invalid";
    }
}
