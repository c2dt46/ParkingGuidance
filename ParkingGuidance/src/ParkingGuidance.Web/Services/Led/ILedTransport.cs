namespace ParkingGuidance.Web.Services.Led;

/// <summary>Kết quả 1 thao tác với card LED.</summary>
public readonly record struct LedResult(bool Ok, int Code, string? Error, int ElapsedMs)
{
    public static LedResult Success(int ms = 0) => new(true, 0, null, ms);
    public static LedResult Fail(string error, int code = -1, int ms = 0) => new(false, code, error, ms);
}

/// <summary>1 vùng hiển thị đã tính xong, sẵn sàng đưa vào SDK (Hd_AddArea + Hd_AddSimpleTextAreaItem / Hd_AddImageAreaItem).</summary>
public sealed record LedSendArea(
    int X, int Y, int W, int H, bool IsImage, string Text, string ImagePath,
    int R, int G, int B, string FontName, int FontSize, int Effect, int Speed, int Stay);

/// <summary>Toàn bộ nội dung gửi tới 1 card (tương ứng 1 lần chạy SendtoDHCard.run của PGS).</summary>
public sealed record LedSendFrame(
    int CardNo, string Ip, int Width, int Height, int Mode, int Color, int Gray, IReadOnlyList<LedSendArea> Areas);

/// <summary>
/// Lớp giao tiếp thật với card LED. Tách khỏi logic để HDSDK.dll (32-bit) chạy ở tiến trình riêng (<see cref="BridgeLedTransport"/>)
/// và để chạy giả lập khi chưa có phần cứng (<see cref="SimulatorLedTransport"/>). Dispatcher đảm bảo mỗi lúc chỉ 1 thao tác (SDK có 1 màn hình toàn cục).
/// </summary>
public interface ILedTransport
{
    string Name { get; }
    /// <summary>Cảnh báo tình trạng (vd "không tìm thấy led-bridge") hoặc null.</summary>
    string? Warning { get; }
    Task<LedResult> SendAsync(LedSendFrame frame, CancellationToken ct);
    Task<LedResult> IsOnlineAsync(string ip, CancellationToken ct);
}
