using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using OpenCvSharp;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Anpr;
using ParkingGuidance.Web.Services.Camera;
using ParkingGuidance.Web.Services.Detection;
using ParkingGuidance.Web.Services.Settings;

namespace ParkingGuidance.Web.Controllers;

/// <summary>
/// Tab "Mô phỏng" (Thiết bị → Camera): nhận diện thử ảnh lấy từ thư mục trên máy tính.
/// KHÁC với <c>test-detect</c> của từng camera: KHÔNG áp dụng vùng nhận diện (mọi xe trong ảnh đều được nhận diện, đọc biển số),
/// và KHÔNG ghi gì vào bãi đỗ / bản đồ / lịch sử / SignalR — chỉ trả kết quả để xem.
/// </summary>
[ApiController]
[Route("api/system/simulation")]
public class SimulationApiController : ControllerBase
{
    private readonly IVehicleDetectionService _detector;
    private readonly IPlateRecognitionService _anpr;
    private readonly AiJobScheduler _scheduler;
    private readonly ISettingsService _settings;

    /// <summary>Chờ tối đa bao lâu để tới lượt (camera thật luôn được ưu tiên) trước khi báo bận.</summary>
    private static readonly TimeSpan SimulationMaxWait = TimeSpan.FromSeconds(120);

    public SimulationApiController(
        IVehicleDetectionService detector, IPlateRecognitionService anpr, AiJobScheduler scheduler, ISettingsService settings)
    {
        _settings = settings;
        _detector = detector;
        _anpr = anpr;
        _scheduler = scheduler;
    }

    /// <summary>Trạng thái model AI — để tab Mô phỏng cảnh báo sớm khi model chưa nạp được.</summary>
    [HttpGet("status")]
    public IActionResult Status() => Ok(new
    {
        detectorReady = _detector.IsReady,
        anprReady = _anpr.IsReady,
        modelPath = _detector.ResolvedModelPath ?? _detector.ModelPath,
        modelInfo = _detector.ModelInfo,
        loadError = _detector.LoadError
    });

