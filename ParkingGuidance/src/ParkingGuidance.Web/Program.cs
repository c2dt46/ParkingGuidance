using System.Text.Json.Serialization;
using ParkingGuidance.Web.Hubs;
using ParkingGuidance.Web.Services.Anpr;
using ParkingGuidance.Web.Services.Browser;
using ParkingGuidance.Web.Services.Camera;
using ParkingGuidance.Web.Services.CameraRegistry;
using ParkingGuidance.Web.Services.Detection;
using ParkingGuidance.Web.Services.Floor;
using ParkingGuidance.Web.Services.Layout;
using ParkingGuidance.Web.Services.Modbus;
using ParkingGuidance.Web.Services.Led;
using ParkingGuidance.Web.Services.Parking;
using ParkingGuidance.Web.Services.Persistence;
using ParkingGuidance.Web.Services.Sensor;
using ParkingGuidance.Web.Services.Simulation;
using ParkingGuidance.Web.Services.Tags;

var builder = WebApplication.CreateBuilder(args);

// Du phong: ThreadPool khoi dau chi co so luong luong bang so nhan CPU va chi them ~1 luong/giay khi bi nghen.
// Khi co nhieu thiet bi (camera/LED/cam bien) cung cho ket noi, neu cac luong bi chiem het thi moi tac vu/timer khac bi tre -> timeout oan.
ThreadPool.GetMinThreads(out var minWorker, out var minIo);
ThreadPool.SetMinThreads(Math.Max(minWorker, 64), Math.Max(minIo, 64));


builder.Services.AddRazorPages();

// Serialize enum (VehicleType, SlotStatus, LayoutElementType...) thanh chuoi ten
// (vi du "Occupied" thay vi 2) de JavaScript phia client de doc va so sanh hon.
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.AddSignalR();

// Luu du lieu ra file JSON (thu muc "data/") de KHONG mat khi tat/khoi dong lai server
builder.Services.AddSingleton<IPersistenceService, JsonPersistenceService>();

// Kho du lieu bai xe (in-memory + tu dong luu/khoi phuc qua IPersistenceService)
builder.Services.AddSingleton<IParkingService, ParkingService>();

// Kho du lieu layout (nut an, hinh anh, text, lo do xe dat tren ban do Giam sat)
builder.Services.AddSingleton<ILayoutService, LayoutService>();

// Kho du lieu mat bang (B1, B2...) va anh nen tuong ung
builder.Services.AddSingleton<IFloorService, FloorService>();

// Tim & mo trinh duyet duoc chon (Chrome/Edge/Firefox/Safari...) tren may chu; khong co thi dung trinh duyet mac dinh
builder.Services.AddSingleton<IBrowserLauncher, BrowserLauncher>();

// Quan ly camera vat ly: danh sach da khai bao + do tim qua ONVIF WS-Discovery
builder.Services.AddSingleton<ParkingGuidance.Web.Services.Settings.ISettingsService, ParkingGuidance.Web.Services.Settings.SettingsService>();
builder.Services.AddSingleton<ICameraRegistryService, CameraRegistryService>();
builder.Services.AddSingleton<IOnvifDiscoveryService, OnvifDiscoveryService>();

// AI: nhan dien xe (YOLOv8/ONNX) va doc bien so (Tesseract OCR)
builder.Services.AddSingleton<IVehicleDetectionService, YoloVehicleDetectionService>();
builder.Services.AddSingleton<IPlateRecognitionService, TesseractPlateRecognitionService>();
builder.Services.AddSingleton<ICharModelReloader>(sp => (ICharModelReloader)sp.GetRequiredService<IPlateRecognitionService>());
builder.Services.AddSingleton<IOcrModelReloader>(sp => (IOcrModelReloader)sp.GetRequiredService<IPlateRecognitionService>());
// Bo do vung bien so (tab Train bien so): goi y khung, do, cai va nap lai model da train them khi dang chay
builder.Services.AddSingleton<IPlateDetectorModel>(sp => (IPlateDetectorModel)sp.GetRequiredService<IPlateRecognitionService>());

