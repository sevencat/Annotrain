using Microsoft.Extensions.Logging;
using Sevencat.Core.Annotation;
using Sevencat.Core.Imaging;
using Sevencat.Core.Model;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Dataset;

/// <summary>
/// 平铺形态：图片与同名标注（labelme json / YOLO txt …）放在同一目录，没有 split 概念。
/// 类别表没有独立来源，按标注里首次出现的顺序累积，最后一次加载后可通过 <see cref="Classes"/> 取到。
/// </summary>
public sealed class FlatDatasetStore : IDatasetStore
{
    /// <summary>平铺形态只有一个逻辑 split。</summary>
    public const string OnlySplit = "all";

    private readonly IBlobStore _store;
    private readonly IImageInfoSource _imageInfo;
    private readonly IReadOnlyList<IAnnotationCodec> _codecs;
    private readonly ILogger? _logger;
    private ClassCatalog _classes;

    internal FlatDatasetStore(
        IBlobStore store,
        IImageInfoSource imageInfo,
        StorageLocation location,
        IReadOnlyList<IAnnotationCodec> codecs,
        ClassCatalog? classes = null,
        ILogger? logger = null)
    {
        _store = store;
        _imageInfo = imageInfo;
        _codecs = codecs;
        _logger = logger;
        _classes = classes ?? ClassCatalog.Empty;
        Root = location;
    }

    public string Id => "flat";

    public DatasetFormat Format => DatasetFormat.Flat;

    public StorageLocation Root { get; }

    public StorageLocation ImageLocation => Root;

    public ClassCatalog Classes => _classes;

    public IReadOnlyList<string> Splits => [OnlySplit];

    public IDatasetSplit OpenSplit(string split) => new FlatSplit(this, split);

    internal async Task<IReadOnlyList<DatasetSample>> LoadAsync(CancellationToken ct)
    {
        var files = new List<string>();

        await foreach (BlobEntry entry in _store
                           .ListAsync(new StorageKey(Root, string.Empty), ct)
                           .ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            files.Add(entry.RelativePath);
        }

        // 目录 + 文件名 → 标注文件，避免每张图再扫一遍列表
        var labels = new Dictionary<string, (string Path, IAnnotationCodec Codec)>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            IAnnotationCodec? codec = FindCodec(file);
            if (codec is not null)
            {
                labels[StemKey(file)] = (file, codec);
            }
        }

        var pending = new List<(ImageRef Image, ImageInfo Info, (string Label, RectD Rect)[] Shapes)>();
        ClassCatalog classes = _classes;

        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();

            if (!ImageFiles.IsSupported(file) || !labels.TryGetValue(StemKey(file), out (string Path, IAnnotationCodec Codec) label))
            {
                continue;
            }

            var image = new ImageRef(file, Root);
            ImageInfo? info = await _imageInfo.ReadAsync(image, ct).ConfigureAwait(false);
            if (info is null)
            {
                _logger?.LogDebug("平铺数据集跳过无法读取的图片：{Image}", file);
                continue;
            }

            (string Label, RectD Rect)[] shapes = await ReadShapesAsync(label, info, classes, ct).ConfigureAwait(false);
            pending.Add((image, info, shapes));

            classes = classes.WithMissing(shapes.Select(shape => shape.Label)).Catalog;
        }

        _classes = classes;

        var samples = new List<DatasetSample>(pending.Count);

        foreach ((ImageRef image, ImageInfo info, (string Label, RectD Rect)[] shapes) in pending)
        {
            var boxes = new List<BoxAnnotation>(shapes.Length);

            foreach ((string label, RectD rect) in shapes)
            {
                int classId = _classes.IndexOf(label);
                if (classId < 0)
                {
                    continue;
                }

                RectD clamped = BoxConvert.Clamp(rect, info.Width, info.Height);
                if (!clamped.IsEmpty)
                {
                    boxes.Add(BoxAnnotation.FromRect(classId, clamped));
                }
            }

            samples.Add(new DatasetSample(image, info.Width, info.Height, boxes));
        }

        _logger?.LogInformation(
            "平铺数据集加载完成：{Samples} 张图，{Classes} 个类别",
            samples.Count,
            _classes.Count);

        return samples;
    }

    private async Task<(string Label, RectD Rect)[]> ReadShapesAsync(
        (string Path, IAnnotationCodec Codec) label,
        ImageInfo info,
        ClassCatalog classes,
        CancellationToken ct)
    {
        await using Stream stream = await _store
            .OpenReadAsync(new StorageKey(Root, label.Path), ct)
            .ConfigureAwait(false);

        AnnotationParseResult parsed = label.Codec.Read(stream, new AnnotationReadContext(info, classes));

        return [.. parsed.Document.Shapes
            .Where(shape => shape.IsClosed)
            .Select(shape => (shape.Label, shape.Bounds))];
    }

    private IAnnotationCodec? FindCodec(string path)
    {
        string extension = Path.GetExtension(path);

        foreach (IAnnotationCodec codec in _codecs)
        {
            if (codec.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return codec;
            }
        }

        return null;
    }

    private static string StemKey(string path)
    {
        string normalized = path.Replace('\\', '/');
        string directory = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? string.Empty;
        return $"{directory}/{Path.GetFileNameWithoutExtension(normalized)}";
    }

    private sealed class FlatSplit(FlatDatasetStore owner, string name) : IDatasetSplit
    {
        public string Name => name;

        public Task<IReadOnlyList<DatasetSample>> LoadSamplesAsync(CancellationToken ct = default)
            => owner.LoadAsync(ct);
    }
}

/// <summary>平铺形态的 Provider：探测命中 <see cref="DatasetFormat.Flat"/> 时负责打开。</summary>
public sealed class FlatDatasetStoreProvider(
    BlobStoreRegistry registry,
    IImageInfoSource imageInfo,
    IEnumerable<IAnnotationCodec> codecs,
    ILoggerFactory? loggerFactory = null) : IDatasetStoreProvider
{
    public string Id => "flat";

    public DatasetFormat Format => DatasetFormat.Flat;

    public DatasetStoreCapabilities Capabilities { get; } = new(CanWrite: true, HasSplits: false);

    public bool CanOpen(DatasetProbe probe)
        => probe.Format is DatasetFormat.Flat or DatasetFormat.Ambiguous;

    public Task<IDatasetStore> OpenAsync(DatasetProbe probe, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(probe);

        IBlobStore store = registry.Get(probe.Root);
        IDatasetStore dataset = new FlatDatasetStore(
            store,
            imageInfo,
            probe.Root,
            [.. codecs],
            logger: loggerFactory?.CreateLogger<FlatDatasetStore>());

        return Task.FromResult(dataset);
    }
}
