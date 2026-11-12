using System.Security.Cryptography;
using FreeSql;
using Microsoft.Extensions.Logging;
using Sevencat.Core.Imaging;
using Sevencat.Core.Localization;
using Sevencat.Core.Model;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Dataset.TinyLabel;

/// <summary>图片清单的一行（状态 / 版本 / 备注 / 最后标注信息都带出来，界面直接用）。</summary>
public sealed record TinyLabelImageRow(
    long Id,
    long ProjectId,
    string FileName,
    string RelPath,
    string FilePath,
    int Width,
    int Height,
    string State,
    int Revision,
    string? Remark,
    string? Split,
    DateTime? AnnotatedAt = null,
    long AnnotatedBy = 0,
    string? AnnotatorName = null)
{
    public string SizeText => $"{Width}×{Height}";

    public string StateText => State switch
    {
        "pending" => "未标注",
        "labeled" => "已标注",
        "reviewed" => "已通过",
        "excluded" => "已排除",
        "skipped" => "已跳过",
        _ => State,
    };

    public string RevisionText => $"rev {Revision}";

    public string SplitText => string.IsNullOrWhiteSpace(Split) ? "未划分" : Split;

    public string RemarkText => string.IsNullOrWhiteSpace(Remark) ? "—" : Remark;
}

/// <summary>一页图片清单。</summary>
public sealed record TinyLabelImagePage(
    IReadOnlyList<TinyLabelImageRow> Items,
    int Total,
    int PageIndex,
    int PageSize)
{
    public int PageCount => PageSize <= 0 ? 1 : Math.Max(1, (Total + PageSize - 1) / PageSize);

    /// <summary>界面上从 1 开始的页码。</summary>
    public int DisplayPage => PageIndex + 1;

    public string PageText => $"第 {DisplayPage} 页 / 共 {PageCount} 页";

    public string TotalText => $"共 {Total} 张";
}

/// <summary>导入图片的结果。</summary>
public sealed record TinyLabelImageImportReport(int Imported, int Skipped, IReadOnlyList<string> Warnings)
{
    public string Summary => $"导入 {Imported} 张，跳过 {Skipped} 张";
}

