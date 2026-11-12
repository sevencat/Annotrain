using System.Text;
using Microsoft.Extensions.Logging;
using Sevencat.Core.Annotation;
using Sevencat.Core.Annotation.Formats;
using Sevencat.Core.Localization;
using Sevencat.Core.Model;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Dataset.TinyLabel;

/// <summary>库里有多个项目、又没指定项目时抛出：让界面先选项目。</summary>
public sealed class DatasetProjectSelectionException(IReadOnlyList<ProjectEntity> projects) : Exception(
    Text.Format(
        "Error.MultiProject",
        projects.Count,
        string.Join(Text.Get("Error.ListSeparator"), projects.Select(p => $"{p.Name}({p.Id})"))))
{
    public IReadOnlyList<ProjectEntity> Projects { get; } = projects;
}

/// <summary>
/// 「图片目录 + SQLite」形态：一个项目 = 一个数据集。
/// 图片在目录里（<see cref="ImageEntity.FilePath"/>），类别在 <c>project_classes</c>，标注在 <c>annotations.DocJson</c>。
/// </summary>
public sealed class TinyLabelDatasetStore : IDatasetStore, IDisposable
{
    private readonly TinyLabelDatabase _db;
    private readonly ProjectEntity _project;
    private readonly LabelmeCodec _codec = new();
    private readonly ILogger? _logger;

    /// <summary>库是本 store 自己开的（外部数据集）才由它关；复用容器里的当前库时不关。</summary>
    private readonly bool _ownsDatabase;

    internal TinyLabelDatasetStore(
        TinyLabelDatabase db,
        ProjectEntity project,
        StorageLocation root,
        ClassCatalog classes,
        bool ownsDatabase,
        ILogger? logger = null)
    {
        _db = db;
        _project = project;
        _ownsDatabase = ownsDatabase;
        _logger = logger;
        Root = root;
        Classes = classes;
        Splits = LoadSplits();
    }

    public string Id => "tinylabel";

    public DatasetFormat Format => DatasetFormat.TinyLabelDb;

    public StorageLocation Root { get; }

    public StorageLocation ImageLocation => Root;

    public ClassCatalog Classes { get; }

    public IReadOnlyList<string> Splits { get; }

    public long ProjectId => _project.Id;

    public string ProjectName => _project.Name;

    /// <summary>数据库文件绝对路径（标注侧据此打开 <see cref="DatabaseAnnotationStore"/>）。</summary>
    public string DatabasePath => _db.Path;

    public IDatasetSplit OpenSplit(string split) => new TinyLabelSplit(this, split);

    internal async Task<IReadOnlyList<DatasetSample>> LoadAsync(string split, CancellationToken ct)
    {
        string key = split.Trim().ToLowerInvariant();

        List<ImageEntity> images = _db.Orm.Select<ImageEntity>()
            .Where(image => image.ProjectId == _project.Id && image.Split == key)
            .OrderBy(image => image.RelPath)
            .ToList();

        Dictionary<long, string> docs = _db.Orm.Select<AnnotationEntity>()
            .Where(annotation => annotation.ProjectId == _project.Id)
            .ToList()
            .ToDictionary(annotation => annotation.ImageId, annotation => annotation.DocJson);

        var samples = new List<DatasetSample>(images.Count);

        foreach (ImageEntity image in images)
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<BoxAnnotation> boxes =
                docs.TryGetValue(image.Id, out string? json) && !string.IsNullOrWhiteSpace(json)
                    ? ReadBoxes(json, image)
                    : [];

            samples.Add(new DatasetSample(ResolveImage(image), image.Width, image.Height, boxes));
        }

        await Task.CompletedTask.ConfigureAwait(false);

        _logger?.LogInformation(
            "tinylabel 数据集加载完成：项目 {Project}，split {Split}，{Samples} 张图",
            _project.Name,
            split,
            samples.Count);

