using System.Diagnostics;
using System.Globalization;
using System.Text;
using Sevencat.Core.Annotation;
using Sevencat.Core.Localization;
using Sevencat.Core.Model;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Dataset.Export;

/// <summary>导出时的公共设施：计数、警告、图片拷贝、文本写入。</summary>
internal sealed class DatasetExportWriter(BlobStoreRegistry registry, DatasetExportOptions options)
{
    private const int MaxWarnings = 200;

    private readonly List<string> _warnings = [];

    public IBlobStore Destination { get; } = registry.Get(options.Destination);

    public int ImagesExported { get; set; }

    public int ImagesSkipped { get; set; }

    public int BoxesExported { get; set; }

    public int BoxesDropped { get; set; }

    public void Warn(string message)
    {
        if (_warnings.Count < MaxWarnings)
        {
            _warnings.Add(message);
        }
    }

    /// <summary>不许把结果写回源数据集：既可能覆盖原数据，也会让下次探测变成歧义。</summary>
    public static void EnsureDifferentRoot(IDatasetStore source, DatasetExportOptions exportOptions)
    {
        if (string.Equals(source.Root.Scheme, exportOptions.Destination.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                Path.GetFullPath(source.Root.Root).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(exportOptions.Destination.Root).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(Text.Get("Error.ExportTargetSameAsSource"));
        }
    }

    public async Task<bool> CopyImageAsync(DatasetSample sample, string destinationRelative, CancellationToken ct)
    {
        if (!options.CopyImages)
        {
            return true;
        }

        try
        {
            IBlobStore sourceStore = registry.Get(sample.Image.Location);
            var sourceKey = new StorageKey(sample.Image.Location, sample.Image.RelativePath);

            await using Stream input = await sourceStore.OpenReadAsync(sourceKey, ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, ct).ConfigureAwait(false);

            buffer.Position = 0;
            await Destination
                .WriteAsync(new StorageKey(options.Destination, destinationRelative), buffer, ct)
                .ConfigureAwait(false);

            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Warn($"图片拷贝失败：{sample.Image.RelativePath}（{error.Message}）");
            return false;
        }
    }

    public async Task WriteTextAsync(string relative, string content, CancellationToken ct)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        await Destination
            .WriteAsync(new StorageKey(options.Destination, relative), stream, ct)
            .ConfigureAwait(false);
    }

    public DatasetExportReport Build(
        DatasetFormat targetFormat,
        IReadOnlyList<string> splits,
        TimeSpan duration)
        => new(
            targetFormat,
            options.Destination,
            splits,
            ImagesExported,
            ImagesSkipped,
            BoxesExported,
            BoxesDropped,
            [.. _warnings],
            duration);

    public static string NormalizeRelative(string path) => path.Replace('\\', '/').TrimStart('/');
}

/// <summary>导出为 YOLO：<c>data.yaml</c> + <c>images/{split}</c> + <c>labels/{split}/*.txt</c>。</summary>
public sealed class YoloDatasetExporter(BlobStoreRegistry registry) : IDatasetExporter
{
    public DatasetFormat TargetFormat => DatasetFormat.Yolo;

    public async Task<DatasetExportReport> ExportAsync(
        IDatasetStore source,
        DatasetExportOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        var started = Stopwatch.StartNew();
        DatasetExportWriter.EnsureDifferentRoot(source, options);

        string[] splits = options.Splits is { Count: > 0 } ? [.. options.Splits] : [.. source.Splits];
        if (splits.Length == 0)
        {
            throw new InvalidOperationException(Text.Get("Error.ExportNoSplit"));
        }

        var writer = new DatasetExportWriter(registry, options);
        var exported = new List<string>(splits.Length);

        foreach (string split in splits)
        {
            string key = split.Trim().ToLowerInvariant();
            IReadOnlyList<DatasetSample> samples = await source
                .OpenSplit(key)
                .LoadSamplesAsync(ct)
                .ConfigureAwait(false);

            foreach (DatasetSample sample in samples)
            {
                ct.ThrowIfCancellationRequested();

                string fileName = Path.GetFileName(sample.Image.RelativePath);
                string stem = Path.GetFileNameWithoutExtension(fileName);

                if (!await writer.CopyImageAsync(sample, $"images/{key}/{fileName}", ct).ConfigureAwait(false))
                {
                    writer.ImagesSkipped++;
                    continue;
                }

                var builder = new StringBuilder();

                foreach (BoxAnnotation box in sample.Boxes)
                {
                    RectD rect = BoxConvert.Clamp(box.ToRect(), sample.Width, sample.Height);

                    if (rect.IsEmpty || box.ClassId < 0 || box.ClassId >= source.Classes.Count)
                    {
                        writer.BoxesDropped++;
                        writer.Warn($"丢弃越界框：{sample.Image.RelativePath} classId={box.ClassId}");
                        continue;
                    }

                    (double centerX, double centerY, double width, double height) =
                        BoxConvert.ToYolo(rect, sample.Width, sample.Height);

                    builder.Append(box.ClassId.ToString(CultureInfo.InvariantCulture)).Append(' ')
                        .Append(centerX.ToString("F6", CultureInfo.InvariantCulture)).Append(' ')
                        .Append(centerY.ToString("F6", CultureInfo.InvariantCulture)).Append(' ')
                        .Append(width.ToString("F6", CultureInfo.InvariantCulture)).Append(' ')
                        .Append(height.ToString("F6", CultureInfo.InvariantCulture))
                        .Append('\n');

                    writer.BoxesExported++;
                }

                await writer.WriteTextAsync($"labels/{key}/{stem}.txt", builder.ToString(), ct).ConfigureAwait(false);
                writer.ImagesExported++;
            }

            exported.Add(key);
        }

        await writer.WriteTextAsync("classes.txt", BuildClassList(source.Classes), ct).ConfigureAwait(false);
        await writer
            .WriteTextAsync("data.yaml", BuildYaml(source.Classes, exported, ResolveRootPath(options)), ct)
            .ConfigureAwait(false);

        return writer.Build(TargetFormat, exported, started.Elapsed);
    }

