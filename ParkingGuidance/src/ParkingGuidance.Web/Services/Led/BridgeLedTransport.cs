using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParkingGuidance.Web.Services.Led;

/// <summary>
/// Gọi HDSDK.dll qua tiến trình phụ <c>ParkingGuidance.LedBridge.exe</c> (x86): HDSDK.dll là DLL 32-bit nên web app 64-bit không nạp trực tiếp được;
/// SDK treo/crash chỉ làm bridge bị kill và tự khởi động lại. Giao thức: mỗi dòng 1 JSON (UTF-8) qua stdin/stdout, lần lượt từng yêu cầu.
/// </summary>
public sealed class BridgeLedTransport : ILedTransport, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _exePath;
    private readonly Func<TimeSpan> _timeoutProvider;
    private readonly ILogger<BridgeLedTransport> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _proc;
    private long _nextId;

    /// <param name="timeoutProvider">Trả về thời gian chờ phản hồi tối đa; được đọc lại ở MỖI yêu cầu nên đổi cài đặt có hiệu lực ngay.</param>
    public BridgeLedTransport(string exePath, Func<TimeSpan> timeoutProvider, ILogger<BridgeLedTransport> logger)
    {
        _exePath = exePath;
        _timeoutProvider = timeoutProvider;
        _logger = logger;
    }

    public string Name => "HDSDK (bridge x86)";
    public string? Warning => null;

    public Task<LedResult> SendAsync(LedSendFrame f, CancellationToken ct) =>
        CallAsync(new BridgeRequest
        {
            Cmd = "send", Ip = f.Ip,
            Screen = new BridgeScreen { Width = f.Width, Height = f.Height, Color = f.Color, Gray = f.Gray, CardType = f.Mode },
            Areas = f.Areas.Select(a => new BridgeArea
            {
                X = a.X, Y = a.Y, W = a.W, H = a.H, Image = a.IsImage, Text = a.Text, Path = a.ImagePath,
                R = a.R, G = a.G, B = a.B, FontName = a.FontName, FontSize = a.FontSize,
                Effect = a.Effect, Speed = a.Speed, Stay = a.Stay
            }).ToList()
        }, ct);

    public Task<LedResult> IsOnlineAsync(string ip, CancellationToken ct) =>
        CallAsync(new BridgeRequest { Cmd = "online", Ip = ip }, ct);

    private async Task<LedResult> CallAsync(BridgeRequest req, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var sw = Stopwatch.StartNew();
        var timeout = _timeoutProvider();
        try
        {
            // Tối đa 2 lần: lần 1 có thể rơi vào tiến trình bridge đã chết giữa chừng
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var proc = EnsureStarted();
                    req.Id = Interlocked.Increment(ref _nextId);
                    await proc.StandardInput.WriteLineAsync(JsonSerializer.Serialize(req, Json).AsMemory(), ct);
                    await proc.StandardInput.FlushAsync(ct);

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(timeout);
                    while (true)
                    {
                        var line = await proc.StandardOutput.ReadLineAsync(cts.Token);
                        if (line is null) throw new IOException("Bridge da thoat.");
                        var resp = JsonSerializer.Deserialize<BridgeResponse>(line, Json);
                        if (resp is null || resp.Id != req.Id) continue;
                        return new LedResult(resp.Ok, resp.Code, resp.Error, (int)sw.ElapsedMilliseconds);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Kill(); // SDK treo quá hạn -> kill, lần sau tự khởi động lại
                    return LedResult.Fail($"Quá {timeout.TotalSeconds:0}s không có phản hồi từ card/SDK (chỉnh ở ô 'Timeout gửi LED').", -2, (int)sw.ElapsedMilliseconds);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException && attempt == 0)
                {
                    _logger.LogWarning(ex, "Bridge LED loi, khoi dong lai va thu lan nua.");
                    Kill();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Kill();
                    return LedResult.Fail("Không chạy được led-bridge: " + ex.Message, -3, (int)sw.ElapsedMilliseconds);
                }
            }
        }
        finally { _gate.Release(); }
    }

    private Process EnsureStarted()
    {
        if (_proc is { HasExited: false }) return _proc;
        Kill();

        var psi = new ProcessStartInfo(_exePath)
        {
            WorkingDirectory = Path.GetDirectoryName(_exePath)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false)
        };
        var p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start tra ve null.");
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _logger.LogWarning("[led-bridge] {Line}", e.Data); };
        p.BeginErrorReadLine();
        _proc = p;
        _logger.LogInformation("Da khoi dong led-bridge (PID {Pid}).", p.Id);
        return p;
    }

    private void Kill()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
    }

    public void Dispose() => Kill();

    // ---- Giao thức (khớp ParkingGuidance.LedBridge) ----
    private sealed class BridgeRequest
    {
        public long Id { get; set; }
        public string Cmd { get; set; } = "";
        public string Ip { get; set; } = "";
        public BridgeScreen? Screen { get; set; }
        public List<BridgeArea>? Areas { get; set; }
    }
    private sealed class BridgeScreen { public int Width { get; set; } public int Height { get; set; } public int Color { get; set; } public int Gray { get; set; } public int CardType { get; set; } }
    private sealed class BridgeArea
    {
        public int X { get; set; } public int Y { get; set; } public int W { get; set; } public int H { get; set; }
        public bool Image { get; set; } public string Text { get; set; } = ""; public string Path { get; set; } = "";
        public int R { get; set; } public int G { get; set; } public int B { get; set; }
        public string FontName { get; set; } = ""; public int FontSize { get; set; }
        public int Effect { get; set; } public int Speed { get; set; } public int Stay { get; set; }
    }
    private sealed class BridgeResponse { public long Id { get; set; } public bool Ok { get; set; } public int Code { get; set; } public string? Error { get; set; } }
}