/// <summary>
/// 图片清单的读取与维护：
/// <list type="bullet">
/// <item>分页查询（状态 / split / 关键词过滤）</item>
/// <item>改状态与备注（标注保存时由 <see cref="Annotation.DatabaseAnnotationStore"/> 自动置 labeled）</item>
/// <item>导入图片（多选文件 / 整个文件夹，复制进项目目录）与扫描目录（只登记，不复制）</item>
/// </list>
/// </summary>
public sealed class TinyLabelImageService(
    IImageInfoSource infoSource,
    ILogger<TinyLabelImageService>? logger = null)
{
    /// <summary>分页查询项目内的图片。</summary>
    public Task<TinyLabelImagePage> QueryAsync(
        StorageLocation root,
        long projectId,
        int pageIndex,
        int pageSize,
        string? keyword = null,
        string? state = null,
        string? split = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);

        pageSize = Math.Clamp(pageSize, 1, 500);
        pageIndex = Math.Max(0, pageIndex);

        TinyLabelWorkspace.RequireDatabase(root);
        TinyLabelDatabase database = TinyLabelDatabase.Current;

        // 过滤 / 排序 / 分页全部在 SQL 里做：图片上万条的项目也不能整表读进内存再 LINQ
        ISelect<ImageEntity> query = database.Orm
            .Select<ImageEntity>()
            .Where(row => row.ProjectId == projectId);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            string needle = keyword.Trim();
            query = query.Where(row => row.FileName.Contains(needle) || row.RelPath.Contains(needle));
        }

        if (!string.IsNullOrWhiteSpace(state))
        {
            string value = state.Trim();
            query = query.Where(row => row.State == value);
        }

        if (!string.IsNullOrWhiteSpace(split))
        {
            string value = split.Trim();
            query = query.Where(row => row.Split == value);
        }

        int total = (int)query.Count();

        List<ImageEntity> window = query
            .OrderBy(row => row.Split)
            .OrderBy(row => row.FileName)
            // 同 Split + 同文件名的行也要有稳定次序，翻页才不会重复或漏行
            .OrderBy(row => row.Id)
            .Skip(pageIndex * pageSize)
            .Limit(pageSize)
            .ToList();

        // 最后标注信息与用户显示名都只查这一页的 20 条，避免把整项目标注原文读出来
        Dictionary<long, (long UserId, DateTime At)> stamps = ReadAnnotationStamps(database, window);
        Dictionary<long, string> users = ReadUserNames(database);

        List<TinyLabelImageRow> page = [.. window.Select(row =>
        {
            stamps.TryGetValue(row.Id, out (long UserId, DateTime At) stamp);

            return new TinyLabelImageRow(
                row.Id,
                row.ProjectId,
                row.FileName,
                row.RelPath,
                row.FilePath,
                row.Width,
                row.Height,
                row.State,
                row.Revision,
                row.Remark,
                row.Split,
                stamp.At == default ? null : stamp.At,
                stamp.UserId,
                AnnotatorOf(stamp, users));
        })];

        ct.ThrowIfCancellationRequested();

        return Task.FromResult(new TinyLabelImagePage(page, total, pageIndex, pageSize));
    }

    /// <summary>这一页图片的标注时间与标注人（<c>annotations</c> 行，没有标注就没有条目）。</summary>
    private static Dictionary<long, (long UserId, DateTime At)> ReadAnnotationStamps(
        TinyLabelDatabase database,
        IReadOnlyList<ImageEntity> window)
    {
        if (window.Count == 0)
        {
            return [];
        }

        List<long> ids = [.. window.Select(row => row.Id)];

        // 只取时间与用户 Id，不把 DocJson 整段读出来
        List<AnnotationStamp> rows = database.Orm
            .Select<AnnotationEntity>()
            .Where(row => ids.Contains(row.ImageId))
            .ToList(row => new AnnotationStamp
            {
                ImageId = row.ImageId,
                UpdatedBy = row.UpdatedBy,
                UpdatedAt = row.UpdatedAt,
            });

        return rows.ToDictionary(row => row.ImageId, row => (row.UpdatedBy, row.UpdatedAt));
    }

    /// <summary><c>web_users</c> 的 Id → 显示名；库里没有这张表（纯桌面端项目）就返回空。</summary>
    private Dictionary<long, string> ReadUserNames(TinyLabelDatabase database)
    {
        try
        {
            bool exists = database.Orm.Ado.Query<int>(
                "select count(1) from sqlite_master where type='table' and name='web_users'")[0] > 0;

            if (!exists)
            {
                return [];
            }

            List<WebUserNameRow> rows = database.Orm.Ado
                .Query<WebUserNameRow>("select Id, DisplayName from web_users");

            return rows
                .Where(row => !string.IsNullOrWhiteSpace(row.DisplayName))
                .ToDictionary(row => row.Id, row => row.DisplayName!);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger?.LogWarning(error, "读取 web_users 失败，标注人显示为用户 Id");
            return [];
        }
    }

    /// <summary>标注人显示名：没有标注留空，<c>0</c> 是桌面端本机，其它按 <c>web_users</c> 反查。</summary>
    private static string? AnnotatorOf(
        (long UserId, DateTime At) stamp,
        IReadOnlyDictionary<long, string> users)
    {
        if (stamp.At == default)
        {
            return null;
        }

        if (stamp.UserId == 0)
        {
            return Text.Get("Label.LocalAnnotator");
        }

        return users.TryGetValue(stamp.UserId, out string? name) ? name : $"#{stamp.UserId}";
    }

    /// <summary>标注行的读取投影（只要时间与人，不碰 <c>DocJson</c>）。</summary>
    private sealed class AnnotationStamp
    {
        public long ImageId { get; set; }

        public long UpdatedBy { get; set; }

        public DateTime UpdatedAt { get; set; }
    }

    /// <summary><c>web_users</c> 的只读投影。</summary>
    private sealed class WebUserNameRow
    {
        public long Id { get; set; }

        public string? DisplayName { get; set; }
    }

    /// <summary>改图片状态；<paramref name="remark"/> 为 null 时保留原备注。</summary>
    public Task SetStateAsync(
        StorageLocation root,
        long imageId,
        string state,
        string? remark = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        TinyLabelWorkspace.RequireDatabase(root);
        TinyLabelDatabase database = TinyLabelDatabase.Current;

        var update = database.Orm.Update<ImageEntity>()
            .Where(row => row.Id == imageId)
            .Set(row => row.State, state.Trim().ToLowerInvariant())
            .Set(row => row.UpdatedAt, DateTime.UtcNow);

        if (remark is not null)
        {
            update = update
                .Set(row => row.Remark, remark.Trim())
                .Set(row => row.RemarkAt, DateTime.UtcNow);
        }

        update.ExecuteAffrows();

        return Task.CompletedTask;
    }

    /// <summary>
    /// 清除标注：删掉 <c>annotations</c> 这一行，图片退回 <c>pending</c>（已排除的保持排除），
    /// 版本号 +1，下游凭 rev 就能看出这次改动。
    /// </summary>
    public Task<bool> ClearAnnotationAsync(StorageLocation root, long imageId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ct.ThrowIfCancellationRequested();

        TinyLabelWorkspace.RequireDatabase(root);
        TinyLabelDatabase database = TinyLabelDatabase.Current;

        ImageEntity? image = database.Orm.Select<ImageEntity>().Where(row => row.Id == imageId).First();

        if (image is null)
        {
            return Task.FromResult(false);
        }

        string next = string.Equals(image.State, "excluded", StringComparison.OrdinalIgnoreCase)
            ? "excluded"
            : "pending";

        database.Orm.Delete<AnnotationEntity>().Where(row => row.ImageId == imageId).ExecuteAffrows();

        database.Orm.Update<ImageEntity>()
            .Where(row => row.Id == imageId)
            .Set(row => row.State, next)
            .Set(row => row.Revision, image.Revision + 1)
            .Set(row => row.UpdatedAt, DateTime.UtcNow)
            .ExecuteAffrows();

        logger?.LogInformation("清除图片 {Image} 的标注，状态回到 {State}", imageId, next);

        return Task.FromResult(true);
    }

    /// <summary>
    /// 删除图片：标注与图片行一起删；<c>copy</c>（导入）模式下连项目目录里的原图与缩略图一起清，
    /// <c>scan</c>（扫描）模式只删登记，原文件是别人目录里的，不动。
    /// </summary>
    public Task<bool> DeleteImageAsync(StorageLocation root, long imageId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ct.ThrowIfCancellationRequested();

        TinyLabelWorkspace.RequireDatabase(root);
        TinyLabelDatabase database = TinyLabelDatabase.Current;

        ImageEntity? image = database.Orm.Select<ImageEntity>().Where(row => row.Id == imageId).First();

        if (image is null)
        {
            return Task.FromResult(false);
        }

        ProjectEntity? project = database.Orm
            .Select<ProjectEntity>()
            .Where(row => row.Id == image.ProjectId)
            .First();

        database.Orm.Delete<AnnotationEntity>().Where(row => row.ImageId == imageId).ExecuteAffrows();
        database.Orm.Delete<ImageEntity>().Where(row => row.Id == imageId).ExecuteAffrows();

        if (project is not null && string.Equals(project.StorageMode, "copy", StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(image.FilePath);
            TryDelete(ThumbnailCache.PathOf(root, image.ProjectId, image.RelPath));
        }

        logger?.LogInformation("删除图片 {Image}（项目 {Project}）", imageId, image.ProjectId);

        return Task.FromResult(true);
    }

    /// <summary>删文件失败只记日志：库里已经删了，留个孤儿文件不影响使用。</summary>
    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(error, "删不掉文件 {Path}，只删了库里的登记", path);
        }
    }

    /// <summary>多选图片导入：复制进 <c>projects/{id}/</c>（<paramref name="copy"/>=false 时只登记原路径）。</summary>
    public async Task<TinyLabelImageImportReport> ImportFilesAsync(
        StorageLocation root,
        long projectId,
        IReadOnlyList<string> files,
        string? split = null,
        bool copy = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(files);

        return await ImportCoreAsync(
            root,
            projectId,
            files.Select(file => (Path: file, Relative: Path.GetFileName(file))),
            split,
            copy,
            ct).ConfigureAwait(false);
    }

    /// <summary>导入文件夹：保持子目录结构；<paramref name="copy"/>=false 即「扫描目录」。</summary>
    public async Task<TinyLabelImageImportReport> ImportFolderAsync(
        StorageLocation root,
        long projectId,
        string folder,
        string? split = null,
        bool copy = true,
        bool recursive = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException(Text.Format("Error.FolderNotFound", folder));
        }

        IEnumerable<string> files = Directory
            .EnumerateFiles(folder, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(ImageFiles.IsSupported)
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase);

        return await ImportCoreAsync(
            root,
            projectId,
            files.Select(file => (Path: file, Relative: Path.GetRelativePath(folder, file).Replace('\\', '/'))),
            split,
            copy,
            ct).ConfigureAwait(false);
    }

    private async Task<TinyLabelImageImportReport> ImportCoreAsync(
        StorageLocation root,
        long projectId,
        IEnumerable<(string Path, string Relative)> files,
        string? split,
        bool copy,
        CancellationToken ct)
    {
        TinyLabelWorkspace.RequireDatabase(root);
        TinyLabelDatabase database = TinyLabelDatabase.Current;

        ProjectEntity project = database.Orm.Select<ProjectEntity>().Where(row => row.Id == projectId).First()
            ?? throw new InvalidOperationException(Text.Format("Error.ProjectNotFound", projectId));

        string projectFolder = TinyLabelWorkspace.EnsureProjectDirectory(root, project.Id);
        string imageFolder = TinyLabelWorkspace.ImageDirectory(root, project.Id);

        // 已有图片：按内容哈希 + 相对路径去重
        HashSet<string> hashes = [.. database.Orm.Select<ImageEntity>()
            .Where(row => row.ProjectId == projectId)
            .ToList(row => row.Sha256)];

        HashSet<string> paths = [.. database.Orm.Select<ImageEntity>()
            .Where(row => row.ProjectId == projectId)
            .ToList(row => row.FilePath)];

        var warnings = new List<string>();
        var pending = new List<ImageEntity>();
        int skipped = 0;

        foreach ((string path, string relative) in files)
        {
            ct.ThrowIfCancellationRequested();

            if (!File.Exists(path))
            {
                skipped++;
                warnings.Add($"{relative}：文件不存在");
                continue;
            }

            await using FileStream stream = File.OpenRead(path);
            string sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            long size = stream.Length;

            if (!hashes.Add(sha))
            {
                skipped++;
                continue;
            }

            string targetPath = path;

            if (copy)
            {
                // 原图统一进 projects/{id}/images/，缩略图在旁边的 thumbs/（按需生成）
                string target = Path.Combine(imageFolder, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                if (File.Exists(target))
                {
                    skipped++;
                    warnings.Add($"{relative}：目标文件已存在");
                    continue;
                }

                File.Copy(path, target);
                targetPath = target;
            }

            if (!paths.Add(targetPath))
            {
                skipped++;
                continue;
            }

            int width = 0;
            int height = 0;

            try
            {
                ImageInfo? info = await infoSource
                    .ReadAsync(new ImageRef(relative, StorageLocation.FromPath(Path.GetDirectoryName(path)!)))
                    .ConfigureAwait(false);

                width = info?.Width ?? 0;
                height = info?.Height ?? 0;
            }
            catch (Exception error) when (error is IOException or InvalidDataException)
            {
                warnings.Add($"{relative}：读尺寸失败（{error.Message}）");
            }

            pending.Add(new ImageEntity
            {
                ProjectId = projectId,
                FileName = Path.GetFileName(relative),
                RelPath = relative,
                FilePath = targetPath,
                Width = width,
                Height = height,
                FileSizeBytes = size,
                Sha256 = sha,
                State = "pending",
                Revision = 0,
                Split = NormalizeSplit(split),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        if (pending.Count > 0)
        {
            database.Orm.Insert(pending).ExecuteAffrows();
        }

        if (copy && Directory.Exists(projectFolder))
        {
            database.Orm.Update<ProjectEntity>()
                .Where(row => row.Id == projectId)
                .Set(row => row.StorageMode, "copy")
                .ExecuteAffrows();
        }

        logger?.LogInformation(
            "项目 {Project} 导入图片：新增 {Imported}，跳过 {Skipped}（复制={Copy}）",
            projectId,
            pending.Count,
            skipped,
            copy);

        return new TinyLabelImageImportReport(pending.Count, skipped, warnings);
    }

    private static string? NormalizeSplit(string? split)
    {
        string? trimmed = split?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
