namespace ParkingGuidance.Web.Services.Licensing;

/// <summary>
/// Bảng ký tự cố định của cơ chế giấy phép.
///
/// Bảng chữ cái: các ký tự có thể xuất hiện trong số seri của card mạng (MAC), ổ cứng, main sau khi
/// chuẩn hoá (bỏ dấu cách / gạch / chấm, viết hoa) = 0-9 và A-Z, tổng 36 ký tự.
///
/// 20 bảng dưới đây là 20 hoán vị của 36 ký tự đó, được SINH NGẪU NHIÊN MỘT LẦN lúc viết code rồi cố định
/// ở đây (không sinh lại khi chạy). 10 bảng loại 1 (Type1) và 10 bảng loại 2 (Type2) → 10 x 10 = 100 cặp
/// (i, j) = 100 "quy luật mã hoá". Mỗi giấy phép / yêu cầu được mã hoá bằng 1 cặp ngẫu nhiên.
///
/// KHÔNG SỬA bất kỳ ký tự nào trong file này sau khi đã cấp giấy phép: các bảng này cũng là nguyên liệu
/// sinh khoá mã hoá, sửa là mọi giấy phép đã cấp đều không đọc được nữa.
/// </summary>
internal static class LicenseTables
{
    public const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public static readonly string[] Type1 =
    {
        "XUENI8HODKYG95RQ7CM6ZVTLJP1WABFS3024",
        "VUNZPGM8Y3BQSH7JKT0FA6W9X5D4EOCI1R2L",
        "HQP65TXOWUVNIZ8KAB0GEJ9LRDFS2M41C7Y3",
        "MSY9HO1Q0PDUNXRBIAK4EL823VJGC5ZFW76T",
        "F6CAIX321KVBNE5P9TDL4HMQOW8UZRJ0SG7Y",
        "YSD1OZ5K0XGH3B9WPN2QTRCEIA7F8LJM46UV",
        "B95T6GMJSNP4IU83KXYWACEO2QLVFR07DHZ1",
        "NE86TZQL4XFUOBDMJKVP2RSG3HYW970AC1I5",
        "BCUZH93P7G4OMK5NVA2DF16TXQREJLI8SY0W",
        "U6EBJ9SPK3XQOHLYGDRMVAZ7N14TF085WIC2",
    };

    public static readonly string[] Type2 =
    {
        "VCOEFGR3NM5BWYSJ84K621DHAT09LI7UZXQP",
        "HEZ89BRXMVCU62IFJ34SOQP1K70NLYTGAD5W",
        "O9C5NIZSMQ64A0EG2VBLYRTK3W7D81JFHUXP",
        "Y4LMAE6JK91250XU7IW3HFDBZGVPON8QCSTR",
        "WFJH7YA4U2ICRXZ3KTNDVGSO1MEPL5Q9B860",
        "CX5QBZHSTEPVMFK12U9W6O0Y3I8D4RGNAJ7L",
        "AUHGJ08F4IPWCETD9RXBNV36LK7M25S1OZQY",
        "Z2F58RNASOIX43V0WHGPMB79QLUDTEJ6KY1C",
        "JX94LD8VZTCWUY6K1S7BAHNGIRFO0E23PM5Q",
        "1BQO0CUJ83LATVN6D7MWXYI4E9KZS5R2PFGH",
    };
}
