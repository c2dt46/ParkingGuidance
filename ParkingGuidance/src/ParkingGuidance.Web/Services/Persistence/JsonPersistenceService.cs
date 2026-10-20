using System.Text.Json;

namespace ParkingGuidance.Web.Services.Persistence;

/// <summary>
/// Lưu dữ liệu dạng file JSON trong thư mục cấu hình ở "Persistence:DataPath" (mặc định "data/").
/// Mỗi "key" tương ứng 1 file (vd "parking-state.json", "layout-elements.json", "floors.json").
///
/// Ghi file theo kiểu "ghi ra file tạm rồi đổi tên" (write-then-rename) để tránh hỏng file nếu
/// server bị tắt đột ngột đúng lúc đang ghi.
///
/// Toàn bộ trạng thái chương trình = nội dung thư mục "data/" + "wwwroot/uploads/floors/" (ảnh
/// nền mặt bằng) + "wwwroot/uploads/layout/" (ảnh đèn/công tắc bit). Muốn sao lưu/chuyển sang máy khác, chỉ cần copy các thư mục này.
/// </summary>
public class JsonPersistenceService : IPersistenceService
{
    private readonly string _dataDir;
    public string DataDirectory => _dataDir;
    private readonly object _lock = new();
    private readonly ILogger<JsonPersistenceService> _logger;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public JsonPersistenceService(IConfiguration config, ILogger<JsonPersistenceService> logger)
    {
        _logger = logger;
        var configuredPath = config["Persistence:DataPath"];

        // Thu muc cu (truoc day): "data" canh file thuc thi (bin/Debug/net8.0/data). Thu muc nay bi MAT khi
        // giai nen ban moi sang thu muc khac, clean/rebuild hoac publish lai — day la nguyen nhan cai dat va
        // danh sach camera "khong duoc luu". Nen mac dinh nay chuyen sang thu muc du lieu CUA NGUOI DUNG
        // (Windows: %LOCALAPPDATA%\ParkingGuidance\data), khong phu thuoc noi chay chuong trinh.
        var legacyDir = Path.Combine(AppContext.BaseDirectory, "data");

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _dataDir = string.IsNullOrWhiteSpace(local)
                ? legacyDir
                : Path.Combine(local, "ParkingGuidance", "data");
        }
        else
        {
            _dataDir = Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(AppContext.BaseDirectory, configuredPath);
        }

        try
        {
            Directory.CreateDirectory(_dataDir);
            _logger.LogInformation("Du lieu chuong trinh se duoc luu tai: {Path}", _dataDir);
            MigrateLegacyData(legacyDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Khong tao duoc thu muc luu du lieu '{Path}'. Du lieu se KHONG duoc luu lai " +
                "(mat khi restart server) cho den khi khac phuc quyen ghi thu muc.", _dataDir);
        }
    }

    /// <summary>Lan dau chay voi thu muc du lieu moi: neu thu muc "data" cu (canh file thuc thi) con file thi
    /// SAO CHEP sang (chi file chua ton tai o thu muc moi, khong ghi de) de khong mat du lieu da luu.</summary>
    private void MigrateLegacyData(string legacyDir)
    {
        try
        {
            if (!Directory.Exists(legacyDir)) return;
            if (string.Equals(Path.GetFullPath(legacyDir), Path.GetFullPath(_dataDir), StringComparison.OrdinalIgnoreCase)) return;

            foreach (var file in Directory.GetFiles(legacyDir, "*.json"))
            {
                var target = Path.Combine(_dataDir, Path.GetFileName(file));
                if (File.Exists(target)) continue;
                File.Copy(file, target);
                _logger.LogInformation("Da chuyen du lieu cu '{File}' sang thu muc du lieu moi.", Path.GetFileName(file));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Khong chuyen duoc du lieu cu tu '{Dir}' (bo qua).", legacyDir);
        }
    }

    public void Save<T>(string key, T data)
    {
        lock (_lock)
        {
            try
            {
                var path = Path.Combine(_dataDir, $"{key}.json");
                var tmpPath = path + ".tmp";
                File.WriteAllText(tmpPath, JsonSerializer.Serialize(data, Options));
                File.Move(tmpPath, path, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Khong luu duoc du lieu '{Key}'. Thay doi nay co the mat khi restart.", key);
            }
        }
    }

    public T? Load<T>(string key)
    {
        lock (_lock)
        {
            var path = Path.Combine(_dataDir, $"{key}.json");
            if (!File.Exists(path)) return default;

            try
            {
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "File du lieu '{Key}' bi loi/hong, se dung du lieu mac dinh cho phan nay. " +
                    "File loi van con o '{Path}' de kiem tra thu cong neu can.", key, path);
                return default;
            }
        }
    }
}
