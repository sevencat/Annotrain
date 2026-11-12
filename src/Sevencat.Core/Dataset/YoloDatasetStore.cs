using System.Globalization;
using Sevencat.Core.Annotation;
using Sevencat.Core.Imaging;
using Sevencat.Core.Localization;
using Sevencat.Core.Model;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Dataset;

/// <summary>YOLO 布局的数据集：data.yaml 决定类别与 split，图片在 images/，标签在 labels/。</summary>
public sealed class YoloDatasetStore : IDatasetStore
{
    private readonly IBlobStore _store;
    private readonly IImageInfoSource _imageInfo;
    private readonly YoloDataConfig _config;
    private readonly string _basePrefix;

    internal YoloDatasetStore(
        IBlobStore store,
        IImageInfoSource imageInfo,
        StorageLocation location,
        string basePrefix,
        YoloDataConfig config)
    {
        _store = store;
        _imageInfo = imageInfo;
        _config = config;
        _basePrefix = basePrefix.Trim('/');
        Root = location;

        Classes = ClassCatalog.FromNames(config.Names);

        var splits = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.Train))
        {
            splits.Add("train");
        }

        if (!string.IsNullOrWhiteSpace(config.Val))
        {
            splits.Add("val");
        }

        if (!string.IsNullOrWhiteSpace(config.Test))
        {
            splits.Add("test");
        }

        Splits = splits.Count > 0 ? splits : ["train"];
    }

    public string Id => "yolo";

    public DatasetFormat Format => DatasetFormat.Yolo;

    public StorageLocation Root { get; }

    public StorageLocation ImageLocation => Root;

    public ClassCatalog Classes { get; }

    public IReadOnlyList<string> Splits { get; }

    public IDatasetSplit OpenSplit(string split)
    {
        string configured = split switch
        {
            "val" => _config.Val ?? "images/val",
            "test" => _config.Test ?? "images/test",
            _ => _config.Train ?? "images/train",
        };

        return new YoloSplit(this, split, Combine(configured));
    }

    internal IBlobStore Store => _store;

    internal IImageInfoSource ImageInfo => _imageInfo;

    internal string Combine(string relative)
    {
        string value = relative.Replace('\\', '/').Trim('/');
        return _basePrefix.Length == 0 ? value : $"{_basePrefix}/{value}";
    }

    /// <summary>
    /// images 路径 → labels 路径。老数据集常把 train 写成 "images/train"，也有写成 "../images/train" 的，
    /// 这里统一按「最后一个 images 段替换为 labels」处理。
    /// </summary>
    internal static string ToLabelPrefix(string imagePath)
    {
        string normalized = imagePath.Replace('\\', '/').Trim('/');

        int index = normalized.LastIndexOf("images/", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return $"labels/{normalized}";
        }

        return $"labels/{normalized[(index + "images/".Length)..]}";
    }

    private sealed class YoloSplit(YoloDatasetStore owner, string name, string imagePrefix) : IDatasetSplit
    {
        public string Name => name;

        public async Task<IReadOnlyList<DatasetSample>> LoadSamplesAsync(CancellationToken ct = default)
        {
            var samples = new List<DatasetSample>();
            string labelPrefix = YoloDatasetStore.ToLabelPrefix(imagePrefix);

            await foreach (BlobEntry entry in owner.Store
                               .ListAsync(new StorageKey(owner.Root, imagePrefix), ct)
                               .ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();

                if (!ImageFiles.IsSupported(entry.RelativePath))
                {
                    continue;
                }

                var imageRef = new ImageRef(entry.RelativePath, owner.Root);
                ImageInfo? info = await owner.ImageInfo.ReadAsync(imageRef, ct).ConfigureAwait(false);
                if (info is null)
                {
                    continue;
                }

                string labelRelative = $"{labelPrefix}/{Path.GetFileNameWithoutExtension(entry.RelativePath)}.txt";
                IReadOnlyList<BoxAnnotation> boxes = await ReadLabelsAsync(
                        owner.Store, new StorageKey(owner.Root, labelRelative), info, ct)
                    .ConfigureAwait(false);

                samples.Add(new DatasetSample(imageRef, info.Width, info.Height, boxes));
            }

            return samples;
        }

        private static async Task<IReadOnlyList<BoxAnnotation>> ReadLabelsAsync(
            IBlobStore store,
            StorageKey key,
            ImageInfo info,
            CancellationToken ct)
        {
            if (!await store.ExistsAsync(key, ct).ConfigureAwait(false))
            {
                return [];
            }

            await using Stream stream = await store.OpenReadAsync(key, ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            var boxes = new List<BoxAnnotation>();

            while (reader.ReadLine() is { } line)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#')
                {
                    continue;
                }

                string[] parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 5
                    || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int classId)
                    || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double cx)
                    || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double cy)
                    || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double width)
                    || !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double height))
                {
                    continue;
                }

                RectD rect = BoxConvert.Clamp(
                    BoxConvert.FromYolo(cx, cy, width, height, info.Width, info.Height),
                    info.Width,
                    info.Height);

                if (!rect.IsEmpty)
                {
                    boxes.Add(BoxAnnotation.FromRect(classId, rect));
                }
            }

            return boxes;
        }
    }
}

/// <summary>YOLO 形态的 Provider：探测命中 <see cref="DatasetFormat.Yolo"/> 时负责打开。</summary>
public sealed class YoloDatasetStoreProvider(BlobStoreRegistry registry, IImageInfoSource imageInfo) : IDatasetStoreProvider
{
    public string Id => "yolo";

    public DatasetFormat Format => DatasetFormat.Yolo;

    public DatasetStoreCapabilities Capabilities { get; } = new(CanWrite: true, HasSplits: true);

    public bool CanOpen(DatasetProbe probe)
        => probe.Format is DatasetFormat.Yolo or DatasetFormat.Ambiguous;

    public async Task<IDatasetStore> OpenAsync(DatasetProbe probe, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (string.IsNullOrWhiteSpace(probe.DescriptorPath))
        {
            throw new InvalidOperationException(Text.Get("Error.YoloDescriptorMissing"));
        }

        IBlobStore store = registry.Get(probe.Root);
        var key = new StorageKey(probe.Root, probe.DescriptorPath);

        await using Stream stream = await store.OpenReadAsync(key, ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        string yaml = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        YoloDataConfig config = YoloDataConfig.Parse(yaml);
        if (config.Names.Count == 0)
        {
            throw new InvalidDataException(Text.Format("Error.YoloNamesMissing", probe.DescriptorPath));
        }

        string basePrefix = Path.GetDirectoryName(probe.DescriptorPath)?.Replace('\\', '/') ?? string.Empty;

        return new YoloDatasetStore(store, imageInfo, probe.Root, basePrefix, config);
    }
}