        return samples;
    }

    private IReadOnlyList<BoxAnnotation> ReadBoxes(string json, ImageEntity image)
    {
        var info = new ImageInfo
        {
            RelativePath = image.RelPath,
            Width = image.Width,
            Height = image.Height,
        };

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        AnnotationParseResult parsed = _codec.Read(stream, new AnnotationReadContext(info, Classes));

        if (parsed.Warnings.Count > 0)
        {
            _logger?.LogDebug("图片 {Image} 的标注有 {Count} 条警告：{Warnings}",
                image.RelPath, parsed.Warnings.Count, string.Join("；", parsed.Warnings));
        }

        var boxes = new List<BoxAnnotation>(parsed.Document.Shapes.Count);

        foreach (Shape shape in parsed.Document.Shapes)
        {
            if (!shape.IsClosed)
            {
                continue;
            }

            int classId = Classes.IndexOf(shape.Label);
            if (classId < 0)
            {
                continue;
            }

            RectD rect = BoxConvert.Clamp(shape.Bounds, image.Width, image.Height);
            if (!rect.IsEmpty)
            {
                boxes.Add(BoxAnnotation.FromRect(classId, rect));
            }
        }

        return boxes;
    }

    /// <summary>图片落在数据集根下就用相对路径，落在根外（扫描模式）就按文件所在目录建 location。</summary>
    private ImageRef ResolveImage(ImageEntity image)
    {
        string full = image.FilePath.Replace('\\', '/');

        if (Root.Scheme == StorageLocation.FileScheme)
        {
            string root = Root.Root.Replace('\\', '/').TrimEnd('/');

            if (full.StartsWith($"{root}/", StringComparison.OrdinalIgnoreCase))
            {
                return new ImageRef(full[(root.Length + 1)..], Root);
            }
        }

        string relative = image.RelPath.Replace('\\', '/').TrimStart('/');
        return new ImageRef(relative, Root);
    }

    private IReadOnlyList<string> LoadSplits()
    {
        List<string?> values = _db.Orm.Select<ImageEntity>()
            .Where(image => image.ProjectId == _project.Id && image.Split != null)
            .ToList(image => image.Split);

        return [.. values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)];
    }

    public void Dispose()
    {
        if (_ownsDatabase)
        {
            _db.Dispose();
        }
    }

    private sealed class TinyLabelSplit(TinyLabelDatasetStore owner, string name) : IDatasetSplit
    {
        public string Name => name;

        public Task<IReadOnlyList<DatasetSample>> LoadSamplesAsync(CancellationToken ct = default)
            => owner.LoadAsync(name, ct);
    }
}

/// <summary>「图片目录 + SQLite」形态的 Provider：探测命中 <see cref="DatasetFormat.TinyLabelDb"/> 时负责打开。</summary>
public sealed class TinyLabelDatasetStoreProvider(
    TinyLabelDatabase? current = null,
    ILoggerFactory? loggerFactory = null) : IDatasetStoreProvider
{
    public string Id => "tinylabel";

    public DatasetFormat Format => DatasetFormat.TinyLabelDb;

    public DatasetStoreCapabilities Capabilities { get; } = new(CanWrite: true, HasSplits: true);

    public bool CanOpen(DatasetProbe probe)
        => probe.Format is DatasetFormat.TinyLabelDb or DatasetFormat.Ambiguous;

    /// <summary>库里只有一个项目时直接打开；有多个会抛 <see cref="DatasetProjectSelectionException"/>。</summary>
    public Task<IDatasetStore> OpenAsync(DatasetProbe probe, CancellationToken ct = default)
        => OpenAsync(probe, datasetId: null, ct);

    /// <summary><paramref name="datasetId"/> 即项目 Id（long）；为空时要求库里只有一个项目。</summary>
    public Task<IDatasetStore> OpenAsync(DatasetProbe probe, string? datasetId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(probe);

        long? projectId = null;
        if (!string.IsNullOrWhiteSpace(datasetId))
        {
            if (!long.TryParse(datasetId, out long parsed))
            {
                throw new ArgumentException(Text.Format("Error.ProjectIdNotNumeric", datasetId), nameof(datasetId));
            }

            projectId = parsed;
        }

        string dbPath = ResolveDatabasePath(probe);

        // 就是当前工作库时直接用（store 关闭不会关掉它）；导入/导出只碰当前库，
        // 会走到 Open 的只有"打开外部数据集"那种场景（训练端选目录、造模拟数据）。
        bool owns = current is null
            || !string.Equals(dbPath, current.Path, StringComparison.OrdinalIgnoreCase);

        TinyLabelDatabase db = owns ? TinyLabelDatabase.Open(dbPath, createIfMissing: false) : current!;

        try
        {
            ProjectEntity project = SelectProject(db, projectId);
            ClassCatalog classes = TinyLabelQueries.LoadClasses(db.Orm, project.Id);

            IDatasetStore store = new TinyLabelDatasetStore(
                db,
                project,
                probe.Root,
                classes,
                owns,
                loggerFactory?.CreateLogger<TinyLabelDatasetStore>());

            return Task.FromResult(store);
        }
        catch
        {
            if (owns)
            {
                db.Dispose();
            }

            throw;
        }
    }

    private static string ResolveDatabasePath(DatasetProbe probe)
    {
        if (probe.Root.Scheme != StorageLocation.FileScheme)
        {
            throw new NotSupportedException(Text.Get("Error.DatabaseRequiresLocalFile"));
        }

        if (string.IsNullOrWhiteSpace(probe.DescriptorPath))
        {
            throw new InvalidOperationException(Text.Get("Error.DetectMissingDbPath"));
        }

        string relative = probe.DescriptorPath.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(probe.Root.Root, relative);
    }

    private static ProjectEntity SelectProject(TinyLabelDatabase db, long? projectId)
    {
        List<ProjectEntity> projects = db.Orm.Select<ProjectEntity>()
            .OrderBy(project => project.CreatedAt)
            .ToList();

        if (projects.Count == 0)
        {
            throw new InvalidDataException(Text.Format("Error.DatabaseNoProject", db.Path));
        }

        if (projectId is { } id)
        {
            return projects.FirstOrDefault(project => project.Id == id)
                   ?? throw new InvalidOperationException(Text.Format("Error.DatabaseNoProjectId", id));
        }

        return projects.Count == 1 ? projects[0] : throw new DatasetProjectSelectionException(projects);
    }

}
