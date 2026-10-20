using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ParkingGuidance.Web.Models;

namespace ParkingGuidance.Web.Services.Sensor;

/// <summary>
/// Tiến trình nền nói chuyện với cảm biến thật, theo loại đang chọn ở trang "Cảm biến":
///  • MGT200: kết nối tới converter, lần lượt hỏi từng ZCU được kích hoạt (như TCPIP.Myloop của PGS):
///    mất kết nối converter 10 lần liên tiếp thì coi converter hỏng; 1 ZCU không trả lời 3 lần liên tiếp thì coi ZCU đó mất kết nối.
///  • Keytop: mở server TCP lắng nghe, các Keytop tự kết nối tới và gửi trạng thái; quá thời gian chờ thì coi mất kết nối.
/// Đổi cài đặt thì vòng chạy hiện tại bị huỷ và chạy lại ngay theo cài đặt mới (không cần khởi động lại chương trình).
/// </summary>
public sealed class SensorDriverService : BackgroundService
{
    private enum PollResult { ConnectFailed, NoReply, Invalid, Ok }

    private readonly ISensorService _sensors;
    private readonly ILogger<SensorDriverService> _log;
    private readonly object _ctsLock = new();
    private CancellationTokenSource? _runCts;

    public SensorDriverService(ISensorService sensors, ILogger<SensorDriverService> log)
    {
        _sensors = sensors;
        _log = log;
        _sensors.SettingsChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged()
    {
        lock (_ctsLock)
        {
            try { _runCts?.Cancel(); } catch (ObjectDisposedException) { /* vòng chạy vừa kết thúc */ }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = _sensors.Settings;
            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (_ctsLock) { _runCts = runCts; }
            try
            {
                // cài đặt vừa đổi đúng lúc đang khởi động vòng chạy: chạy lại với cài đặt mới
                if (!ReferenceEquals(settings, _sensors.Settings)) continue;

                if (settings.Type == SensorType.Keytop) await RunKeytopAsync(settings, runCts.Token);
                else await RunPm200Async(settings, runCts.Token);
            }
            catch (OperationCanceledException)
            {
                // đổi cài đặt hoặc tắt chương trình
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Tien trinh doc cam bien gap loi, thu lai sau 3 giay");
                try { await Task.Delay(3000, stoppingToken); } catch (OperationCanceledException) { }
            }
            finally
            {
                lock (_ctsLock) { _runCts = null; }
            }
        }
    }

    // =================================================================== MGT200
    private async Task RunPm200Async(SensorSettings s, CancellationToken ct)
    {
        var zcuFails = new int[SensorSettings.Pm200MaxZcu];
        var zcuNextTryUtc = new DateTime[SensorSettings.Pm200MaxZcu]; // ZCU đã mất kết nối: chỉ hỏi lại theo chu kỳ dài
        int converterFails = 0;
        _sensors.SetListening(false, null);

        while (!ct.IsCancellationRequested)
        {
            bool any = false;
            bool polled = false;
            for (int zcu = 0; zcu < SensorSettings.Pm200MaxZcu && !ct.IsCancellationRequested; zcu++)
            {
                await ServeCommandsAsync(s, ct);   // lệnh cài đặt/đọc khoảng cách do người dùng yêu cầu: gửi trước khi hỏi ZCU tiếp theo

                if (!s.Pm200Enabled[zcu])
                {
                    _sensors.SetDeviceOnline(zcu, false);
                    continue;
                }
                any = true;

                // ZCU đã mất kết nối (không trả lời ≥ 3 lần): chỉ hỏi lại mỗi vài giây. Mỗi lần hỏi 1 ZCU không trả lời tốn hết thời gian chờ
                // phản hồi — nếu cứ hỏi liên tục thì các ZCU còn tốt bị đọc chậm theo.
                if (zcuFails[zcu] >= 3 && DateTime.UtcNow < zcuNextTryUtc[zcu]) continue;

                polled = true;
                var r = await PollPm200Async(s, zcu, ct);
                if (r == PollResult.ConnectFailed)
                {
                    if (converterFails < 10) converterFails++;
                    if (converterFails >= 10)
                    {
                        _sensors.SetConverterOk(false, $"Không kết nối được converter MGT200 {s.Pm200Ip}:{s.Pm200Port}.");
                        for (int i = 0; i < SensorSettings.Pm200MaxZcu; i++) _sensors.SetDeviceOnline(i, false);
                    }
                    await Task.Delay(200, ct);
                    break; // converter không kết nối được: không cần thử tiếp từng ZCU còn lại trong vòng này (mỗi lần thử tốn 1 giây chờ kết nối)
                }

                converterFails = 0;
                _sensors.SetConverterOk(true, null);

                if (r == PollResult.NoReply)
                {
                    if (zcuFails[zcu] < 3) zcuFails[zcu]++;
                    if (zcuFails[zcu] >= 3)
                    {
                        _sensors.SetDeviceOnline(zcu, false);
                        zcuNextTryUtc[zcu] = DateTime.UtcNow.AddSeconds(5);
                    }
                }
                else
                {
                    zcuFails[zcu] = 0; // đã đánh dấu online trong ApplyPm200 / NoteInvalidFrame
                }

                await Task.Delay(100, ct);
            }

            if (any && !polled) await Task.Delay(200, ct); // mọi ZCU đang nghỉ chờ hỏi lại: tránh vòng lặp quay trống

            if (!any)
            {
                _sensors.SetConverterOk(false, "Chưa kích hoạt ZCU nào để quét.");
                await ServeCommandsAsync(s, ct);
                await Task.Delay(300, ct);
            }
        }
    }

    /// <summary>Gửi các lệnh đang chờ (nếu có) tới converter và trả kết quả cho người gọi.</summary>
    private async Task ServeCommandsAsync(SensorSettings s, CancellationToken ct)
    {
        SensorPendingCommand? cmd;
        while ((cmd = _sensors.DequeueCommand()) is not null)
        {
            if (cmd.Abandoned) continue;
            SensorCommandResult result;
            try
            {
                result = await ExecutePm200CommandAsync(s, cmd.Frame, ct);
            }
            catch (OperationCanceledException)
            {
                cmd.Completion.TrySetResult(new SensorCommandResult { Error = "Đã huỷ do cài đặt thay đổi hoặc chương trình đang tắt." });
                throw;
            }
            cmd.Completion.TrySetResult(result);
        }
    }

    /// <summary>Kết nối converter, gửi 1 lệnh và gom phản hồi: chờ tối đa 1,5 giây cho dữ liệu đầu tiên, rồi dừng khi im lặng 250 ms.</summary>
    private async Task<SensorCommandResult> ExecutePm200CommandAsync(SensorSettings s, string frame, CancellationToken ct)
    {
        using var client = new TcpClient { NoDelay = true };
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(1000);
            await client.ConnectAsync(s.Pm200Ip, s.Pm200Port, connectCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return new SensorCommandResult { Error = $"Không kết nối được converter MGT200 {s.Pm200Ip}:{s.Pm200Port}." };
        }

        try
        {
            var stream = client.GetStream();
            var request = Encoding.ASCII.GetBytes(frame);
            await stream.WriteAsync(request, 0, request.Length, ct);

            var text = new StringBuilder();
            var buf = new byte[512];
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3000)
            {
                int wait = text.Length == 0 ? 1500 : 250;
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(wait);

                int n;
                try { n = await stream.ReadAsync(buf, 0, buf.Length, readCts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; } // hết thời gian chờ

                if (n <= 0) break;
                text.Append(Encoding.Latin1.GetString(buf, 0, n));
            }
            return new SensorCommandResult { Ok = true, Reply = text.ToString() };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new SensorCommandResult { Error = "Lỗi khi gửi lệnh: " + ex.Message };
        }
    }

    private async Task<PollResult> PollPm200Async(SensorSettings s, int zcu, CancellationToken ct)
    {
        using var client = new TcpClient { NoDelay = true };
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(1000);
            await client.ConnectAsync(s.Pm200Ip, s.Pm200Port, connectCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return PollResult.ConnectFailed; }

        try
        {
            var stream = client.GetStream();
            var request = Encoding.ASCII.GetBytes(SensorProtocol.Pm200.BuildRequest(zcu));
            await stream.WriteAsync(request, 0, request.Length, ct);

            var text = new StringBuilder();
            var buf = new byte[512];
            var sw = Stopwatch.StartNew();
            int wait = s.Pm200ReplyWaitMs;

            while (sw.ElapsedMilliseconds < wait)
            {
                int remaining = wait - (int)sw.ElapsedMilliseconds;
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(Math.Max(remaining, 1));

                int n;
                try { n = await stream.ReadAsync(buf, 0, buf.Length, readCts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; } // hết thời gian chờ

                if (n <= 0) break; // converter đóng kết nối
                text.Append(Encoding.Latin1.GetString(buf, 0, n));

                var so_far = text.ToString();
                if (SensorProtocol.Pm200.TryParse(so_far, out var states))
                {
                    _sensors.ApplyPm200(zcu, states, so_far);
                    return PollResult.Ok;
                }
            }

            if (text.Length == 0) return PollResult.NoReply;
            _sensors.NoteInvalidFrame(zcu, text.ToString());
            return PollResult.Invalid;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return PollResult.NoReply; }
    }

    // =================================================================== Keytop
    private async Task RunKeytopAsync(SensorSettings s, CancellationToken ct)
    {
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Any, s.KeytopPort);
            listener.Start();
        }
        catch (Exception ex)
        {
            _sensors.SetListening(false, $"Không mở được cổng {s.KeytopPort} (đang bị chương trình khác dùng?): {ex.Message}");
            await Task.Delay(3000, ct); // huỷ khi đổi cài đặt; hết 3 giây thì vòng ngoài thử lại
            return;
        }

        _sensors.SetListening(true, $"Đang lắng nghe cổng {s.KeytopPort}.");
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                _sensors.ExpireKeytop(DateTime.UtcNow);
                await Task.Delay(1000, ct);
            }
        }, ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = HandleKeytopClientAsync(client, ct);
            }
        }
        finally
        {
            listener.Stop();
            _sensors.SetListening(false, null);
        }
    }

    private async Task HandleKeytopClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            string ip = "?";
            try
            {
                if (client.Client.RemoteEndPoint is IPEndPoint ep)
                {
                    var addr = ep.Address;
                    if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
                    ip = addr.ToString();
                }
                client.NoDelay = true;

                var stream = client.GetStream();
                var buf = new byte[1024];
                while (!ct.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(buf, 0, buf.Length, ct);
                    if (n <= 0) break;
                    _sensors.ApplyKeytopFrame(ip, Encoding.ASCII.GetString(buf, 0, n), out _);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Keytop {Ip} ngat ket noi", ip);
            }
        }
    }
}