    /// <summary>与 TinyLabel 导出一致：<c>path</c> 写目标目录绝对路径，整个文件夹拷走也能就地训练。</summary>
    private static string ResolveRootPath(DatasetExportOptions options)
        => options.Destination.Scheme == StorageLocation.FileScheme
            ? Path.GetFullPath(options.Destination.Root).Replace('\\', '/')
            : options.Destination.Root;

    private static string BuildClassList(ClassCatalog classes)
    {
        var builder = new StringBuilder();
        for (int index = 0; index < classes.Count; index++)
        {
            builder.Append(classes.NameOf(index)).Append('\n');
        }

        return builder.ToString();
    }

    private static string BuildYaml(ClassCatalog classes, IReadOnlyList<string> splits, string rootPath)
    {
        var builder = new StringBuilder();
        builder.Append($"path: {rootPath}\n");

        string train = splits.Contains("train") ? "train" : splits[0];
        builder.Append($"train: images/{train}\n");

        if (splits.Contains("val"))
        {
            builder.Append("val: images/val\n");
        }

        if (splits.Contains("test"))
        {
            builder.Append("test: images/test\n");
        }

        builder.Append("names:\n");

        for (int index = 0; index < classes.Count; index++)
        {
            builder.Append($"  {index}: {Quote(classes.NameOf(index))}\n");
        }

        return builder.ToString();
    }

    private static string Quote(string name)
    {
        bool needsQuote = name.Length == 0
                          || name.Trim() != name
                          || name.Contains(':')
                          || name.Contains('#')
                          || name.Contains('"');

        return needsQuote ? $"\"{name.Replace("\"", "\\\"")}\"" : name;
    }
}

/// <summary>导出为 RF-DETR / COCO：每个 split 一个 <c>_annotations.coco.json</c>，图片与之同目录。</summary>
public sealed class CocoDatasetExporter(BlobStoreRegistry registry) : IDatasetExporter
{
    private readonly CocoDatasetCodec _codec = new();

    public DatasetFormat TargetFormat => DatasetFormat.Coco;

    public async Task<DatasetExportReport> ExportAsync(
        IDatasetStore source,
        DatasetExportOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        var started = Stopwatch.StartNew();
        DatasetExportWriter.EnsureDifferentRoot(source, options);

        string[] splits = options.Splits is { Count: > 0 } ? [.. options.Splits] : [.. source.Splits];
        if (splits.Length == 0)
        {
            throw new InvalidOperationException(Text.Get("Error.ExportNoSplit"));
        }

        var writer = new DatasetExportWriter(registry, options);
        var exported = new List<string>(splits.Length);
        long shapeId = 0;

        foreach (string split in splits)
        {
            string key = split.Trim().ToLowerInvariant();
            IReadOnlyList<DatasetSample> samples = await source
                .OpenSplit(key)
                .LoadSamplesAsync(ct)
                .ConfigureAwait(false);

            var annotations = new DatasetAnnotations
            {
                Classes = source.Classes,
                Images = new Dictionary<string, AnnotationDocument>(StringComparer.OrdinalIgnoreCase),
            };

            foreach (DatasetSample sample in samples)
            {
                ct.ThrowIfCancellationRequested();

                string fileName = Path.GetFileName(sample.Image.RelativePath);

                if (!await writer.CopyImageAsync(sample, $"{SplitDirectory(key)}/{fileName}", ct).ConfigureAwait(false))
                {
                    writer.ImagesSkipped++;
                    continue;
                }

                var shapes = new List<Shape>(sample.Boxes.Count);

                foreach (BoxAnnotation box in sample.Boxes)
                {
                    RectD rect = BoxConvert.Clamp(box.ToRect(), sample.Width, sample.Height);

                    if (rect.IsEmpty || box.ClassId < 0 || box.ClassId >= source.Classes.Count)
                    {
                        writer.BoxesDropped++;
                        writer.Warn($"丢弃越界框：{sample.Image.RelativePath} classId={box.ClassId}");
                        continue;
                    }

                    shapes.Add(new Shape
                    {
                        Id = (++shapeId).ToString(CultureInfo.InvariantCulture),
                        Type = ShapeType.Rectangle,
                        Points = BoxConvert.ToCorners(rect),
                        Label = source.Classes.NameOf(box.ClassId),
                    });

                    writer.BoxesExported++;
                }

                annotations.Images[fileName] = new AnnotationDocument
                {
                    ImagePath = fileName,
                    ImageWidth = sample.Width,
                    ImageHeight = sample.Height,
                    Shapes = shapes,
                };

                writer.ImagesExported++;
            }

            await writer
                .WriteTextAsync($"{SplitDirectory(key)}/_annotations.coco.json", _codec.Write(annotations), ct)
                .ConfigureAwait(false);

            exported.Add(key);
        }

        var classList = new StringBuilder();
        for (int index = 0; index < source.Classes.Count; index++)
        {
            classList.Append(source.Classes.NameOf(index)).Append('\n');
        }

        await writer.WriteTextAsync("classes.txt", classList.ToString(), ct).ConfigureAwait(false);

        return writer.Build(TargetFormat, exported, started.Elapsed);
    }

    /// <summary>RF-DETR 认 <c>train</c> / <c>valid</c>，所以 <c>val</c> 要落成 <c>valid</c> 目录。</summary>
    private static string SplitDirectory(string split) => split switch
    {
        "val" or "valid" => "valid",
        _ => split,
    };
}
