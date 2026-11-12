using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Sevencat.Core.Model;

namespace Sevencat.Core.Simulation;

/// <summary>模拟数据落盘格式（TinyLabel 那种「写库」的走 <see cref="SyntheticLabelingSession"/>）。</summary>
public enum SyntheticExportFormat
{
    /// <summary>YOLO：<c>data.yaml</c> + <c>images/{train,val}</c> + <c>labels/{train,val}</c>。</summary>
    Yolo = 0,

    /// <summary>RF-DETR / Roboflow：<c>train|valid/&lt;图片&gt;</c> + <c>_annotations.coco.json</c>。</summary>
    RfDetr = 1,
}

/// <summary>落盘结果（界面与日志直接展示）。</summary>
public sealed record SyntheticWriteResult(
    SyntheticExportFormat Format,
    string DescriptorPath,
    int Images,
    int Boxes,
    IReadOnlyList<int> ObjectsPerClass,
    IReadOnlyDictionary<string, int> ImagesPerSplit,
    TimeSpan Elapsed);

/// <summary>
/// 把 <see cref="SyntheticImageGenerator"/> 画出来的图连同标注写成现成的数据集：
/// YOLO 目录或 RF-DETR（Roboflow COCO）目录。图片与标签出自同一份真值，不会对不上。
/// </summary>
public static class SyntheticDatasetWriter
{
    /// <summary>RF-DETR / Roboflow 约定的标注文件名。</summary>
    public const string RoboflowFileName = "_annotations.coco.json";

    /// <summary>生成并落盘；<paramref name="progress"/> 收到「已写 / 总数」。</summary>
    public static SyntheticWriteResult Write(
        SyntheticOptions generation,
        SyntheticExportFormat format,
        string outputDirectory,
        double valRatio = 0.25,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(generation);
        Validate(generation, outputDirectory);

        string root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);

        var watch = Stopwatch.StartNew();
        var perClass = new int[SyntheticImageGenerator.ClassNames.Count];
        var perSplit = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var coco = new Dictionary<string, List<CocoImage>>(StringComparer.OrdinalIgnoreCase);

        int total = generation.ImageCount;
        int done = 0;
        long annotationId = 1;

        string descriptor = format == SyntheticExportFormat.Yolo
            ? Path.Combine(root, "data.yaml")
            : Path.Combine(root, "train", RoboflowFileName);

        foreach (SyntheticImage image in new SyntheticImageGenerator(generation).Enumerate())
        {
            ct.ThrowIfCancellationRequested();

            string split = ResolveSplit(done, total, valRatio);
            perSplit[split] = perSplit.TryGetValue(split, out int seen) ? seen + 1 : 1;

            foreach (SyntheticShape shape in image.Shapes)
            {
                perClass[(int)shape.Kind]++;
            }

            if (format == SyntheticExportFormat.Yolo)
            {
                WriteYoloSample(root, split, image);
            }
            else
            {
                // RF-DETR 用 train / valid 两个目录，图片与 json 放在一起
                string folder = split == "val" ? "valid" : split;
                string directory = Path.Combine(root, folder);
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, image.FileName), image.Png);

                if (!coco.TryGetValue(folder, out List<CocoImage>? list))
                {
                    coco[folder] = list = [];
                }

                var annotations = new List<CocoAnnotation>(image.Shapes.Count);
                foreach (SyntheticShape shape in image.Shapes)
                {
                    annotations.Add(new CocoAnnotation(annotationId++, shape));
                }

