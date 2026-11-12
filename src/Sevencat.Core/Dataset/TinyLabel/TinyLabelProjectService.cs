using Microsoft.Extensions.Logging;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Dataset.TinyLabel;

/// <summary>
/// 项目级写操作：新建项目。库不存在时自动建库建表，界面上「新建项目」直接落到当前数据目录。
/// </summary>
public sealed class TinyLabelProjectService(
    ILogger<TinyLabelProjectService>? logger = null)
{
    public const int MaxNameLength = 128;

    /// <summary>在当前数据目录的 <c>annotrain.db</c> 里新建一个空项目，返回项目 Id。</summary>
    public Task<long> CreateAsync(
        StorageLocation root,
        string name,
        string? description = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ct.ThrowIfCancellationRequested();

        if (!root.IsFile)
        {
            throw new NotSupportedException("数据库形态只支持本地目录：SQLite 的 WAL 需要本地随机写");
        }

        string trimmed = (name ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            throw new ArgumentException("项目名不能为空", nameof(name));
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException($"项目名不能超过 {MaxNameLength} 个字符", nameof(name));
        }

        Directory.CreateDirectory(root.Root);
        string databasePath = Path.Combine(root.Root, TinyLabelProjectCatalog.DefaultDatabaseFile);

        using TinyLabelDatabase database = TinyLabelDatabase.Open(databasePath);

        long projectId = (long)database.Orm.Insert(new ProjectEntity
        {
            Name = trimmed,
            Description = description?.Trim() ?? string.Empty,
            StorageMode = "copy",
            CreatedBy = 0,
            CreatedAt = DateTime.UtcNow,
        }).ExecuteIdentity();

        logger?.LogInformation("新建项目 {Name}（Id={Id}）于 {Root}", trimmed, projectId, root);

        return Task.FromResult(projectId);
    }
}
