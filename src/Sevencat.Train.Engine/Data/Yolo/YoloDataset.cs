using System.Globalization;

namespace Sevencat.Train.Engine.Data.Yolo;

/// <summary>YOLO 标签行的任务类型（按字段数推断）。</summary>
public enum YoloTask
{
    /// <summary>检测：<c>class cx cy w h</c>（5 列）。</summary>
    Detect,

    /// <summary>分割：<c>class x1 y1 x2 y2 ...</c>（奇数且 ≥ 7 列）。</summary>
    Segment,

    /// <summary>旋转框：<c>class x1 y1 x2 y2 x3 y3 x4 y4</c>（9 列）。</summary>
    Obb,
}

/// <summary>一条标签：类别 + 归一化坐标（不含类别列本身）。</summary>
public sealed record YoloLabel(int ClassId, YoloTask Task, float[] Values);

/// <summary>一张图与它的标签文件。</summary>
public sealed record YoloSample(string ImagePath, string LabelPath, IReadOnlyList<YoloLabel> Labels);

/// <summary>
/// YOLO 数据集：按 <c>data.yaml</c> 找到图片目录与对应的 <c>labels</c> 目录，逐图解析标签。
/// <para>只做文件与标签解析，不解码图片像素。</para>
/// </summary>
public sealed class YoloDataset
{
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };

    private YoloDataset(
        YoloDatasetConfig config,
        string split,
        string imageDirectory,
        string labelDirectory,
        IReadOnlyList<YoloSample> samples,
        YoloTask task)
    {
        Config = config;
        Split = split;
        ImageDirectory = imageDirectory;
        LabelDirectory = labelDirectory;
        Samples = samples;
        Task = task;
    }

    public YoloDatasetConfig Config { get; }

    /// <summary>当前 split 名（train / val / test）。</summary>
    public string Split { get; }

    /// <summary>图片目录（绝对路径）。</summary>
    public string ImageDirectory { get; }

    /// <summary>标签目录（绝对路径）。</summary>
    public string LabelDirectory { get; }

    public IReadOnlyList<YoloSample> Samples { get; }

    /// <summary>数据集任务类型（取第一张非空标签推断）。</summary>
    public YoloTask Task { get; }

    /// <summary>
    /// 用内存里的样本构造数据集（COCO 等外部格式转换后使用）。
    /// <para>样本的 <see cref="YoloSample.LabelPath"/> 允许为空字符串（标签来自 json，没有 .txt）。</para>
    /// </summary>
    public static YoloDataset CreateInMemory(
        string split,
        IReadOnlyList<YoloSample> samples,
        IReadOnlyList<string> classNames,
        string imageDirectory,
        YoloTask task = YoloTask.Detect,
        string descriptorPath = "")
    {
        ArgumentNullException.ThrowIfNull(samples);

        YoloDatasetConfig config = YoloDatasetConfig.FromNames(classNames, descriptorPath, imageDirectory);
        return new YoloDataset(config, split, imageDirectory, imageDirectory, samples, task);
    }

    /// <summary>打开某个 split。</summary>
    public static YoloDataset Open(string dataYamlPath, string split = "train")
    {
        YoloDatasetConfig config = YoloDatasetConfig.Load(dataYamlPath);
        string imageDirectory = config.ResolveSplitDirectory(split);

        if (!Directory.Exists(imageDirectory))
            throw new DirectoryNotFoundException($"图片目录不存在：{imageDirectory}");

        string labelDirectory = ResolveLabelDirectory(imageDirectory);
        if (!Directory.Exists(labelDirectory))
            throw new DirectoryNotFoundException($"标签目录不存在：{labelDirectory}");

        var samples = new List<YoloSample>();
        YoloTask? task = null;

        foreach (string imagePath in Directory.EnumerateFiles(imageDirectory).OrderBy(path => path, StringComparer.Ordinal))
        {
            string extension = Path.GetExtension(imagePath).ToLowerInvariant();
            if (!ImageExtensions.Contains(extension))
                continue;

            string relative = Path.GetRelativePath(imageDirectory, imagePath);
            string labelPath = Path.Combine(labelDirectory, Path.ChangeExtension(relative, ".txt"));

            if (!File.Exists(labelPath))
                throw new FileNotFoundException($"图片缺少标签文件：{labelPath}", labelPath);

            IReadOnlyList<YoloLabel> labels = ParseLabelFile(labelPath, config.Names.Count);

            foreach (YoloLabel label in labels)
            {
                task ??= label.Task;
                if (label.Task != task)
                {
                    throw new FormatException(
                        $"{labelPath}：同一个数据集里混用了 {task} 与 {label.Task} 标签。");
                }
            }

            samples.Add(new YoloSample(imagePath, labelPath, labels));
        }

        if (samples.Count == 0)
            throw new InvalidDataException($"目录里没有图片：{imageDirectory}");

        return new YoloDataset(config, split, imageDirectory, labelDirectory, samples, task ?? YoloTask.Detect);
    }

    /// <summary>解析一个 YOLO 标签文件。</summary>
    public static IReadOnlyList<YoloLabel> ParseLabelFile(string path, int classCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (classCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(classCount), classCount, "类别数必须大于 0");

        string[] lines = File.ReadAllLines(path);
        var labels = new List<YoloLabel>();

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            string line = lines[lineIndex].Trim();
            if (line.Length == 0)
                continue;

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            YoloTask task = parts.Length switch
            {
                5 => YoloTask.Detect,
                9 => YoloTask.Obb,
                >= 7 when parts.Length % 2 == 1 => YoloTask.Segment,
                _ => throw new FormatException(
                    $"{path}:{lineIndex + 1}：字段数 {parts.Length} 无法识别（detect=5，obb=9，segment=奇数≥7）。"),
            };

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int classId))
                throw new FormatException($"{path}:{lineIndex + 1}：类别 '{parts[0]}' 不是整数。");

            if (classId < 0 || classId >= classCount)
            {
                throw new FormatException(
                    $"{path}:{lineIndex + 1}：类别 {classId} 超出 [0,{classCount})。");
            }

            var values = new float[parts.Length - 1];
            for (int i = 1; i < parts.Length; i++)
            {
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                {
                    throw new FormatException(
                        $"{path}:{lineIndex + 1}：第 {i + 1} 个字段 '{parts[i]}' 不是数字。");
                }

                if (value < -1e-3f || value > 1f + 1e-3f)
                {
                    throw new FormatException(
                        $"{path}:{lineIndex + 1}：坐标 {value} 不在归一化范围 [0,1]。");
                }

                values[i - 1] = value;
            }

            labels.Add(new YoloLabel(classId, task, values));
        }

        return labels;
    }

    /// <summary>把 <c>...\images\&lt;split&gt;</c> 映射到 <c>...\labels\&lt;split&gt;</c>。</summary>
    public static string ResolveLabelDirectory(string imageDirectory)
    {
        string full = Path.GetFullPath(imageDirectory);
        string[] parts = full.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        for (int i = parts.Length - 1; i >= 0; i--)
        {
            if (!string.Equals(parts[i], "images", StringComparison.OrdinalIgnoreCase))
                continue;

            parts[i] = "labels";
            return parts[0] + Path.DirectorySeparatorChar
                + string.Join(Path.DirectorySeparatorChar, parts[1..]);
        }

        string? parent = Path.GetDirectoryName(full);
        return Path.Combine(parent ?? full, "labels");
    }
}