// Tab Mo phong: luu cac bien nguoi dung da sua + dieu khien tien trinh train rieng (chi chay khi bam Train, xong la thoat)
builder.Services.AddSingleton<SimulationCorrectionStore>();
builder.Services.AddSingleton<TrainingInboxStore>();
builder.Services.AddSingleton<SimulationTrainer>();
builder.Services.AddSingleton<OcrTrainer>();
// Train xe: bo anh da gan khung + huan luyen them YOLO11 (tien trinh Python rieng, uu tien thap, chi chay khi bam Train)
builder.Services.AddSingleton<ParkingGuidance.Web.Services.VehicleTrain.VehicleTrainStore>();
builder.Services.AddSingleton<ParkingGuidance.Web.Services.VehicleTrain.VehicleTrainer>();
// Train bien so: bo anh da gan khung bien + huan luyen them bo do bien so YOLO11 (tien trinh Python rieng, uu tien thap, chi chay khi bam Train)
builder.Services.AddSingleton<ParkingGuidance.Web.Services.PlateTrain.PlateTrainStore>();
builder.Services.AddSingleton<ParkingGuidance.Web.Services.PlateTrain.PlateTrainer>();
// Export / Import du lieu train (ky tu · bien so · xe) ra / vao mot thu muc
builder.Services.AddSingleton<ParkingGuidance.Web.Services.TrainTransfer.TrainTransferService>();

// Toi uu tai xu ly khi co nhieu camera: chi chay AI khi anh thay doi du nguong
builder.Services.AddSingleton<IFrameChangeDetector, FrameChangeDetector>();

// Xu ly 1 khung hinh camera (nhan dien + khop vung + OCR + ghi nhan) - dung chung boi vong lap
// nen (CameraIngestionService) va nut "Nhan dien ngay" (thu cong, xem SystemCamerasApiController)
builder.Services.AddSingleton<ICameraFrameProcessor, CameraFrameProcessor>();

// Dich vu nen: doc camera dinh ky va chay pipeline nhan dien
builder.Services.AddHostedService<ParkingGuidance.Web.Services.AiWarmupService>();

// Hang doi AI dung chung (1 luong xu ly, uu tien viec nguoi dung dang cho) + giam sat cac cong nhan camera chay doc lap
builder.Services.AddSingleton<AiJobScheduler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AiJobScheduler>());
builder.Services.AddSingleton<CameraIngestionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CameraIngestionService>());

// Bang LED (card Huidu, HDSDK.dll) theo Form HDCard cua PGS: 100 card, vung Image/Text/Number, thanh ghi A/B/C.
// Dispatcher chay theo su kien va chi gui khi du lieu (khoa A/B/C) doi, giong SendtoDHCard.HDMain.
// Giao tiep that qua tien trinh phu x86 (led-bridge) vi HDSDK.dll la DLL 32-bit; khong co bridge thi tu dong dung che do mo phong.
builder.Services.AddSingleton<ILedCardService, LedCardService>();
builder.Services.AddSingleton<ILedDataService, LedDataService>();   // nguon so cho vung Number: bien MS + bien MC + project tag
builder.Services.AddSingleton<ILedTransport>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var logs = sp.GetRequiredService<ILoggerFactory>();
    var driver = cfg["Led:Driver"] ?? "Auto";

    if (driver.Equals("Simulator", StringComparison.OrdinalIgnoreCase))
        return new SimulatorLedTransport(logs.CreateLogger<SimulatorLedTransport>(), "Đang chạy chế độ mô phỏng theo cấu hình (Led:Driver = Simulator) — không gửi gì tới bảng LED thật.");

    var bridge = cfg["Led:BridgePath"] ?? "led-bridge/ParkingGuidance.LedBridge.exe";
    if (!Path.IsPathRooted(bridge)) bridge = Path.Combine(AppContext.BaseDirectory, bridge);

    if (!OperatingSystem.IsWindows())
        return new SimulatorLedTransport(logs.CreateLogger<SimulatorLedTransport>(), "HDSDK.dll chỉ chạy trên Windows — đang dùng chế độ mô phỏng.");
    if (!File.Exists(bridge))
        return new SimulatorLedTransport(logs.CreateLogger<SimulatorLedTransport>(),
            $"Không tìm thấy led-bridge ({bridge}) — đang dùng chế độ mô phỏng. Chạy tools\\build-led-bridge.cmd để tạo.");

    var settings = sp.GetRequiredService<ParkingGuidance.Web.Services.Settings.ISettingsService>();
    return new BridgeLedTransport(bridge,
        () => TimeSpan.FromSeconds(settings.Current.LedRequestTimeoutSeconds ?? 15),
        logs.CreateLogger<BridgeLedTransport>());
});
builder.Services.AddSingleton<LedDispatcher>();
builder.Services.AddSingleton<ILedDispatcher>(sp => sp.GetRequiredService<LedDispatcher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<LedDispatcher>());

