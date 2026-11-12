using Sevencat.Train.Engine.Data.Yolo;

namespace Sevencat.Train.Engine.Data;

/// <summary>数据集格式。</summary>
public enum DatasetFormat
{
    /// <summary>未识别。</summary>
    Unknown = 0,

    /// <summary>YOLO：data.yaml + images/labels。</summary>
    Yolo = 1,

    /// <summary>COCO：annotations/*.json + images。</summary>
    Coco = 2,

    /// <summary>同时存在两种格式，需要用户选择。</summary>
    Ambiguous = 3,
}

/// <summary>数据集探测结果。</summary>
/// <param name="Format">识别出的格式。</param>
/// <param name="Descriptor">可直接喂给训练器的描述文件（YOLO 的 data.yaml；COCO 的 json）。</param>
/// <param name="Alternatives">同时存在的其它候选描述文件（歧义时给用户选）。</param>
/// <param name="Message">给人看的一句话说明；识别失败时写明期望的目录结构。</param>
public sealed record DatasetDetection(
    DatasetFormat Format,
    string? Descriptor,
    IReadOnlyList<string> Alternatives,
    string Message)
{
    /// <summary>是否识别成功（歧义也算成功，只是需要选一个）。</summary>
    public bool IsRecognized => Format is DatasetFormat.Yolo or DatasetFormat.Coco or DatasetFormat.Ambiguous;
}

/// <summary>
/// 数据集目录探测：用户扔进来一个目录，自动判断是 YOLO 还是 COCO。
/// <para>
/// 只读文件头/文件名，不解析全量标注 —— 界面要即时反馈，几万张图的 COCO 不该在这里卡住。
/// </para>
/// </summary>
public static class DatasetLocator
{
    private static readonly string[] YamlExtensions = [".yaml", ".yml"];
    private const int CocoProbeBytes = 8192;

    /// <summary>探测给定路径（目录或其内的描述文件）。</summary>
    /// <param name="path">数据集目录，或 data.yaml / COCO json 的路径。</param>
    public static DatasetDetection Detect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (File.Exists(path))
            return DetectDescriptorFile(path);

        if (!Directory.Exists(path))
            return new DatasetDetection(DatasetFormat.Unknown, null, [], $"路径不存在：{path}");

        var yolo = new List<string>();
        foreach (string file in Directory.EnumerateFiles(path))
        {
            if (YamlExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()) && IsYoloDescriptor(file))
                yolo.Add(file);
        }

        string? coco = FindCocoDescriptor(path);
        return Decide(yolo, coco);
    }

    private static DatasetDetection DetectDescriptorFile(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();

        if (YamlExtensions.Contains(extension))
        {
            return IsYoloDescriptor(path)
                ? new DatasetDetection(DatasetFormat.Yolo, path, [], $"YOLO 数据集：{Path.GetFileName(path)}")
                : new DatasetDetection(
                    DatasetFormat.Unknown, null, [],
                    $"{Path.GetFileName(path)} 不是有效的 YOLO 描述文件（需要 train / val / names）。");
        }

        if (extension == ".json" && LooksLikeCoco(path))
            return new DatasetDetection(DatasetFormat.Coco, path, [], $"COCO 标注：{Path.GetFileName(path)}");

        return new DatasetDetection(
            DatasetFormat.Unknown, null, [],
            $"无法识别的文件：{Path.GetFileName(path)}（支持 data.yaml 或 COCO json）。");
    }

    private static DatasetDetection Decide(List<string> yolo, string? coco)
    {
        if (yolo.Count > 0 && coco is not null)
        {
            var alternatives = yolo.Skip(1).Append(coco).ToArray();
            return new DatasetDetection(
                DatasetFormat.Ambiguous, yolo[0], alternatives,
                $"同时发现 YOLO（{Path.GetFileName(yolo[0])}）与 COCO（{Path.GetFileName(coco)}），默认用 YOLO。");
        }

        if (yolo.Count > 0)
        {
            return new DatasetDetection(
                DatasetFormat.Yolo, yolo[0], yolo.Skip(1).ToArray(),
                $"YOLO 数据集：{Path.GetFileName(yolo[0])}");
        }

        if (coco is not null)
            return new DatasetDetection(DatasetFormat.Coco, coco, [], $"COCO 数据集：{Path.GetFileName(coco)}");

        return new DatasetDetection(
            DatasetFormat.Unknown, null, [],
            "未识别到数据集。期望结构之一：\n"
            + "  YOLO: <dir>/data.yaml + images/{train,val} + labels/{train,val}\n"
            + "  COCO: <dir>/annotations/*.json + images/");
    }

    private static bool IsYoloDescriptor(string path)
    {
        try
        {
            YoloDatasetConfig config = YoloDatasetConfig.Load(path);
            return config.Names.Count > 0 && !string.IsNullOrWhiteSpace(config.Train);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or FormatException)
        {
            // 目录里常混着训练用的 config.yaml；解析不了就说明不是数据集描述文件。
            return false;
        }
    }

    private static string? FindCocoDescriptor(string directory)
    {
        var candidates = new List<string>(Directory.EnumerateFiles(directory, "*.json"));

        string annotations = Path.Combine(directory, "annotations");
        if (Directory.Exists(annotations))
            candidates.AddRange(Directory.EnumerateFiles(annotations, "*.json"));

        // RF-DETR / Roboflow 把 json 放进每个 split 子目录（train/_annotations.coco.json），
        // 所以再扫一层常见的 split 名。
        foreach (string split in new[] { "train", "valid", "val", "test" })
        {
            string splitDirectory = Path.Combine(directory, split);
            if (Directory.Exists(splitDirectory))
                candidates.AddRange(Directory.EnumerateFiles(splitDirectory, "*.json"));
        }

        // 优先挑名字里带 train 的 json（COCO 单文件布局会有 annotations/train.json 与 val.json）。
        foreach (string candidate in candidates
                     .OrderByDescending(path => Path.GetFileName(path).Contains("train", StringComparison.OrdinalIgnoreCase)))
        {
            if (LooksLikeCoco(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>只看文件头：COCO 标注必然同时含 "images" 与 "annotations" 两个键。</summary>
    private static bool LooksLikeCoco(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int length = (int)Math.Min(CocoProbeBytes, stream.Length);
            var buffer = new byte[length];

            int read = 0;
            while (read < length)
            {
                int chunk = stream.Read(buffer, read, length - read);
                if (chunk <= 0)
                    break;

                read += chunk;
            }

            string head = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            return head.Contains("\"images\"", StringComparison.Ordinal)
                && head.Contains("\"annotations\"", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }
}
