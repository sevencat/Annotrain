using System.Text.Json;
using Sevencat.Core.Localization;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Dataset.TinyLabel;

/// <summary>Web 端一张图的任务视图：清单字段 + 租约 + 负责人 + 是否已有标注。</summary>
public sealed record TinyLabelImageTask(
    long Id,
    long ProjectId,
    string FileName,
    string RelPath,
    string FilePath,
    int Width,
    int Height,
    long FileSizeBytes,
    string Sha256,
    string State,
    int Revision,
    string? Remark,
    string? Split,
    long? LockedBy,
    DateTime? LockExpiresAt,
    long? AssigneeId,
    DateTime UpdatedAt,
    bool HasAnnotation)
{
    /// <summary>租约是否还在有效期内（过期即视为无人锁定，别人可以接手）。</summary>
    public bool IsLockedAt(DateTime utcNow) => LockedBy is not null && LockExpiresAt > utcNow;
}

/// <summary>标注原文 + 版本；Web 端直接编辑 <see cref="DocJson"/>，服务端不改结构。</summary>
public sealed record TinyLabelAnnotationDoc(
    long ImageId,
    long ProjectId,
    int Revision,
    string DocJson,
    long UpdatedBy,
    DateTime UpdatedAt);

/// <summary>一个人的待办负载（<see cref="UserId"/> 为 <c>null</c> 表示没分配）。</summary>
public sealed record TinyLabelAssigneeLoad(long? UserId, int Assigned, int Labeled, int Reviewed);

/// <summary>项目统计：按状态分桶 + 人均负载。</summary>
public sealed record TinyLabelProjectStats(
    long ProjectId,
    int Total,
    int Pending,
    int Labeled,
    int Reviewed,
    int Excluded,
    int Skipped,
    int Unassigned,
    IReadOnlyList<TinyLabelAssigneeLoad> Workload);

/// <summary>抢任务时发现图被别人占着；带一份最新状态，客户端可以跟着刷新。</summary>
public sealed class TinyLabelImageLockedException(TinyLabelImageTask current)
    : Exception(Text.Format("Error.ImageLocked", current.Id))
{
    public TinyLabelImageTask Current { get; } = current;
}

/// <summary>保存标注时版本对不上（别人先存了）。</summary>
public sealed class TinyLabelRevisionConflictException(long imageId, int expected, int actual)
    : Exception(Text.Format("Error.RevisionConflict", imageId, expected, actual))
{
    public long ImageId { get; } = imageId;

    public int Expected { get; } = expected;

    public int Actual { get; } = actual;
}

/// <summary>
/// 标注 Web 端的任务流：下一张 / 抢图 / 放租约 / 跳过 / 退回、标注读写、按状态统计。
/// 桌面端不走这里（它直接编辑选中的图），所以这段只在 Web 服务端用。
/// </summary>
public sealed class TinyLabelTaskService
{
    /// <summary>租约时长；前端每 4 分钟自动续一次，超时后别人可以接手。</summary>
    public static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(5);

    public const string StatePending = "pending";
    public const string StateLabeled = "labeled";
    public const string StateReviewed = "reviewed";
    public const string StateExcluded = "excluded";
    public const string StateSkipped = "skipped";

    public Task<TinyLabelImageTask?> GetAsync(
        StorageLocation root,
        long imageId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ct.ThrowIfCancellationRequested();

        using TinyLabelDatabase database = Open(root);

        return Task.FromResult(FindTask(database, imageId));
    }

    /// <summary>
    /// 取下一张待标注：只看 <c>pending</c>，且没被别人有效占用；<paramref name="afterImageId"/>
    /// 用来接着上往下走，走到底再从最小 Id 兜回来。取到就顺手抢租约。
    /// </summary>
    public Task<TinyLabelImageTask?> TakeNextAsync(
        StorageLocation root,
        long projectId,
        long userId,
        long? afterImageId = null,
        TimeSpan? lease = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ct.ThrowIfCancellationRequested();

        using TinyLabelDatabase database = Open(root);
        DateTime now = DateTime.UtcNow;

        List<ImageEntity> candidates = database.Orm.Select<ImageEntity>()
            .Where(row => row.ProjectId == projectId && row.State == StatePending)
            .OrderBy(row => row.Id)
            .ToList()
            .Where(row => CanTake(row, userId, now))
            .ToList();

        if (candidates.Count == 0)
        {
            return Task.FromResult<TinyLabelImageTask?>(null);
        }

        ImageEntity picked = afterImageId is { } after
            ? candidates.FirstOrDefault(row => row.Id > after) ?? candidates[0]
            : candidates[0];

        return Task.FromResult<TinyLabelImageTask?>(ClaimCore(database, picked, userId, now, lease));
    }