// Cam bien dau vao theo PGS: MGT200 (mac dinh, PC hoi converter qua TCP) hoac Keytop (PC mo server TCP cho thiet bi gui toi).
// SensorService giu trang thai + cai dat (data/sensors.json); SensorDriverService la tien trinh nen noi chuyen voi thiet bi that.
builder.Services.AddSingleton<ISensorService, SensorService>();
// Bien MS (tab "Du lieu" cua trang Cam bien): moi bien dem 1 trang thai tren cac cam bien duoc chi dinh; tinh truc tiep tu trang thai that, khong co luong nen.
builder.Services.AddSingleton<ISensorVariableService, SensorVariableService>();
// Bien MC (tab Du lieu cua trang Camera): dem 1 trang thai tren cac vi tri do (vung nhan dien) duoc chi dinh.
builder.Services.AddSingleton<ICameraVariableService, CameraVariableService>();
builder.Services.AddHostedService<SensorDriverService>();

// Thiet bi ngoai > Modbus TCP/IP: khai bao Server (cong se mo) va Client (IP + cong thiet bi). Chi luu + kiem tra cau hinh, chua mo ket noi;
// gateway chay nen (khi co) se doc cau hinh nay va dang ky su kien Changed.
builder.Services.AddSingleton<IModbusConfigService, ModbusConfigService>();
// Modbus Client chay nen: moi thiet bi da bat la 1 tac vu async rieng, ghi gia tri bien MS vao thanh ghi theo chu ky khai bao.
builder.Services.AddSingleton<ModbusClientService>();
builder.Services.AddSingleton<IModbusClientStatusProvider>(sp => sp.GetRequiredService<ModbusClientService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ModbusClientService>());
// Modbus Server chay nen: mo cac cong da khai bao, tra loi lenh doc/ghi thanh ghi tren bang thanh ghi rieng cua tung cong (du lieu gan sau).
builder.Services.AddSingleton<ModbusServerService>();
builder.Services.AddSingleton<IModbusServerStatusProvider>(sp => sp.GetRequiredService<ModbusServerService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ModbusServerService>());

// TASKS > Tags. System tags (Hour, Minute... BlinkSlow, BlinkFast) do 1 luong nen RIENG cap nhat; khong phu thuoc camera/LED/cam bien/bai xe,
// loi trong luong nay bi chan tai cho nen khong the anh huong luong chinh. Tat han bang appsettings "Tags:SystemEnabled": false.
// Project tags la bang khai bao cua nguoi dung (data/project-tags.json).
builder.Services.AddSingleton<SystemTagService>();
builder.Services.AddSingleton<ISystemTagService>(sp => sp.GetRequiredService<SystemTagService>());
builder.Services.AddHostedService<SystemTagWorker>();
builder.Services.AddSingleton<IProjectTagService, ProjectTagService>();

var app = builder.Build();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapRazorPages();
app.MapControllers();
app.MapHub<MonitoringHub>("/hubs/monitoring");

app.Run();
