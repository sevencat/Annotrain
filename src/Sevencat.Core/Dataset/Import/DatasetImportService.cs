using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Sevencat.Core.Annotation;
using Sevencat.Core.Annotation.Formats;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Imaging;
using Sevencat.Core.Localization;
using Sevencat.Core.Model;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Dataset.Import;

/// <summary>
/// 把现成的 YOLO / RF-DETR(COCO) 数据集导入成标注项目：
/// 复制图片进 <c>projects/{项目Id}/{split}/</c>，标注写成 labelme JSON，train/val 落到 <c>images.Split</c>。
/// 只读源、只写目标库；源目录不会被改动。
/// </summary>
/// <remarks>
/// split 归一：<c>valid</c> / <c>validation</c> → <c>val</c>（RF-DETR 用 valid，TinyLabel 用 val，导出时会再转回去）。
/// 多个 split 的类别取并集，顺序按首次出现；类别下标在写入时按并集重新映射。
/// </remarks>
public sealed class DatasetImportService(
    IDatasetProbe probe,
    IEnumerable<IDatasetStoreProvider> providers,
    BlobStoreRegistry registry,
    IImageInfoSource imageInfo,
    ILogger<DatasetImportService>? logger = null)
{
    /// <summary>RF-DETR 的 split 目录名；同时用于识别 COCO 描述文件。</summary>
    private static readonly string[] SplitDirectories = ["train", "val", "valid", "test"];

    private static readonly string[] Palette =
    [
        "#3B82F6", "#22C55E", "#EF4444", "#F59E0B", "#8B5CF6",
        "#06B6D4", "#EC4899", "#84CC16", "#F97316", "#14B8A6",
    ];

    private const int MaxWarnings = 200;

    private readonly IDatasetStoreProvider[] _providers = [.. providers];

    /// <summary>只探测不导入：界面据此告诉用户源目录是什么形态。</summary>
    public Task<DatasetProbe> DetectAsync(StorageLocation source, CancellationToken ct = default)
        => probe.DetectAsync(source, ct);

    /// <summary>
    /// 导入。<paramref name="detected"/> 可传入已探测结果，避免重复扫描大目录。
    /// </summary>
    public async Task<DatasetImportReport> ImportAsync(
        DatasetImportOptions options,
        DatasetProbe? detected = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        EnsureLocalDestination(options.Destination);
        EnsureDifferentRoot(options);

        var started = Stopwatch.StartNew();
        var warnings = new List<string>();

        (DatasetFormat format, DatasetProbe effective) = await ResolveAsync(options, detected, ct).ConfigureAwait(false);
        List<ImportSplit> splits = await CollectAsync(format, effective, options.Splits, warnings, ct).ConfigureAwait(false);

        if (splits.Count == 0)
        {
            throw new InvalidOperationException(Text.Format("Error.SourceHasNoImages", options.Source));
        }

        ClassCatalog classes = MergeClasses(splits, warnings);

        Directory.CreateDirectory(options.Destination.Root);
        string databasePath = Path.Combine(options.Destination.Root, "annotrain.db");
        IBlobStore destination = registry.Get(options.Destination);

        using TinyLabelDatabase database = TinyLabelDatabase.Open(databasePath);

        long projectId = CreateProject(database, options, format, classes);
        string prefix = $"projects/{projectId.ToString(CultureInfo.InvariantCulture)}";

        int images = 0, skipped = 0, annotated = 0, boxes = 0, dropped = 0;
        var importedSplits = new List<string>(splits.Count);
        var perSplit = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (ImportSplit split in splits)
        {
            ct.ThrowIfCancellationRequested();
            importedSplits.Add(split.Name);
            perSplit[split.Name] = 0;

            foreach (DatasetSample sample in split.Samples)
            {
                ct.ThrowIfCancellationRequested();

                string fileName = Path.GetFileName(sample.Image.RelativePath);
                if (string.IsNullOrWhiteSpace(fileName) || !ImageFiles.IsSupported(fileName))
                {
                    skipped++;
                    Warn(warnings, $"跳过非图片文件：{sample.Image.RelativePath}");
                    continue;
                }

                (int width, int height) = await ResolveSizeAsync(sample, ct).ConfigureAwait(false);
                if (width <= 0 || height <= 0)
                {
                    skipped++;
                    Warn(warnings, $"跳过尺寸未知的图片：{sample.Image.RelativePath}");
                    continue;
                }

                byte[] payload;
                try
                {
                    payload = await ReadBytesAsync(sample.Image, ct).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or NotSupportedException)
                {
                    skipped++;
                    Warn(warnings, $"跳过读不到的图片：{sample.Image.RelativePath}（{error.Message}）");
                    continue;
                }

                string relative = $"{prefix}/{split.Name}/{fileName}";

                await destination
                    .WriteAsync(new StorageKey(options.Destination, relative), new MemoryStream(payload), ct)
                    .ConfigureAwait(false);

                string filePath = Path.GetFullPath(
                    Path.Combine(options.Destination.Root, relative.Replace('/', Path.DirectorySeparatorChar)));

                IReadOnlyList<Shape> shapes = BuildShapes(split, sample, classes, width, height, warnings, ref dropped);

                long imageId = (long)database.Orm.Insert(new ImageEntity
                {
                    ProjectId = projectId,
                    FileName = fileName,
                    RelPath = relative,
                    FilePath = filePath,
                    Width = width,
                    Height = height,
                    FileSizeBytes = payload.Length,
                    Sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
                    State = shapes.Count > 0 ? "labeled" : "pending",
                    Revision = shapes.Count > 0 ? 1 : 0,
                    Split = split.Name,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                }).ExecuteIdentity();

                images++;
                perSplit[split.Name]++;
                boxes += shapes.Count;

                if (shapes.Count == 0)
                {
                    continue;
                }

                annotated++;
                WriteAnnotation(database, projectId, imageId, relative, width, height, shapes);
            }
        }

        DatasetImportReport report = new(
            format,
            options.Source,
            databasePath,
            projectId,
            importedSplits,
            perSplit,
            classes.Names,
            images,
            skipped,
            annotated,
            boxes,
            dropped,
            [.. warnings],
            started.Elapsed);

        logger?.LogInformation(
            "数据集导入完成：{Summary}，项目 {ProjectId}，库 {Database}，耗时 {Duration}",
            report.Summary,
            projectId,
            databasePath,
            report.Duration);

        return report;
    }

    // ── 源解析 ────────────────────────────────────────────────────────────────

    private async Task<(DatasetFormat Format, DatasetProbe Probe)> ResolveAsync(
        DatasetImportOptions options,
        DatasetProbe? detected,
        CancellationToken ct)
    {
        DatasetProbe result = detected ?? await probe.DetectAsync(options.Source, ct).ConfigureAwait(false);
        DatasetFormat format = options.Format ?? result.Format;

        if (format == DatasetFormat.Ambiguous)
        {
            throw new DatasetFormatAmbiguousException(result);
        }

        if (format == DatasetFormat.Unknown)
        {
            throw new DatasetFormatNotSupportedException(format, result.Message);
        }

        if (format == DatasetFormat.TinyLabelDb)
        {
            throw new NotSupportedException(Text.Get("Error.SourceAlreadyImageDatabase"));
        }

        // 歧义或强制指定形态时，入口文件必须换成该形态自己的（data.yaml 不能交给 COCO Provider）
        DatasetCandidate? candidate = result.CandidateOf(format);
        if (candidate is null)
        {
            throw new DatasetFormatNotSupportedException(
                format,
                Text.Format("Error.DescriptorMissingForFormat", options.Source, result.Message));
        }

        return (format, result with { Format = format, DescriptorPath = candidate.DescriptorPath });
    }

    private async Task<List<ImportSplit>> CollectAsync(
        DatasetFormat format,
        DatasetProbe effective,
        IReadOnlyList<string>? only,
        List<string> warnings,
        CancellationToken ct)
    {
        return format switch
        {
            DatasetFormat.Yolo => await CollectYoloAsync(effective, only, warnings, ct).ConfigureAwait(false),
            DatasetFormat.Coco => await CollectCocoAsync(effective, only, warnings, ct).ConfigureAwait(false),
            _ => throw new DatasetFormatNotSupportedException(format),
        };
    }

    /// <summary>YOLO：data.yaml 已经给出全部 split，逐个 <see cref="IDatasetStore.OpenSplit"/> 装载。</summary>
    private async Task<List<ImportSplit>> CollectYoloAsync(
        DatasetProbe effective,
        IReadOnlyList<string>? only,
        List<string> warnings,
        CancellationToken ct)
    {
        IDatasetStore store = await FindProvider(DatasetFormat.Yolo).OpenAsync(effective, ct).ConfigureAwait(false);

        try
        {
            var result = new List<ImportSplit>(store.Splits.Count);

            foreach (string split in store.Splits)
            {
                string name = NormalizeSplit(split);
                if (!IsWanted(only, name) || result.Exists(item => item.Name == name))
                {
                    continue;
                }

                IReadOnlyList<DatasetSample> samples = await store
                    .OpenSplit(split)
                    .LoadSamplesAsync(ct)
                    .ConfigureAwait(false);

                if (samples.Count == 0)
                {
                    Warn(warnings, $"split {split} 没有图片，已跳过");
                    continue;
                }

                result.Add(new ImportSplit(name, store.Classes, samples));
            }

            return result;
        }
        finally
        {
            (store as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// RF-DETR：一个 json 一个 split。把源根下所有 COCO 描述文件各自打开一次
    /// （Provider 每次只认一个 json），再按 train → val → test 排序。
    /// </summary>
    private async Task<List<ImportSplit>> CollectCocoAsync(
        DatasetProbe effective,
        IReadOnlyList<string>? only,
        List<string> warnings,
        CancellationToken ct)
    {
        List<string> descriptors = await FindCocoDescriptorsAsync(effective.Root, ct).ConfigureAwait(false);

        // 兜底：目录扫描没命中时，用探测给出的那一个
        if (descriptors.Count == 0 && !string.IsNullOrWhiteSpace(effective.DescriptorPath))
        {
            descriptors.Add(effective.DescriptorPath);
        }

        var collected = new List<(int Order, ImportSplit Split)>(descriptors.Count);

        foreach (string descriptor in descriptors)
        {
            ct.ThrowIfCancellationRequested();

            DatasetProbe sub = effective with { Format = DatasetFormat.Coco, DescriptorPath = descriptor };

            IDatasetStore store;
            try
            {
                store = await FindProvider(DatasetFormat.Coco).OpenAsync(sub, ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is InvalidDataException or FormatException)
            {
                Warn(warnings, $"跳过无法解析的标注文件：{descriptor}（{error.Message}）");
                continue;
            }

            try
            {
                string raw = store.Splits.Count > 0 ? store.Splits[0] : "train";
                string name = NormalizeSplit(raw);

                if (!IsWanted(only, name) || collected.Exists(item => item.Split.Name == name))
                {
                    continue;
                }

                IReadOnlyList<DatasetSample> samples = await store
                    .OpenSplit(raw)
                    .LoadSamplesAsync(ct)
                    .ConfigureAwait(false);

                if (samples.Count == 0)
                {
                    Warn(warnings, $"{descriptor} 没有图片，已跳过");
                    continue;
                }

                collected.Add((SplitOrder(name), new ImportSplit(name, store.Classes, samples)));
            }
            finally
            {
                (store as IDisposable)?.Dispose();
            }
        }

        return [.. collected
            .OrderBy(item => item.Order)
            .Select(item => item.Split)];
    }

    /// <summary>列举形如 <c>train/_annotations.coco.json</c> / <c>annotations/instances_*.json</c> 的描述文件。</summary>
    private async Task<List<string>> FindCocoDescriptorsAsync(StorageLocation root, CancellationToken ct)
    {
        IBlobStore store = registry.Get(root);
        var files = new List<string>();

        await foreach (BlobEntry entry in store
                           .ListAsync(new StorageKey(root, string.Empty), ct)
                           .ConfigureAwait(false))
        {
            if (IsCocoDescriptor(entry.RelativePath))
            {
                files.Add(entry.RelativePath);
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static bool IsCocoDescriptor(string relative)
    {
        string normalized = relative.Replace('\\', '/').TrimStart('/');

        if (!normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string fileName = Path.GetFileName(normalized);

        if (fileName.Contains("annotations", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? directory = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        // {split}/instances_train.json 之类的 Roboflow 变体
        return SplitDirectories.Contains(directory, StringComparer.OrdinalIgnoreCase)
               && fileName.Contains("instances", StringComparison.OrdinalIgnoreCase);
    }

    // ── 写入 ──────────────────────────────────────────────────────────────────

    private static long CreateProject(
        TinyLabelDatabase database,
        DatasetImportOptions options,
        DatasetFormat format,
        ClassCatalog classes)
    {
        string name = string.IsNullOrWhiteSpace(options.ProjectName)
            ? DefaultProjectName(options.Source)
            : options.ProjectName.Trim();

        long projectId = (long)database.Orm.Insert(new ProjectEntity
        {
            Name = name,
            Description = $"从 {format.ToDisplayName()} 导入：{options.Source}",
            StorageMode = "import",
            CreatedBy = 0,
            CreatedAt = DateTime.UtcNow,
        }).ExecuteIdentity();

        for (int index = 0; index < classes.Count; index++)
        {
            database.Orm.Insert(new ProjectClassEntity
            {
                ProjectId = projectId,
                Name = classes.NameOf(index),
                OrderIndex = index,
                Color = Palette[index % Palette.Length],
            }).ExecuteAffrows();
        }

        return projectId;
    }

    private static string DefaultProjectName(StorageLocation source)
    {
        if (!source.IsFile)
        {
            return source.Root;
        }

        string name = Path.GetFileName(source.Root.TrimEnd(Path.DirectorySeparatorChar, '/'));
        return string.IsNullOrWhiteSpace(name) ? source.Root : name;
    }

    private static IReadOnlyList<Shape> BuildShapes(
        ImportSplit split,
        DatasetSample sample,
        ClassCatalog classes,
        int width,
        int height,
        List<string> warnings,
        ref int dropped)
    {
        if (sample.Boxes.Count == 0)
        {
            return [];
        }

        var shapes = new List<Shape>(sample.Boxes.Count);

        foreach (BoxAnnotation box in sample.Boxes)
        {
            string label = split.Classes.NameOf(box.ClassId);
            int classId = label.Length == 0 ? -1 : classes.IndexOf(label);

            if (classId < 0)
            {
                dropped++;
                Warn(warnings, $"丢弃未知类别的框：{sample.Image.RelativePath} classId={box.ClassId}");
                continue;
            }

            RectD rect = BoxConvert.Clamp(box.ToRect(), width, height);
            if (rect.IsEmpty)
            {
                dropped++;
                continue;
            }

            shapes.Add(new Shape
            {
                Id = IdGenerator.NewId(),
                Type = ShapeType.Rectangle,
                Points = BoxConvert.ToCorners(rect),
                Label = label,
            });
        }

        return shapes;
    }

    private static void WriteAnnotation(
        TinyLabelDatabase database,
        long projectId,
        long imageId,
        string relative,
        int width,
        int height,
        IReadOnlyList<Shape> shapes)
    {
        var document = new AnnotationDocument
        {
            ImagePath = relative,
            ImageWidth = width,
            ImageHeight = height,
            Shapes = shapes,
            Meta = new DocumentMeta
            {
                Id = IdGenerator.NewId(),
                Revision = 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
        };

        database.Orm.Insert(new AnnotationEntity
        {
            ImageId = imageId,
            ProjectId = projectId,
            Revision = 1,
            DocJson = LabelmeCodec.Serialize(document),
            UpdatedBy = 0,
            UpdatedAt = DateTime.UtcNow,
        }).ExecuteAffrows();
    }

    // ── 工具 ──────────────────────────────────────────────────────────────────

    private static ClassCatalog MergeClasses(IReadOnlyList<ImportSplit> splits, List<string> warnings)
    {
        ClassCatalog merged = ClassCatalog.Empty;

        for (int index = 0; index < splits.Count; index++)
        {
            ImportSplit split = splits[index];
            (ClassCatalog catalog, IReadOnlyList<string> added) = merged.WithMissing(split.Classes.Names);
            merged = catalog;

            if (index > 0 && added.Count > 0)
            {
                Warn(warnings, $"split {split.Name} 带来新类别：{string.Join("、", added)}");
            }
        }

        if (merged.Count == 0)
        {
            Warn(warnings, "源数据集没有类别，导入后所有框都会被丢弃");
        }

        return merged;
    }

    /// <summary>用图片头兜底：COCO json 里的宽高可能缺失或不准。</summary>
    private async Task<(int Width, int Height)> ResolveSizeAsync(DatasetSample sample, CancellationToken ct)
    {
        if (sample.Width > 0 && sample.Height > 0)
        {
            return (sample.Width, sample.Height);
        }

        try
        {
            ImageInfo? info = await imageInfo.ReadAsync(sample.Image, ct).ConfigureAwait(false);
            return info is null ? (0, 0) : (info.Width, info.Height);
        }
        catch (Exception error) when (error is IOException or NotSupportedException)
        {
            return (0, 0);
        }
    }

    private async Task<byte[]> ReadBytesAsync(ImageRef image, CancellationToken ct)
    {
        IBlobStore store = registry.Get(image.Location);

        await using Stream input = await store
            .OpenReadAsync(new StorageKey(image.Location, image.RelativePath), ct)
            .ConfigureAwait(false);

        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private IDatasetStoreProvider FindProvider(DatasetFormat format)
        => _providers.FirstOrDefault(item => item.Format == format)
           ?? throw new DatasetFormatNotSupportedException(format);

    private static void Warn(List<string> warnings, string message)
    {
        if (warnings.Count < MaxWarnings)
        {
            warnings.Add(message);
        }
    }

    private static bool IsWanted(IReadOnlyList<string>? only, string split)
        => only is not { Count: > 0 } || only.Contains(split, StringComparer.OrdinalIgnoreCase);

    /// <summary>RF-DETR 的 <c>valid</c> 归一成 TinyLabel 的 <c>val</c>；其它名字原样保留。</summary>
    private static string NormalizeSplit(string split) => split.Trim().ToLowerInvariant() switch
    {
        "valid" or "validation" or "val" => "val",
        "train" or "training" => "train",
        "test" or "testing" => "test",
        var other => other,
    };

    private static int SplitOrder(string split) => split switch
    {
        "train" => 0,
        "val" => 1,
        "test" => 2,
        _ => 3,
    };

    private static void EnsureLocalDestination(StorageLocation destination)
    {
        if (!destination.IsFile)
        {
            throw new NotSupportedException(Text.Get("Error.ImportTargetNotLocal"));
        }
    }

    private static void EnsureDifferentRoot(DatasetImportOptions options)
    {
        if (options.Source.IsFile
            && string.Equals(
                Path.GetFullPath(options.Source.Root).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(options.Destination.Root).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(Text.Get("Error.ImportTargetSameAsSource"));
        }
    }

    /// <summary>一个 split 的装载结果（类别表用于把 classId 映射到并集下标）。</summary>
    private sealed record ImportSplit(string Name, ClassCatalog Classes, IReadOnlyList<DatasetSample> Samples);
}
