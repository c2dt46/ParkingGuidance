using System.Text.Json;
using ParkingGuidance.Web.Services.Persistence;

namespace ParkingGuidance.Web.Services.Project;

public sealed class ProjectService : IProjectService
{
    private const int MaxRecent = 8;
    private static readonly JsonSerializerOptions JsonOpt = new() { WriteIndented = true };

    private readonly IPersistenceService _persistence;
    private readonly IReadOnlyList<IProjectScoped> _parts;
    private readonly ILogger<ProjectService> _logger;
    private readonly object _gate = new();
    private ProjectManifest _manifest;

    public event Action? Changed;

    public ProjectService(IPersistenceService persistence, IEnumerable<IProjectScoped> parts, ILogger<ProjectService> logger)
    {
        _persistence = persistence;
        _parts = parts.ToList();
        _logger = logger;

        lock (_gate)
        {
            _manifest = ReadManifest(_persistence.ProjectDirectory) ?? NewManifest(_persistence.ProjectDirectory, null);
            WriteManifest(_persistence.ProjectDirectory, _manifest);
            RememberCurrent();
            _persistence.MarkProjectSaved();      // lúc khởi động bộ nhớ vừa nạp xong từ file: chưa có thay đổi nào
        }
    }

    public ProjectInfo Current
    {
        get
        {
            lock (_gate)
                return new ProjectInfo
                {
                    Name = _manifest.Name,
                    Path = _persistence.ProjectDirectory,
                    CreatedUtc = _manifest.CreatedUtc,
                    SavedUtc = _manifest.SavedUtc
                };
        }
    }

    public IReadOnlyList<RecentProject> Recent
    {
        get
        {
            var st = _persistence.Load<AppState>("app-state");
            var cur = Path.GetFullPath(_persistence.ProjectDirectory);
            return (st?.Recent ?? new())
                .Where(r => !string.IsNullOrWhiteSpace(r.Path)
                            && !string.Equals(SafeFull(r.Path), cur, StringComparison.OrdinalIgnoreCase)
                            && File.Exists(Path.Combine(r.Path, ProjectFiles.Manifest)))
                .ToList();
        }
    }

    // ------------------------------------------------------------------ lưu chung
    public ProjectInfo Save()
    {
        lock (_gate)
        {
            SaveLocked();
            return Current;
        }
    }

    private void SaveLocked()
    {
        var failed = new List<string>();
        foreach (var p in _parts)
        {
            try { if (!p.FlushProject()) failed.Add(p.ProjectPartName); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Loi khi luu phan '{Part}' cua du an.", p.ProjectPartName);
                failed.Add(p.ProjectPartName);
            }
        }

        _manifest.SavedUtc = DateTime.UtcNow;
        if (!WriteManifest(_persistence.ProjectDirectory, _manifest)) failed.Add("project.pgs.json");

        if (failed.Count == 0) _persistence.MarkProjectSaved();
        if (failed.Count > 0)
            throw new InvalidOperationException("Không lưu được: " + string.Join(", ", failed) + ". Kiểm tra quyền ghi của thư mục dự án và dung lượng ổ đĩa.");
    }

    // ------------------------------------------------------------------ mở / tạo / lưu thành
    public ProjectInfo Open(string path)
    {
        var full = FullPath(path);
        if (!Directory.Exists(full)) throw new InvalidOperationException("Thư mục không tồn tại.");
        if (!File.Exists(Path.Combine(full, ProjectFiles.Manifest)))
            throw new InvalidOperationException($"Thư mục này không phải dự án PGS (không có {ProjectFiles.Manifest}).");

        lock (_gate)
        {
            SwitchTo(full);                     // KHÔNG tự lưu dự án đang mở: việc lưu do người dùng bấm nút Lưu (giao diện hỏi trước khi mở)
        }
        Changed?.Invoke();
        return Current;
    }

