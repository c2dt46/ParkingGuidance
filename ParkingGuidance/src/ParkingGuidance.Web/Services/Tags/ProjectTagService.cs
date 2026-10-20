using System.Text.RegularExpressions;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Persistence;

namespace ParkingGuidance.Web.Services.Tags;

public interface IProjectTagService
{
    IReadOnlyList<ProjectTag> GetAll();
    ProjectTag? Get(string name);
    (ProjectTag? Tag, string? Error) Create(ProjectTag dto);
    /// <summary>Sửa tag (có thể đổi tên). Đổi kiểu dữ liệu thì giá trị được chuẩn hoá lại theo kiểu mới.</summary>
    (ProjectTag? Tag, string? Error) Update(string oldName, ProjectTag dto);
    bool Delete(string name);
    (ProjectTag? Tag, string? Error) SetValue(string name, string? value);
    /// <summary>Tên → giá trị hiện tại (trang Tags gọi định kỳ).</summary>
    Dictionary<string, string> GetValues();
}

/// <summary>
/// Project tags do người dùng khai báo. Lưu ở data/project-tags.json.
/// Độc lập với luồng chính: chỉ dùng IPersistenceService, mọi lỗi lưu/đọc đều bị chặn tại đây.
/// </summary>
public sealed class ProjectTagService : IProjectTagService
{
    private const string StateKey = "project-tags";
    public const int MaxTags = 5000;
    private const int MaxDescription = 200;

    private static readonly Regex NameRx = new(@"^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.Compiled);
    private static readonly string[] Scopes = { "Local", "Global" };

    private readonly IPersistenceService _persistence;
    private readonly ILogger<ProjectTagService> _logger;
    private readonly object _lock = new();
    private readonly List<ProjectTag> _tags;

    public ProjectTagService(IPersistenceService persistence, ILogger<ProjectTagService> logger)
    {
        _persistence = persistence;
        _logger = logger;
        _tags = new List<ProjectTag>();
        try
        {
            foreach (var t in _persistence.Load<List<ProjectTag>>(StateKey) ?? new())
            {
                if (t is null || !NameRx.IsMatch(t.Name ?? "")) continue;
                if (SystemTagService.IsReserved(t.Name)) continue;
                if (_tags.Any(x => x.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase))) continue;
                TagValue.TryNormalize(t.Type, t.Value, out var v, out _);
                _tags.Add(new ProjectTag
                {
                    Name = t.Name, Type = t.Type, Value = v,
                    Description = (t.Description ?? "").Trim(),
                    Scope = NormalizeScope(t.Scope)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Không đọc được Project tags; dùng danh sách trống.");
        }
    }

    public IReadOnlyList<ProjectTag> GetAll()
    {
        lock (_lock) return _tags.Select(t => t.Clone()).ToList();
    }

    public ProjectTag? Get(string name)
    {
        lock (_lock) return Find(name)?.Clone();
    }

    public Dictionary<string, string> GetValues()
    {
        lock (_lock) return _tags.ToDictionary(t => t.Name, t => t.Value);
    }

    public (ProjectTag? Tag, string? Error) Create(ProjectTag dto)
    {
        lock (_lock)
        {
            if (_tags.Count >= MaxTags) return (null, $"Tối đa {MaxTags} Project tags.");
            var (clean, error) = Validate(dto, ignoreName: null);
            if (clean is null) return (null, error);
            _tags.Add(clean);
            Persist();
            return (clean.Clone(), null);
        }
    }

    public (ProjectTag? Tag, string? Error) Update(string oldName, ProjectTag dto)
    {
        lock (_lock)
        {
            var cur = Find(oldName);
            if (cur is null) return (null, $"Không tìm thấy tag '{oldName}'.");
            var (clean, error) = Validate(dto, ignoreName: cur.Name);
            if (clean is null) return (null, error);
            cur.Name = clean.Name;
            cur.Type = clean.Type;
            cur.Value = clean.Value;
            cur.Description = clean.Description;
            cur.Scope = clean.Scope;
            Persist();
            return (cur.Clone(), null);
        }
    }

    public bool Delete(string name)
    {
        lock (_lock)
        {
            var cur = Find(name);
            if (cur is null) return false;
            _tags.Remove(cur);
            Persist();
            return true;
        }
    }

    public (ProjectTag? Tag, string? Error) SetValue(string name, string? value)
    {
        lock (_lock)
        {
            var cur = Find(name);
            if (cur is null) return (null, $"Không tìm thấy tag '{name}'.");
            if (!TagValue.TryNormalize(cur.Type, value, out var v, out var error)) return (null, error);
            if (cur.Value != v)
            {
                cur.Value = v;
                Persist();
            }
            return (cur.Clone(), null);
        }
    }

    // ------------------------------------------------------------------ nội bộ
    private ProjectTag? Find(string? name) =>
        string.IsNullOrEmpty(name) ? null : _tags.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeScope(string? scope) =>
        Scopes.FirstOrDefault(s => s.Equals(scope?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "Local";

    private (ProjectTag? Clean, string? Error) Validate(ProjectTag dto, string? ignoreName)
    {
        var name = (dto.Name ?? "").Trim();
        if (name.Length == 0) return (null, "Tên tag không được để trống.");
        if (!NameRx.IsMatch(name))
            return (null, "Tên tag chỉ gồm chữ không dấu, số và gạch dưới (_), bắt đầu bằng chữ hoặc _, tối đa 64 ký tự.");
        if (SystemTagService.IsReserved(name))
            return (null, $"'{name}' trùng tên System tag, hãy đặt tên khác.");
        if (_tags.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                           && !(ignoreName is not null && t.Name.Equals(ignoreName, StringComparison.OrdinalIgnoreCase))))
            return (null, $"Tag '{name}' đã tồn tại.");
        if (!Enum.IsDefined(dto.Type)) return (null, "Kiểu dữ liệu không hợp lệ.");

        var desc = (dto.Description ?? "").Trim();
        if (desc.Length > MaxDescription) return (null, $"Mô tả tối đa {MaxDescription} ký tự.");

        var scopeRaw = dto.Scope?.Trim();
        if (!string.IsNullOrEmpty(scopeRaw) && !Scopes.Any(s => s.Equals(scopeRaw, StringComparison.OrdinalIgnoreCase)))
            return (null, "Scope chỉ nhận Local hoặc Global.");

        if (!TagValue.TryNormalize(dto.Type, dto.Value, out var value, out var error)) return (null, error);

        return (new ProjectTag { Name = name, Type = dto.Type, Value = value, Description = desc, Scope = NormalizeScope(scopeRaw) }, null);
    }

    /// <summary>Gọi khi đang giữ khoá. IPersistenceService.Save đã tự bắt lỗi; bọc thêm cho chắc.</summary>
    private void Persist()
    {
        try { _persistence.Save(StateKey, _tags); }
        catch (Exception ex) { _logger.LogError(ex, "Không lưu được Project tags."); }
    }
}
