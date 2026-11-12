using Microsoft.Extensions.Logging;
using Sevencat.Core.Localization;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Dataset.TinyLabel;

/// <summary>左侧项目列表用的一行：项目基本信息 + 图数 / 已标数 / 待处理数 / split / 归档状态。</summary>
public sealed record TinyLabelProjectSummary(
    long Id,
    string Name,
    string Description,
    int ImageCount,
    int LabeledCount,
    int PendingCount,
    DateTime CreatedAt,
    DateTime? ClosedAt,
    IReadOnlyList<string> Splits)
{
    /// <summary><c>import</c>（图片复制进项目目录）或 <c>scan</c>（引用原图）；Web 端原样透传。</summary>
    public string StorageMode { get; init; } = "import";

    /// <summary>已结束（归档）的项目在列表里沉到下方。</summary>
    public bool IsClosed => ClosedAt is not null;

    public double Progress => ImageCount == 0 ? 0 : (double)LabeledCount / ImageCount;

    /// <summary>「图片 14 张 · 已处理 9 张 · 待处理 5 张」。</summary>
    public string Stats => $"图片 {ImageCount} 张 · 已处理 {LabeledCount} 张 · 待处理 {PendingCount} 张";

    public string CreatedText => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd");

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
            throw new NotSupportedException(Text.Get("Error.DatabaseRequiresLocalFolder"));
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

            // 已处理 = 有任何标注状态的图（不含 pending）；待处理 = 还没标的。
            int labeled = own.Count(image => !string.Equals(image.State, "pending", StringComparison.OrdinalIgnoreCase));
            int pending = own.Count - labeled;

            result.Add(new TinyLabelProjectSummary(
                project.Id,
                project.Name,
                project.Description,
                own.Count,
                labeled,
                pending,
                project.CreatedAt,
                project.ClosedAt,
                splits)
            {
                StorageMode = project.StorageMode,
            });
        }

        // 未结束优先，其次按创建时间倒序（与服务端列表一致）
        result.Sort((left, right) =>
        {
            int closed = left.IsClosed.CompareTo(right.IsClosed);

            return closed != 0 ? closed : right.CreatedAt.CompareTo(left.CreatedAt);
        });

        logger?.LogInformation("数据目录 {Root} 有 {Count} 个项目", root, result.Count);

        return result;
    }
}