    public ProjectInfo Create(string path, string? name)
    {
        var full = FullPath(path);
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
            throw new InvalidOperationException("Thư mục đã có nội dung. Hãy chọn / đặt tên một thư mục mới (trống).");

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(full);
                WriteManifest(full, NewManifest(full, name));
            }
            catch (Exception ex) { throw new InvalidOperationException("Không tạo được thư mục dự án: " + ex.Message); }
            SwitchTo(full);
            SaveLocked();                       // dự án mới: ghi các file mặc định ngay để thư mục có đủ nội dung
        }
        Changed?.Invoke();
        return Current;
    }

    public ProjectInfo SaveAs(string path, string? name)
    {
        var full = FullPath(path);
        lock (_gate)
        {
            var cur = Path.GetFullPath(_persistence.ProjectDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), cur, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Đây đang là thư mục của dự án hiện tại.");
            if (full.StartsWith(cur + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Không thể lưu bản sao vào bên trong thư mục của chính dự án.");
            if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
                throw new InvalidOperationException("Thư mục đã có nội dung. Hãy chọn / đặt tên một thư mục mới (trống).");

            // Bản sao = file đã lưu của dự án hiện tại + thông số đang chạy (ghi đè ở SaveLocked bên dưới). Dự án gốc không bị đổi.
            _persistence.PauseProjectWrites(true);
            try
            {
                CopyDirectory(cur, full);
                var m = ReadManifest(full) ?? NewManifest(full, name);
                if (!string.IsNullOrWhiteSpace(name)) m.Name = name.Trim();
                m.CreatedUtc = DateTime.UtcNow;
                WriteManifest(full, m);
                _persistence.SetProjectDirectory(full);
                _manifest = m;
            }
            catch (Exception ex)
            {
                _persistence.SetProjectDirectory(cur);   // sao chép lỗi: ở lại dự án cũ
                throw new InvalidOperationException("Không sao chép được dự án: " + ex.Message);
            }
            finally { _persistence.PauseProjectWrites(false); }

            SaveLocked();
            RememberCurrent();
        }
        Changed?.Invoke();
        return Current;
    }

    /// <summary>Chuyển sang thư mục dự án khác: trỏ đường dẫn, nạp lại mọi phần dữ liệu, rồi ghi bản đã chuẩn hoá. Lỗi thì quay lại dự án cũ.</summary>
    private void SwitchTo(string full)
    {
        var old = _persistence.ProjectDirectory;
        var errors = new List<string>();

        _persistence.PauseProjectWrites(true);         // dữ liệu của dự án cũ (đang nằm trong RAM) không được ghi vào thư mục dự án mới
        try
        {
            _persistence.SetProjectDirectory(full);
            foreach (var p in _parts)
            {
                try { p.ReloadProject(); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Loi khi nap lai phan '{Part}' tu du an '{Path}'.", p.ProjectPartName, full);
                    errors.Add($"{p.ProjectPartName}: {ex.Message}");
                }
            }

            if (errors.Count > 0)
            {
                _persistence.SetProjectDirectory(old);
                foreach (var p in _parts)
                    try { p.ReloadProject(); } catch (Exception ex) { _logger.LogError(ex, "Khong khoi phuc duoc phan '{Part}'.", p.ProjectPartName); }
            }
        }
        finally { _persistence.PauseProjectWrites(false); }

        if (errors.Count > 0)
            throw new InvalidOperationException("Không mở được dự án (đã quay lại dự án cũ): " + string.Join("; ", errors));

        _manifest = ReadManifest(full) ?? NewManifest(full, null);
        _persistence.MarkProjectSaved();               // bộ nhớ vừa nạp từ dự án này: khớp với file
        RememberCurrent();
    }

    // ------------------------------------------------------------------ nhãn dự án + danh sách gần đây
    private static ProjectManifest NewManifest(string dir, string? name)
    {
        var n = string.IsNullOrWhiteSpace(name) ? new DirectoryInfo(dir).Name : name.Trim();
        return new ProjectManifest { Name = n, CreatedUtc = DateTime.UtcNow, SavedUtc = DateTime.UtcNow };
    }

    private ProjectManifest? ReadManifest(string dir)
    {
        try
        {
            var f = Path.Combine(dir, ProjectFiles.Manifest);
            return File.Exists(f) ? JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllText(f), JsonOpt) : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Khong doc duoc {File} trong '{Dir}'.", ProjectFiles.Manifest, dir);
            return null;
        }
    }

    private bool WriteManifest(string dir, ProjectManifest m)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var f = Path.Combine(dir, ProjectFiles.Manifest);
            File.WriteAllText(f + ".tmp", JsonSerializer.Serialize(m, JsonOpt));
            File.Move(f + ".tmp", f, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Khong ghi duoc {File} trong '{Dir}'.", ProjectFiles.Manifest, dir);
            return false;
        }
    }

    private void RememberCurrent()
    {
        try
        {
            var st = _persistence.Load<AppState>("app-state") ?? new AppState();
            var dir = Path.GetFullPath(_persistence.ProjectDirectory);
            st.LastProject = dir;
            st.Recent.RemoveAll(r => string.Equals(SafeFull(r.Path), dir, StringComparison.OrdinalIgnoreCase));
            st.Recent.Insert(0, new RecentProject { Name = _manifest.Name, Path = dir, LastOpenedUtc = DateTime.UtcNow });
            if (st.Recent.Count > MaxRecent) st.Recent.RemoveRange(MaxRecent, st.Recent.Count - MaxRecent);
            _persistence.Save("app-state", st);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Khong ghi duoc danh sach du an gan day."); }
    }

    private static string SafeFull(string p)
    {
        try { return Path.GetFullPath(p); } catch { return p ?? ""; }
    }

    private static string FullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Chưa chọn thư mục.");
        try { return Path.GetFullPath(path.Trim()); }
        catch { throw new InvalidOperationException("Đường dẫn không hợp lệ."); }
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
        {
            if (f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: false);
        }
        foreach (var d in Directory.GetDirectories(src))
            CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }
}