    /// <summary>抢指定图：被别人有效占用时抛 <see cref="TinyLabelImageLockedException"/>。</summary>
    public Task<TinyLabelImageTask> ClaimAsync(
        StorageLocation root,
        long imageId,
        long userId,
        TimeSpan? lease = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ct.ThrowIfCancellationRequested();

        using TinyLabelDatabase database = Open(root);
        DateTime now = DateTime.UtcNow;
        ImageEntity row = Require(database, imageId);

        if (row.LockedBy is not null && row.LockedBy != userId && row.LockExpiresAt > now)
        {
            throw new TinyLabelImageLockedException(FindTask(database, imageId)!);
        }

        return Task.FromResult(ClaimCore(database, row, userId, now, lease));
    }

    /// <summary>放开租约（只放自己的；别人的租约不动）。</summary>
    public Task ReleaseAsync(
        StorageLocation root,
        long imageId,
        long userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ct.ThrowIfCancellationRequested();

        using TinyLabelDatabase database = Open(root);

        database.Orm.Update<ImageEntity>()
            .Where(row => row.Id == imageId && row.LockedBy == userId)
            .Set(row => row.LockedBy, (long?)null)
            .Set(row => row.LockExpiresAt, (DateTime?)null)
            .Set(row => row.UpdatedAt, DateTime.UtcNow)
            .ExecuteAffrows();

        return Task.CompletedTask;
    }

    /// <summary>跳过：留在自己名下，但状态变 <c>skipped</c>，不再出现在「下一张」里。</summary>
    public Task<TinyLabelImageTask> SkipAsync(
        StorageLocation root,
        long imageId,
        long userId,
        string? remark,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ct.ThrowIfCancellationRequested();

        using TinyLabelDatabase database = Open(root);
        ImageEntity row = Require(database, imageId);
        DateTime now = DateTime.UtcNow;

        database.Orm.Update<ImageEntity>()
            .Where(item => item.Id == imageId)
            .Set(item => item.State, StateSkipped)
            .Set(item => item.AssigneeId, row.AssigneeId ?? userId)
            .Set(item => item.Remark, Trim(remark))
            .Set(item => item.RemarkBy, userId)
            .Set(item => item.RemarkAt, now)
            .Set(item => item.LockedBy, (long?)null)
            .Set(item => item.LockExpiresAt, (DateTime?)null)
            .Set(item => item.UpdatedAt, now)
            .ExecuteAffrows();

        return Task.FromResult(FindTask(database, imageId)!);
    }

    /// <summary>退回池子：清掉负责人与租约，回到 <c>pending</c> 等别人接。</summary>
    public Task<TinyLabelImageTask> ReturnToPoolAsync(
        StorageLocation root,
        long imageId,
        long userId,
        string? remark,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ct.ThrowIfCancellationRequested();

        using TinyLabelDatabase database = Open(root);
        Require(database, imageId);
        DateTime now = DateTime.UtcNow;

        database.Orm.Update<ImageEntity>()
            .Where(item => item.Id == imageId)
            .Set(item => item.State, StatePending)
            .Set(item => item.AssigneeId, (long?)null)
            .Set(item => item.Remark, Trim(remark))
            .Set(item => item.RemarkBy, userId)
            .Set(item => item.RemarkAt, now)
            .Set(item => item.LockedBy, (long?)null)
            .Set(item => item.LockExpiresAt, (DateTime?)null)
            .Set(item => item.UpdatedAt, now)
            .ExecuteAffrows();

        return Task.FromResult(FindTask(database, imageId)!);
    }

    public Task<TinyLabelAnnotationDoc?> GetAnnotationAsync(
        StorageLocation root,
        long imageId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ct.ThrowIfCancellationRequested();

        using TinyLabelDatabase database = Open(root);

        AnnotationEntity? row = database.Orm.Select<AnnotationEntity>()
            .Where(item => item.ImageId == imageId)
            .First();

        return Task.FromResult(row is null ? null : ToDoc(row));
    }