                list.Add(new CocoImage(list.Count + 1, image.FileName, image.Width, image.Height, annotations));
            }

            done++;
            progress?.Report((done, total));
        }

        if (format == SyntheticExportFormat.RfDetr)
        {
            foreach ((string folder, List<CocoImage> list) in coco)
            {
                WriteCocoJson(Path.Combine(root, folder, RoboflowFileName), list);
            }
        }
        else
        {
            File.WriteAllText(descriptor, BuildDataYaml(hasVal: valRatio > 0), new UTF8Encoding(false));
        }

        watch.Stop();

        return new SyntheticWriteResult(
            format, descriptor, done, perClass.Sum(), perClass, perSplit, watch.Elapsed);
    }

    /// <summary>按比例交错分配 train/val，并保证两边都有图（除非只有一张）。与写库那条路径用同一套规则。</summary>
    public static string ResolveSplit(int index, int total, double valRatio)
    {
        if (total <= 1 || valRatio is <= 0 or >= 1)
        {
            return "train";
        }

        int step = Math.Max(2, (int)Math.Round(1 / valRatio));
        bool isVal = (index + 1) % step == 0;

        if (!isVal && index == total - 1 && (index + 1) < step)
        {
            isVal = true;
        }

        return isVal ? "val" : "train";
    }

    private static void WriteYoloSample(string root, string split, SyntheticImage image)
    {
        string images = Path.Combine(root, "images", split);
        string labels = Path.Combine(root, "labels", split);
        Directory.CreateDirectory(images);
        Directory.CreateDirectory(labels);

        File.WriteAllBytes(Path.Combine(images, image.FileName), image.Png);

        var lines = new string[image.Shapes.Count];
        for (int index = 0; index < image.Shapes.Count; index++)
        {
            lines[index] = FormatYoloLine(image.Shapes[index], image.Width, image.Height);
        }

        File.WriteAllLines(
            Path.Combine(labels, Path.ChangeExtension(image.FileName, ".txt")),
            lines,
            new UTF8Encoding(false));
    }

    /// <summary>YOLO 文本标签：<c>cls cx cy w h</c>（归一化，6 位小数）。</summary>
    private static string FormatYoloLine(SyntheticShape shape, int width, int height)
    {
        RectD box = shape.Bounds;
        double centerX = (box.X + (box.Width / 2)) / width;
        double centerY = (box.Y + (box.Height / 2)) / height;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)shape.Kind} {centerX:F6} {centerY:F6} {box.Width / width:F6} {box.Height / height:F6}");
    }

    private static string BuildDataYaml(bool hasVal)
    {
        var builder = new StringBuilder();
        builder.AppendLine("path: .");
        builder.AppendLine("train: images/train");
        builder.AppendLine(hasVal ? "val: images/val" : "val: images/train");
        builder.AppendLine("names:");

        for (int index = 0; index < SyntheticImageGenerator.ClassNames.Count; index++)
        {
            builder.AppendLine($"  {index}: {SyntheticImageGenerator.ClassNames[index]}");
        }

        return builder.ToString();
    }

    /// <summary>写 COCO 标注：bbox 是绝对像素 <c>[x, y, w, h]</c>，category_id 从 1 开始。</summary>
    private static void WriteCocoJson(string path, IReadOnlyList<CocoImage> images)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();

        writer.WritePropertyName("info");
        writer.WriteStartObject();
        writer.WriteString("description", "Annotrain synthetic shapes");
        writer.WriteString("version", "1.0");
        writer.WriteEndObject();

        writer.WritePropertyName("licenses");
        writer.WriteStartArray();
        writer.WriteEndArray();

        writer.WritePropertyName("images");
        writer.WriteStartArray();
        foreach (CocoImage image in images)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", image.Id);
            writer.WriteString("file_name", image.FileName);
            writer.WriteNumber("width", image.Width);
            writer.WriteNumber("height", image.Height);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WritePropertyName("annotations");
        writer.WriteStartArray();

        foreach (CocoImage image in images)
        {
            foreach (CocoAnnotation annotation in image.Annotations)
            {
                RectD box = annotation.Shape.Bounds;

                writer.WriteStartObject();
                writer.WriteNumber("id", annotation.Id);
                writer.WriteNumber("image_id", image.Id);
                writer.WriteNumber("category_id", (int)annotation.Shape.Kind + 1);
                writer.WriteNumber("iscrowd", 0);

                writer.WritePropertyName("bbox");
                writer.WriteStartArray();
                writer.WriteNumberValue(Math.Round(box.X, 2));
                writer.WriteNumberValue(Math.Round(box.Y, 2));
                writer.WriteNumberValue(Math.Round(Math.Max(1, box.Width), 2));
                writer.WriteNumberValue(Math.Round(Math.Max(1, box.Height), 2));
                writer.WriteEndArray();

                writer.WriteNumber("area", Math.Round(Math.Max(1, box.Width) * Math.Max(1, box.Height), 2));
                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();

        writer.WritePropertyName("categories");
        writer.WriteStartArray();
        for (int index = 0; index < SyntheticImageGenerator.ClassNames.Count; index++)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", index + 1);
            writer.WriteString("name", SyntheticImageGenerator.ClassNames[index]);
            writer.WriteString("supercategory", "shape");
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
    }

    private static void Validate(SyntheticOptions generation, string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        if (generation.ImageCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(generation), generation.ImageCount, "图片数量至少 1 张。");
        }

        if (generation.Width is < 32 or > 4096 || generation.Height is < 32 or > 4096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generation), $"{generation.Width}x{generation.Height}", "图片宽高必须在 [32,4096]。");
        }

        if (generation.MinShapes < 1 || generation.MaxShapes < generation.MinShapes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generation), generation.MaxShapes, "形状数量需满足 1 <= MinShapes <= MaxShapes。");
        }
    }

    private sealed record CocoImage(
        long Id,
        string FileName,
        int Width,
        int Height,
        IReadOnlyList<CocoAnnotation> Annotations);

    private sealed record CocoAnnotation(long Id, SyntheticShape Shape);
}