    /// <summary>
    /// Nhận diện TẤT CẢ xe trong 1 ảnh (không giới hạn vùng) và đọc biển số từng xe.
    /// Kết quả sắp xếp từ trái sang phải, đánh số từ 1.
    /// </summary>
    /// <param name="conf">Ngưỡng tin cậy nhận diện xe (0.05–0.95); bỏ trống = dùng cài đặt chung.</param>
    /// <param name="mode">
    /// Bỏ trống: nhận diện mọi xe trong ảnh. <c>region</c>: ảnh là VÙNG ĐÃ CẮT (zoom) — nhận diện xe như thường, nếu không thấy xe nào thì
    /// coi cả vùng là xe/biển và đọc biển trực tiếp. <c>plate</c>: coi cả vùng là biển số (bỏ bước dò xe).
    /// </param>
    [HttpPost("analyze")]
    [RequestSizeLimit(40_000_000)]
    public async Task<IActionResult> Analyze(IFormFile image, [FromQuery] float? conf, [FromQuery] string? mode, CancellationToken ct)
    {
        mode = (mode ?? "").Trim().ToLowerInvariant();
        bool plateOnly = mode == "plate";

        if (plateOnly && !_anpr.IsReady)
            return StatusCode(503, new { message = "Bộ đọc biển số chưa sẵn sàng." });

        if (!plateOnly && !_detector.IsReady)
            return StatusCode(503, new
            {
                message = "Model AI nhận diện xe chưa được nạp" +
                          (string.IsNullOrWhiteSpace(_detector.LoadError) ? "." : ": " + _detector.LoadError)
            });

        if (image is null || image.Length == 0)
            return BadRequest(new { message = "Thiếu ảnh đầu vào." });

        using var ms = new MemoryStream();
        await image.CopyToAsync(ms, ct);

        using var mat = Mat.FromImageData(ms.ToArray(), ImreadModes.Color);
        if (mat.Empty())
            return BadRequest(new { message = "Không đọc được ảnh (định dạng không hỗ trợ hoặc file hỏng)." });

        float? threshold = conf.HasValue && conf.Value > 0 ? Math.Clamp(conf.Value, 0.05f, 0.95f) : null;

        var sw = Stopwatch.StartNew();
        List<SimulationVehicle> vehicles;
        try
        {
            // ƯU TIÊN THẤP NHẤT: chỉ chạy khi camera thật và các việc người dùng bấm tay không còn việc chờ,
            // nên mô phỏng (kể cả khi tự chuyển ảnh liên tục) không làm chậm nhận diện thật.
            vehicles = await _scheduler.RunLowPriorityAsync($"Mô phỏng: {image.FileName}", () =>
            {
                var st = _settings.Current;
                float minOverlap = st.PlateMinOverlap ?? 0.1f;
                bool fullSearch = (st.PlateFullSearch ?? 0) != 0;

                // Do xe va do khung bien SONG SONG tren ca anh; bien nam hoan toan ngoai moi xe thi khong hop le (bi bo)
                List<DetectionResult> detected;
                List<BoundingBox> plateBoxes;
                if (plateOnly)
                {
                    detected = new List<DetectionResult>();
                    plateBoxes = new List<BoundingBox>();
                }
                else
                {
                    var (veh, pls) = FrameAnalysis.DetectParallel(_detector, _anpr, mat, st, detectPlates: true, vehicleConf: threshold);
                    detected = veh.OrderBy(d => d.Box.X1).ToList();
                    plateBoxes = pls;
                }
                var plateAssign = FrameAnalysis.AssignPlates(detected.Select(d => d.Box).ToList(), plateBoxes, minOverlap);
                var rows = new List<SimulationVehicle>(detected.Count);

                for (int i = 0; i < detected.Count; i++)
                {
                    var v = detected[i];
                    string? plate = null;
                    float plateConf = 0;
                    BoundingBox? plateBox = null;

                    if (plateAssign.TryGetValue(i, out var assigned))
                    {
                        // Co khung bien nam trong khung xe: doc ky tu tai khung do (toa do da la cua anh goc)
                        var (p, c, pb) = _anpr.ReadPlateAt(mat, assigned);
                        plate = p;
                        plateConf = c;
                        plateBox = pb ?? assigned;
                    }
                    else if (fullSearch)
                    {
                        // Tuy chon (mac dinh tat): xe khong co khung bien thi van tim bien day du — cham
                        var rect = BoxUtils.ClampToImage(mat, v.Box, BoxUtils.PlateSearchExpand);
                        if (rect.Width > 0 && rect.Height > 0)
                        {
                            using var crop = new Mat(mat, rect);
                            var (p, c, pb) = _anpr.Recognize(crop);
                            plate = p;
                            plateConf = c;
                            if (pb is not null)   // khung bien tinh theo anh xe da cat → doi ve toa do anh goc
                                plateBox = new BoundingBox
                                {
                                    X1 = pb.X1 + rect.X, Y1 = pb.Y1 + rect.Y,
                                    X2 = pb.X2 + rect.X, Y2 = pb.Y2 + rect.Y
                                };
                        }
                    }

                    rows.Add(new SimulationVehicle
                    {
                        Index = i + 1,
                        Box = v.Box,
                        VehicleType = v.VehicleType.ToString(),
                        Confidence = v.Confidence,
                        Color = v.Color,
                        PlateNumber = plate,
                        PlateConfidence = plateConf,
                        PlateBox = plateBox,
                        PlateStage = plate is not null ? "read" : (plateBox is not null ? "unread" : "none")
                    });
                }

                // Model dò xe không thấy xe nào: vẫn thử tìm biển trên CẢ khung hình (ảnh chụp sát biển, xe bị cắt khuất, xe lạ…)
                // Luat "bien ngoai vung xe la khong hop le": khi muc chong lan toi thieu > 0 thi khong co xe = khong co bien hop le nen bo qua buoc nay
                if (!plateOnly && rows.Count == 0 && minOverlap <= 0f)
                {
                    // Che do NHANH: khong co bien thi ket thuc ngay, khong chay het cac buoc du phong tren ca khung hinh (rat cham)
                    var (fp, fc, fpb) = _anpr.RecognizeQuick(mat);
                    if (fpb is not null)
                        rows.Add(new SimulationVehicle
                        {
                            Index = 1,
                            Box = fpb,
                            VehicleType = "Unknown",
                            Confidence = 0f,
                            PlateNumber = fp,
                            PlateConfidence = fc,
                            PlateBox = fpb,
                            PlateStage = fp is not null ? "read" : "unread"
                        });
                }

                // Vùng đã cắt: không có xe nào (hoặc chọn "coi là biển") → đọc biển trực tiếp trên cả vùng
                if (plateOnly || (mode == "region" && rows.Count == 0))
                {
                    var (p, c, pb) = _anpr.Recognize(mat);
                    rows.Add(new SimulationVehicle
                    {
                        Index = 1,
                        Box = new BoundingBox { X1 = 0, Y1 = 0, X2 = mat.Width, Y2 = mat.Height },
                        VehicleType = "Region",
                        IsRegion = true,
                        Confidence = 1f,
                        PlateNumber = p,
                        PlateConfidence = c,
                        PlateBox = pb,
                        PlateStage = p is not null ? "read" : (pb is not null ? "unread" : "none")
                    });
                }

                return rows;
            }, SimulationMaxWait, ct);
        }
        catch (OperationCanceledException)
        {
            // Người dùng đã chuyển ảnh / rời tab: việc chưa chạy đã được gỡ khỏi hàng đợi, không tốn tài nguyên.
            return StatusCode(499, new { message = "Đã huỷ." });
        }
        catch (TimeoutException)
        {
            return StatusCode(503, new { message = "Hệ thống đang bận nhận diện camera, ảnh này chưa được tới lượt. Hãy thử lại sau (mô phỏng luôn nhường camera thật)." });
        }
        catch (AiJobScheduler.LowQueueFullException)
        {
            return StatusCode(429, new { message = "Đang có quá nhiều ảnh mô phỏng chờ xử lý, hãy chờ giây lát rồi thử lại." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Lỗi khi nhận diện ảnh: " + ex.Message });
        }
        sw.Stop();

        return Ok(new
        {
            FrameWidth = mat.Width,
            FrameHeight = mat.Height,
            VehicleCount = vehicles.Count(r => !r.IsRegion),
            PlateCount = vehicles.Count(r => !string.IsNullOrEmpty(r.PlateNumber)),
            ElapsedMs = sw.ElapsedMilliseconds,
            Vehicles = vehicles
        });
    }

    public class SimulationVehicle
    {
        public int Index { get; set; }
        public BoundingBox Box { get; set; } = new();
        public string VehicleType { get; set; } = "";

        /// <summary>true = hàng "cả vùng cắt" (không phải xe do AI dò): dùng khi cắt riêng biển số để zoom/nhập biển đúng.</summary>
        public bool IsRegion { get; set; }
        public float Confidence { get; set; }
        public string? Color { get; set; }
        public string? PlateNumber { get; set; }
        public float PlateConfidence { get; set; }

        /// <summary>Khung biển số trên ảnh gốc (pixel): có cả khi tìm được vùng biển nhưng CHƯA đọc được ký tự; null nếu không dò được vùng biển.</summary>
        public BoundingBox? PlateBox { get; set; }

        /// <summary>Bước nào dừng: <c>read</c> = đọc được biển; <c>unread</c> = dò được vùng biển nhưng không đọc được ký tự (lỗi ở bước đọc → Train);
        /// <c>none</c> = không dò được vùng biển (lỗi ở bước dò biển).</summary>
        public string PlateStage { get; set; } = "none";
    }
}
