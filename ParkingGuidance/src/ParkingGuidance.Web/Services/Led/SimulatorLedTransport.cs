namespace ParkingGuidance.Web.Services.Led;

/// <summary>Giả lập card LED (không gửi gì ra mạng) — chạy thử giao diện/luồng cập nhật khi chưa có phần cứng hoặc bridge.</summary>
public sealed class SimulatorLedTransport : ILedTransport
{
    private readonly ILogger<SimulatorLedTransport> _logger;

    public SimulatorLedTransport(ILogger<SimulatorLedTransport> logger, string? warning = null)
    {
        _logger = logger;
        Warning = warning;
    }

    public string Name => "Mô phỏng";
    public string? Warning { get; }

    public Task<LedResult> SendAsync(LedSendFrame f, CancellationToken ct)
    {
        _logger.LogInformation("[LED mo phong] card {No} ({Ip}) <- {Text}", f.CardNo, f.Ip,
            string.Join(" | ", f.Areas.Select(a => a.IsImage ? "[anh]" : a.Text)));
        return Task.FromResult(LedResult.Success());
    }

    public Task<LedResult> IsOnlineAsync(string ip, CancellationToken ct) => Task.FromResult(LedResult.Success());
}