    /// <summary>
    /// 保存标注：<paramref name="expectedRevision"/> 与库里不一致就抛冲突（乐观锁）。
    /// 版本对得上时同一个事务里写 annotations 并把 images.Revision +1。
    /// </summary>
    public Task<TinyLabelAnnotationDoc> SaveAnnotationAsync(
        StorageLocation root,
        long imageId,
        int expectedRevision,
        string docJson,
        long userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(docJson);
        ct.ThrowIfCancellationRequested();

        try
        {
            using JsonDocument _ = JsonDocument.Parse(docJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(Text.Get("Error.LabelFileInvalidJson"), ex);
        }

        using TinyLabelDatabase database = Open(root);
        ImageEntity image = Require(database, imageId);

        if (image.Revision != expectedRevision)
        {
            throw new TinyLabelRevisionConflictException(imageId, expectedRevision, image.Revision);
        }

        DateTime now = DateTime.UtcNow;
        int revision = expectedRevision + 1;
        string state = image.State is StateExcluded or StateReviewed ? image.State : StateLabeled;

        database.Orm.Ado.Transaction(() =>
        {
            database.Orm.Update<ImageEntity>()
                .Where(item => item.Id == imageId && item.Revision == expectedRevision)
                .Set(item => item.Revision, revision)
                .Set(item => item.State, state)
                .Set(item => item.AssigneeId, image.AssigneeId ?? userId)
                .Set(item => item.UpdatedAt, now)
                .ExecuteAffrows();

            int updated = database.Orm.Update<AnnotationEntity>()
                .Where(item => item.ImageId == imageId)
                .Set(item => item.DocJson, docJson)
                .Set(item => item.Revision, revision)
                .Set(item => item.UpdatedBy, userId)
                .Set(item => item.UpdatedAt, now)
                .ExecuteAffrows();

            if (updated == 0)
            {
                database.Orm.Insert(new AnnotationEntity
                {
                    ImageId = imageId,
                    ProjectId = image.ProjectId,
                    Revision = revision,
                    DocJson = docJson,
                    UpdatedBy = userId,
                    UpdatedAt = now,
                }).ExecuteAffrows();
            }
        });

        return Task.FromResult(new TinyLabelAnnotationDoc(
            imageId,
            image.ProjectId,
            revision,
            docJson,
            userId,
            now));
    }

    public Task<TinyLabelProjectStats?> GetStatsAsync(
        StorageLocation root,
        long projectId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ct.ThrowIfCancellationRequested();

        using TinyLabelDatabase database = Open(root);

        if (!database.Orm.Select<ProjectEntity>().Where(row => row.Id == projectId).Any())
        {
            return Task.FromResult<TinyLabelProjectStats?>(null);
        }

        List<ImageEntity> images = database.Orm.Select<ImageEntity>()
            .Where(row => row.ProjectId == projectId)
            .ToList();

        List<TinyLabelAssigneeLoad> workload = images
            .GroupBy(row => row.AssigneeId)
            .Select(group => new TinyLabelAssigneeLoad(
                group.Key,
                group.Count(),
                group.Count(row => row.State is StateLabeled),
                group.Count(row => row.State is StateReviewed)))
            .OrderByDescending(load => load.Assigned)
            .ToList();

        return Task.FromResult<TinyLabelProjectStats?>(new TinyLabelProjectStats(
            projectId,
            images.Count,
            images.Count(row => row.State == StatePending),
            images.Count(row => row.State == StateLabeled),
            images.Count(row => row.State == StateReviewed),
            images.Count(row => row.State == StateExcluded),
            images.Count(row => row.State == StateSkipped),
            images.Count(row => row.AssigneeId is null),
            workload));
    }

    private static TinyLabelImageTask ClaimCore(
        TinyLabelDatabase database,
        ImageEntity row,
        long userId,
        DateTime now,
        TimeSpan? lease)
    {
        DateTime expires = now.Add(lease ?? DefaultLease);

        database.Orm.Update<ImageEntity>()
            .Where(item => item.Id == row.Id)
            .Set(item => item.LockedBy, userId)
            .Set(item => item.LockExpiresAt, expires)
            .Set(item => item.AssigneeId, row.AssigneeId ?? userId)
            .Set(item => item.UpdatedAt, now)
            .ExecuteAffrows();

        return FindTask(database, row.Id)!;
    }

    private static bool CanTake(ImageEntity row, long userId, DateTime now)
        => (row.AssigneeId is null || row.AssigneeId == userId)
           && (row.LockedBy is null
               || row.LockedBy == userId
               || row.LockExpiresAt is null
               || row.LockExpiresAt <= now);

    private static TinyLabelImageTask? FindTask(TinyLabelDatabase database, long imageId)
    {
        ImageEntity? row = database.Orm.Select<ImageEntity>()
            .Where(item => item.Id == imageId)
            .First();

        if (row is null)
        {
            return null;
        }

        bool hasAnnotation = database.Orm.Select<AnnotationEntity>()
            .Where(item => item.ImageId == imageId)
            .Any();

        return new TinyLabelImageTask(
            row.Id,
            row.ProjectId,
            row.FileName,
            row.RelPath,
            row.FilePath,
            row.Width,
            row.Height,
            row.FileSizeBytes,
            row.Sha256,
            row.State,
            row.Revision,
            row.Remark,
            row.Split,
            row.LockedBy,
            row.LockExpiresAt,
            row.AssigneeId,
            row.UpdatedAt,
            hasAnnotation);
    }

    private static ImageEntity Require(TinyLabelDatabase database, long imageId)
        => database.Orm.Select<ImageEntity>().Where(item => item.Id == imageId).First()
           ?? throw new KeyNotFoundException(Text.Format("Error.ImageNotFound", imageId));

    private static TinyLabelAnnotationDoc ToDoc(AnnotationEntity row)
        => new(row.ImageId, row.ProjectId, row.Revision, row.DocJson, row.UpdatedBy, row.UpdatedAt);

    private static string? Trim(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static TinyLabelDatabase Open(StorageLocation root)
    {
        if (!root.IsFile)
        {
            throw new NotSupportedException(Text.Get("Error.DatabaseRequiresLocalFolder"));
        }

        string databasePath = Path.Combine(root.Root, TinyLabelProjectCatalog.DefaultDatabaseFile);

        return TinyLabelDatabase.Open(databasePath, createIfMissing: false);
    }
}
