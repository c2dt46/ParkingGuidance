using System.Text.Json;

namespace ParkingGuidance.Web.Services.Persistence;

/// <summary>
/// Lưu dữ liệu dạng file JSON, TÁCH THÀNH 2 NƠI (xem IPersistenceService):
///
///  1) Dữ liệu HỆ THỐNG (loại 1): thư mục cố định "Persistence:DataPath" (mặc định %LOCALAPPDATA%\ParkingGuidance\data):
///     bản quyền (license.dat), dữ liệu train + model đã train, simulation, anpr-log, settings.json, app-state.json...
///
///  2) Dữ liệu DỰ ÁN (loại 2): thư mục dự án (mặc định Documents\ParkingGuidance\Projects\Default, hoặc dự án mở gần nhất):
///       project.pgs.json          — nhãn dự án (tên, ngày tạo, ngày lưu)
///       data\*.json               — cảm biến, camera + vùng nhận diện, LED, layout, mặt bằng, biến MS/MC, tag, Modbus, bãi xe
///       uploads\floors, layout    — ảnh nền mặt bằng, ảnh đèn/công tắc của layout
///       led-images\               — ảnh cho vùng Image của bảng LED
///     Muốn chuyển dự án sang máy khác: copy nguyên thư mục này rồi bấm "Mở dự án".
///
/// Ghi file theo kiểu "ghi ra file tạm rồi đổi tên" (write-then-rename) để không hỏng file nếu server tắt đột ngột.
/// </summary>
public class JsonPersistenceService : IPersistenceService
{
    /// <summary>Các key thuộc DỮ LIỆU DỰ ÁN (loại 2). Key khác (license, settings, app-state...) là dữ liệu hệ thống (loại 1).
    /// Thêm key mới vào đây nếu muốn nó đi theo dự án (và cho service đó cài IProjectScoped để mở/lưu dự án nạp lại được).</summary>
    public static readonly string[] ProjectKeyList =
    {
        "sensors", "sensor-variables", "camera-registry", "camera-variables", "led-cards",
        "floors", "layout-elements", "modbus-config", "project-tags", "parking-state"
    };
    private static readonly HashSet<string> ProjectKeys = new(ProjectKeyList, StringComparer.OrdinalIgnoreCase);

