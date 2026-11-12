using FreeSql;
using Microsoft.Extensions.Logging;
using Sevencat.Core.Localization;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Dataset.TinyLabel;

/// <summary>类别表的一行；<see cref="OrderIndex"/> 就是导出 YOLO 时的 classId。</summary>
public sealed record TinyLabelClassRow(long Id, string Name, int OrderIndex, string Color)
{
    public string IndexText => OrderIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// 项目类别维护：列表 / 新增 / 重命名 / 删除。
/// <para>
/// <see cref="OrderIndex"/> 由服务端分配且不复用，顺序即 YOLO 的 classId；
/// 所以只允许"追加"和"改名"，不做重排，避免历史导出的索引含义漂移。
/// </para>
/// </summary>
public sealed class TinyLabelClassService(
    IFreeSql orm,
    ILogger<TinyLabelClassService>? logger = null)
{
    public Task<IReadOnlyList<TinyLabelClassRow>> ListAsync(
        StorageLocation root,
        long projectId,
        CancellationToken ct = default)
    {
        IReadOnlyList<TinyLabelClassRow> rows = [.. orm.Select<ProjectClassEntity>()
            .Where(row => row.ProjectId == projectId)
            .OrderBy(row => row.OrderIndex)
            .ToList()
            .Select(row => new TinyLabelClassRow(row.Id, row.Name, row.OrderIndex, row.Color))];

        ct.ThrowIfCancellationRequested();

        return Task.FromResult(rows);
    }

    /// <summary>追加一个类别，返回分配到的 classId（= OrderIndex）。</summary>
    public Task<int> AddAsync(
        StorageLocation root,
        long projectId,
        string name,
        string color = "#3B82F6",
        CancellationToken ct = default)
    {
        string trimmed = Validate(name);
        bool exists = orm.Select<ProjectClassEntity>()
            .Where(row => row.ProjectId == projectId && row.Name == trimmed)
            .Any();

        if (exists)
        {
            throw new InvalidOperationException(Text.Format("Error.ClassExists", trimmed));
        }

        bool hasClasses = orm.Select<ProjectClassEntity>()
            .Where(row => row.ProjectId == projectId)
            .Any();

        int next = hasClasses
            ? orm.Select<ProjectClassEntity>()
                .Where(row => row.ProjectId == projectId)
                .Max(row => row.OrderIndex) + 1
            : 0;

        orm.Insert(new ProjectClassEntity
        {
            ProjectId = projectId,
            Name = trimmed,
            OrderIndex = next,
            Color = NormalizeColor(color),
        }).ExecuteAffrows();

        logger?.LogInformation("项目 {Project} 新增类别 {Name}（classId={Index}）", projectId, trimmed, next);

        ct.ThrowIfCancellationRequested();

        return Task.FromResult(next);
    }

    /// <summary>改名；classId 不变，已标注的数据不受影响。</summary>
    public Task RenameAsync(
        StorageLocation root,
        long projectId,
        long classId,
        string name,
        CancellationToken ct = default)
    {
        string trimmed = Validate(name);
        bool duplicated = orm.Select<ProjectClassEntity>()
            .Where(row => row.ProjectId == projectId && row.Name == trimmed && row.Id != classId)
            .Any();

        if (duplicated)
        {
            throw new InvalidOperationException(Text.Format("Error.ClassExists", trimmed));
        }

        int affected = orm.Update<ProjectClassEntity>()
            .Where(row => row.Id == classId && row.ProjectId == projectId)
            .Set(row => row.Name, trimmed)
            .ExecuteAffrows();

        if (affected == 0)
        {
            throw new InvalidOperationException(Text.Format("Error.ClassNotFound", classId));
        }

        ct.ThrowIfCancellationRequested();

        return Task.CompletedTask;
    }

    /// <summary>
    /// 删除类别：只有"还没有任何标注用到它"时才允许物理删除；
    /// 否则提示改成改名，避免历史标注里的 label 找不到 classId。
    /// </summary>
    public Task RemoveAsync(
        StorageLocation root,
        long projectId,
        long classId,
        CancellationToken ct = default)
    {
        ProjectClassEntity entity = orm.Select<ProjectClassEntity>()
            .Where(row => row.Id == classId && row.ProjectId == projectId)
            .First() ?? throw new InvalidOperationException(Text.Format("Error.ClassNotFound", classId));

        bool used = orm.Select<AnnotationEntity>()
            .Where(row => row.ProjectId == projectId)
            .ToList(row => row.DocJson)
            .Any(json => json.Contains($"\"{entity.Name}\"", StringComparison.Ordinal));

        if (used)
        {
            throw new InvalidOperationException(Text.Format("Error.ClassInUse", entity.Name));
        }

        orm.Delete<ProjectClassEntity>()
            .Where(row => row.Id == classId)
            .ExecuteAffrows();

        logger?.LogInformation("项目 {Project} 删除类别 {Name}（classId={Index}）", projectId, entity.Name, entity.OrderIndex);

        ct.ThrowIfCancellationRequested();

        return Task.CompletedTask;
    }

    private static string Validate(string? name)
    {
        string trimmed = (name ?? string.Empty).Trim();

        return trimmed.Length == 0
            ? throw new ArgumentException(Text.Get("Classes.NameRequired"), nameof(name))
            : trimmed;
    }

    private static string NormalizeColor(string? color)
    {
        string trimmed = (color ?? string.Empty).Trim();

        return trimmed.Length is 7 && trimmed[0] == '#'
            ? trimmed.ToUpperInvariant()
            : "#3B82F6";
    }
}
