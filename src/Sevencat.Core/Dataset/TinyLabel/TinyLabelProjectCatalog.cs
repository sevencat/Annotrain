using Microsoft.Extensions.Logging;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Dataset.TinyLabel;

/// <summary>左侧项目列表用的一行：项目基本信息 + 图数 / 已标数 / split。</summary>
public sealed record TinyLabelProjectSummary(
    long Id,
    string Name,
    string Description,
    int ImageCount,
    int LabeledCount,
    IReadOnlyList<string> Splits)
{
    public double Progress => ImageCount == 0 ? 0 : (double)LabeledCount / ImageCount;

    public string Summary => $"{ImageCount} 张 · 已标 {LabeledCount}";
}

/// <summary>
/// 列出一个数据目录（含 <c>annotrain.db</c>）里的全部项目。
/// 只读库、读完即关，不持有连接；目录里没有库时返回空列表。
/// </summary>
public sealed class TinyLabelProjectCatalog(
    BlobStoreRegistry registry,
    ILogger<TinyLabelProjectCatalog>? logger = null)
{
    public const string DefaultDatabaseFile = "annotrain.db";

    /// <summary>库文件不存在时返回空列表，不算错误（界面上就是"还没有项目"）。</summary>
    public async Task<IReadOnlyList<TinyLabelProjectSummary>> ListAsync(
        StorageLocation root,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (!root.IsFile)
        {
            throw new NotSupportedException("数据库形态只支持本地目录：SQLite 的 WAL 需要本地随机写");
        }

        IBlobStore store = registry.Get(root);
        var key = new StorageKey(root, DefaultDatabaseFile);

        if (!await store.ExistsAsync(key, ct).ConfigureAwait(false))
        {
            return [];
        }

        string databasePath = Path.Combine(root.Root, DefaultDatabaseFile);
        using TinyLabelDatabase database = TinyLabelDatabase.Open(databasePath, createIfMissing: false);

        List<ProjectEntity> projects = database.Orm.Select<ProjectEntity>()
            .OrderBy(project => project.Id)
            .ToList();

        List<ImageEntity> images = database.Orm.Select<ImageEntity>().ToList();
        HashSet<long> annotated = [.. database.Orm.Select<AnnotationEntity>().ToList(image => image.ImageId)];

        Dictionary<long, List<ImageEntity>> byProject = images
            .GroupBy(image => image.ProjectId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var result = new List<TinyLabelProjectSummary>(projects.Count);

        foreach (ProjectEntity project in projects)
        {
            ct.ThrowIfCancellationRequested();

            List<ImageEntity> own = byProject.TryGetValue(project.Id, out List<ImageEntity>? list) ? list : [];

            IReadOnlyList<string> splits = [.. own
                .Where(image => !string.IsNullOrWhiteSpace(image.Split))
                .Select(image => image.Split!.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(split => split, StringComparer.OrdinalIgnoreCase)];

            result.Add(new TinyLabelProjectSummary(
                project.Id,
                project.Name,
                project.Description,
                own.Count,
                own.Count(image => annotated.Contains(image.Id)),
                splits));
        }

        logger?.LogInformation("数据目录 {Root} 有 {Count} 个项目", root, result.Count);

        return result;
    }
}