    /// <summary>Key dự án mà thay đổi chỉ nằm trong bộ nhớ (làm thông số chạy) cho đến khi bấm Lưu chung. Trạng thái bãi xe (xe vào/ra,
    /// phiên đỗ) là dữ liệu chạy liên tục, không phải thông số nên vẫn ghi ngay để không mất khi mất điện.</summary>
    private static readonly HashSet<string> DeferredKeys =
        new(ProjectKeyList.Where(k => !k.Equals("parking-state", StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
    private volatile bool _unsaved;
    public bool HasUnsavedProjectChanges => _unsaved;
    public void MarkProjectSaved() => _unsaved = false;

    private readonly string _dataDir;
    private volatile string _projectDir = "";
    private bool _projectWritesPaused;
    private readonly object _lock = new();
    private readonly ILogger<JsonPersistenceService> _logger;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string DataDirectory => _dataDir;
    public string ProjectDirectory => _projectDir;
    public string ProjectDataDirectory => Path.Combine(_projectDir, "data");
    public string ProjectUploadsDirectory => Path.Combine(_projectDir, "uploads");
    public string ProjectLedImagesDirectory => Path.Combine(_projectDir, "led-images");

    public bool IsProjectKey(string key) => ProjectKeys.Contains(key);

    public JsonPersistenceService(IConfiguration config, IWebHostEnvironment env, ILogger<JsonPersistenceService> logger)
    {
        _logger = logger;
        var configuredPath = config["Persistence:DataPath"];

        // Thu muc cu (truoc day): "data" canh file thuc thi (bin/Debug/net8.0/data). Thu muc nay bi MAT khi
        // giai nen ban moi sang thu muc khac, clean/rebuild hoac publish lai. Nen mac dinh la thu muc du lieu
        // CUA NGUOI DUNG (Windows: %LOCALAPPDATA%\ParkingGuidance\data), khong phu thuoc noi chay chuong trinh.
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
            _logger.LogInformation("Du lieu HE THONG (ban quyen, train...) luu tai: {Path}", _dataDir);
            MigrateLegacyData(legacyDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Khong tao duoc thu muc du lieu he thong '{Path}'. Du lieu se KHONG duoc luu lai " +
                "(mat khi restart server) cho den khi khac phuc quyen ghi thu muc.", _dataDir);
        }

        // Chon thu muc DU AN: appsettings "Persistence:ProjectPath" > du an mo gan nhat (app-state.json) > mac dinh
        string? chosen = null;
        var cfgProject = config["Persistence:ProjectPath"];
        if (!string.IsNullOrWhiteSpace(cfgProject))
        {
            chosen = Path.IsPathRooted(cfgProject) ? cfgProject : Path.Combine(AppContext.BaseDirectory, cfgProject);
        }
        else
        {
            var st = LoadCore<AppState>("app-state", _dataDir);
            if (!string.IsNullOrWhiteSpace(st?.LastProject) && Directory.Exists(st!.LastProject))
                chosen = st.LastProject;
        }
        chosen ??= DefaultProjectDirectory();

        SetProjectDirectory(chosen);
        _logger.LogInformation("Du lieu DU AN luu tai: {Path}", _projectDir);
        MigrateLegacyProjectData(env);
    }

    /// <summary>Thư mục dự án mặc định cho lần chạy đầu tiên: Documents\ParkingGuidance\Projects\Default.</summary>
    public static string DefaultProjectsRoot(string dataDir)
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return !string.IsNullOrWhiteSpace(docs)
            ? Path.Combine(docs, "ParkingGuidance", "Projects")
            : Path.Combine(Path.GetDirectoryName(dataDir.TrimEnd('\\', '/')) ?? dataDir, "projects");
    }

    private string DefaultProjectDirectory() => Path.Combine(DefaultProjectsRoot(_dataDir), "Default");

    public void SetProjectDirectory(string projectDir)
    {
        var full = Path.GetFullPath(projectDir);
        lock (_lock)
        {
            _projectDir = full;
            try
            {
                Directory.CreateDirectory(Path.Combine(full, "data"));
                Directory.CreateDirectory(Path.Combine(full, "uploads", "floors"));
                Directory.CreateDirectory(Path.Combine(full, "uploads", "layout"));
                Directory.CreateDirectory(Path.Combine(full, "led-images"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Khong tao duoc thu muc du an '{Path}'. Du lieu du an se KHONG duoc luu cho den khi khac phuc quyen ghi.", full);
            }
        }
    }

    public void PauseProjectWrites(bool paused)
    {
        lock (_lock) _projectWritesPaused = paused;
    }

    /// <summary>Lan dau chay voi thu muc du lieu moi: neu thu muc \"data\" cu (canh file thuc thi) con file thi
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

    /// <summary>Nâng cấp từ bản cũ (mọi thứ nằm chung trong thư mục dữ liệu hệ thống + wwwroot/uploads): lần đầu mở thư mục dự án
    /// còn TRỐNG (chưa có project.pgs.json, chưa có file JSON nào) thì SAO CHÉP phần dữ liệu dự án sang. Không xoá, không ghi đè
    /// file nguồn — bản cũ vẫn còn nguyên để đối chiếu.</summary>
    private void MigrateLegacyProjectData(IWebHostEnvironment env)
    {
        try
        {
            if (File.Exists(Path.Combine(_projectDir, ProjectFiles.Manifest))) return;
            var dst = ProjectDataDirectory;
            if (Directory.Exists(dst) && Directory.EnumerateFiles(dst, "*.json").Any()) return;

            int n = 0;
            foreach (var key in ProjectKeyList)
            {
                var src = Path.Combine(_dataDir, key + ".json");
                if (!File.Exists(src)) continue;
                File.Copy(src, Path.Combine(dst, key + ".json"), overwrite: false);
                n++;
            }
            n += CopyFiles(Path.Combine(_dataDir, "led-images"), ProjectLedImagesDirectory);
            var web = env.WebRootPath;
            if (!string.IsNullOrWhiteSpace(web))
            {
                n += CopyFiles(Path.Combine(web, "uploads", "floors"), Path.Combine(ProjectUploadsDirectory, "floors"));
                n += CopyFiles(Path.Combine(web, "uploads", "layout"), Path.Combine(ProjectUploadsDirectory, "layout"));
            }
            if (n > 0) _logger.LogInformation("Da chuyen {Count} file du lieu du an tu vi tri cu sang thu muc du an '{Path}'.", n, _projectDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Khong chuyen duoc du lieu du an tu vi tri cu (bo qua).");
        }
    }

    private static int CopyFiles(string srcDir, string dstDir)
    {
        if (!Directory.Exists(srcDir)) return 0;
        Directory.CreateDirectory(dstDir);
        int n = 0;
        foreach (var f in Directory.GetFiles(srcDir))
        {
            var name = Path.GetFileName(f);
            if (name.StartsWith('.')) continue;                 // .gitkeep
            var target = Path.Combine(dstDir, name);
            if (File.Exists(target)) continue;
            File.Copy(f, target);
            n++;
        }
        return n;
    }

    public bool Save<T>(string key, T data)
    {
        if (DeferredKeys.Contains(key))
        {
            lock (_lock)
            {
                if (!_projectWritesPaused) _unsaved = true;     // thông số đã áp dụng vào bộ nhớ; chờ nút Lưu chung mới ghi file
            }
            return true;
        }
        return WriteCore(key, data);
    }

    public bool Commit<T>(string key, T data) => WriteCore(key, data);

    private bool WriteCore<T>(string key, T data)
    {
        bool project = IsProjectKey(key);
        lock (_lock)
        {
            if (project && _projectWritesPaused) return true;   // dang chuyen du an: khong de du lieu du an cu ghi nham vao du an moi
            try
            {
                var dir = project ? ProjectDataDirectory : _dataDir;
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"{key}.json");
                var tmpPath = path + ".tmp";
                File.WriteAllText(tmpPath, JsonSerializer.Serialize(data, Options));
                File.Move(tmpPath, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Khong luu duoc du lieu '{Key}'. Thay doi nay co the mat khi restart.", key);
                return false;
            }
        }
    }

    public T? Load<T>(string key)
    {
        lock (_lock)
            return LoadCore<T>(key, IsProjectKey(key) ? ProjectDataDirectory : _dataDir);
    }

    private T? LoadCore<T>(string key, string dir)
    {
        var path = Path.Combine(dir, $"{key}.json");
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
