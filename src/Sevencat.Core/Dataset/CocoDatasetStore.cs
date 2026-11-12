using Sevencat.Core.Annotation;
using Sevencat.Core.Localization;
using Sevencat.Core.Model;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Dataset;

/// <summary>
/// COCO / Roboflow 形态：一个 json 描述一个 split 的全部图片与框（RF-DETR 的输入）。
/// 图片位置 = json 所在目录 + file_name。
/// </summary>
public sealed class CocoDatasetStore : IDatasetStore
{
    private readonly DatasetAnnotations _annotations;
    private readonly IBlobStore _store;

    internal CocoDatasetStore(
        IBlobStore store,
        StorageLocation location,
        string splitName,
        string imagePrefix,
        DatasetAnnotations annotations)
    {
        _store = store;
        _annotations = annotations;
        Root = location;
        SplitName = splitName;
        ImagePrefix = imagePrefix.Trim('/');
        Splits = [splitName];
    }

    public string Id => "coco";

    public DatasetFormat Format => DatasetFormat.Coco;

    public StorageLocation Root { get; }

    public StorageLocation ImageLocation => Root;

    public ClassCatalog Classes => _annotations.Classes;

    public IReadOnlyList<string> Splits { get; }

    public string SplitName { get; }

    public string ImagePrefix { get; }

    public IReadOnlyList<string> Warnings => _annotations.Warnings;

    public IDatasetSplit OpenSplit(string split) => new CocoSplit(this);

    internal async Task<IReadOnlyList<DatasetSample>> LoadAsync(CancellationToken ct)
    {
        var samples = new List<DatasetSample>(_annotations.Images.Count);

        foreach ((string relative, AnnotationDocument document) in _annotations.Images)
        {
            ct.ThrowIfCancellationRequested();

            string imagePath = ImagePrefix.Length == 0 ? relative : $"{ImagePrefix}/{relative}";
            var boxes = new List<BoxAnnotation>(document.Shapes.Count);

            foreach (Shape shape in document.Shapes)
            {
                if (!shape.IsClosed)
                {
                    continue;
                }

                int classId = _annotations.Classes.IndexOf(shape.Label);
                if (classId < 0)
                {
                    continue;
                }

                RectD rect = BoxConvert.Clamp(shape.Bounds, document.ImageWidth, document.ImageHeight);
                if (!rect.IsEmpty)
                {
                    boxes.Add(BoxAnnotation.FromRect(classId, rect));
                }
            }

            samples.Add(new DatasetSample(
                new ImageRef(imagePath, Root),
                document.ImageWidth,
                document.ImageHeight,
                boxes));
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return samples;
    }

    private sealed class CocoSplit(CocoDatasetStore owner) : IDatasetSplit
    {
        public string Name => owner.SplitName;

        public Task<IReadOnlyList<DatasetSample>> LoadSamplesAsync(CancellationToken ct = default)
            => owner.LoadAsync(ct);
    }
}

/// <summary>COCO 形态的 Provider。</summary>
public sealed class CocoDatasetStoreProvider(BlobStoreRegistry registry) : IDatasetStoreProvider
{
    public string Id => "coco";

    public DatasetFormat Format => DatasetFormat.Coco;

    public DatasetStoreCapabilities Capabilities { get; } = new(CanWrite: true, HasSplits: false);

    public bool CanOpen(DatasetProbe probe)
        => probe.Format is DatasetFormat.Coco or DatasetFormat.Ambiguous;

    public async Task<IDatasetStore> OpenAsync(DatasetProbe probe, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (string.IsNullOrWhiteSpace(probe.DescriptorPath))
        {
            throw new InvalidOperationException(Text.Get("Error.CocoDescriptorMissing"));
        }

        IBlobStore store = registry.Get(probe.Root);
        var key = new StorageKey(probe.Root, probe.DescriptorPath);

        string json;
        await using (Stream stream = await store.OpenReadAsync(key, ct).ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream);
            json = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        DatasetAnnotations annotations = new CocoDatasetCodec().Read(json);

        string prefix = Path.GetDirectoryName(probe.DescriptorPath)?.Replace('\\', '/') ?? string.Empty;
        string splitName = string.IsNullOrWhiteSpace(prefix)
            ? "train"
            : new DirectoryInfo(prefix).Name;

        return new CocoDatasetStore(store, probe.Root, splitName, prefix, annotations);
    }
}
