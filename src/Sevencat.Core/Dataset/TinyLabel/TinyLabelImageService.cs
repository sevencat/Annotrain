using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Sevencat.Core.Imaging;
using Sevencat.Core.Localization;
using Sevencat.Core.Model;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Dataset.TinyLabel;

/// <summary>图片清单的一行（状态 / 版本 / 备注都带出来，界面直接用）。</summary>
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
    string? Split)
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

        using TinyLabelDatabase database = Open(root, createIfMissing: false);

        List<ImageEntity> rows = database.Orm.Select<ImageEntity>()
            .Where(row => row.ProjectId == projectId)
            .OrderBy(row => row.Split)
            .OrderBy(row => row.FileName)
            .ToList();

        IEnumerable<ImageEntity> filtered = rows;

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            string needle = keyword.Trim();
            filtered = filtered.Where(row =>
                row.FileName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || row.RelPath.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(state))
        {
            filtered = filtered.Where(row => string.Equals(row.State, state, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(split))
        {
            filtered = filtered.Where(row => string.Equals(row.Split, split, StringComparison.OrdinalIgnoreCase));
        }

        List<ImageEntity> all = filtered.ToList();
        int total = all.Count;

        List<TinyLabelImageRow> page = all
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .Select(row => new TinyLabelImageRow(
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
                row.Split))
            .ToList();

        ct.ThrowIfCancellationRequested();

        return Task.FromResult(new TinyLabelImagePage(page, total, pageIndex, pageSize));
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

        using TinyLabelDatabase database = Open(root, createIfMissing: false);

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
        using TinyLabelDatabase database = Open(root, createIfMissing: false);

        ProjectEntity project = database.Orm.Select<ProjectEntity>().Where(row => row.Id == projectId).First()
            ?? throw new InvalidOperationException(Text.Format("Error.ProjectNotFound", projectId));

        string projectFolder = Path.Combine(root.Root, "projects", project.Id.ToString());

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
                string target = Path.Combine(projectFolder, relative.Replace('/', Path.DirectorySeparatorChar));
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

    private TinyLabelDatabase Open(StorageLocation root, bool createIfMissing)
    {
        if (!root.IsFile)
        {
            throw new NotSupportedException(Text.Get("Error.DatabaseRequiresLocalFolder"));
        }

        string databasePath = Path.Combine(root.Root, TinyLabelProjectCatalog.DefaultDatabaseFile);

        return TinyLabelDatabase.Open(databasePath, createIfMissing);
    }

    private static string? NormalizeSplit(string? split)
    {
        string? trimmed = split?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
