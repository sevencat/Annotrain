using Sevencat.Train.Engine.Data.Yaml;

namespace Sevencat.Train.Engine.Data.Yolo;

/// <summary>
/// YOLO 数据集的 <c>data.yaml</c>：根目录、各 split 的图片目录、类别表。
/// <para>
/// 支持 TinyLabel / ultralytics 两种常见写法：
/// </para>
/// <code>
/// path: ../dataset
/// train: images/train
/// val: images/val
/// names:
///   0: person
///   1: car
/// </code>
/// <para>或 <c>names: [person, car]</c>。</para>
/// </summary>
public sealed class YoloDatasetConfig
{
    private YoloDatasetConfig(
        string dataYamlPath,
        string rootPath,
        string? train,
        string? val,
        string? test,
        int? nc,
        IReadOnlyDictionary<int, string> names)
    {
        DataYamlPath = dataYamlPath;
        RootPath = rootPath;
        Train = train;
        Val = val;
        Test = test;
        Nc = nc;
        Names = names;
    }

    /// <summary>data.yaml 的绝对路径。</summary>
    public string DataYamlPath { get; }

    /// <summary>数据集根目录（<c>path</c> 相对 data.yaml 解析后的绝对路径）。</summary>
    public string RootPath { get; }

    public string? Train { get; }

    public string? Val { get; }

    public string? Test { get; }

    /// <summary>类别数（未声明时等于 <see cref="Names"/> 数量）。</summary>
    public int? Nc { get; }

    /// <summary>类别索引 → 名称。</summary>
    public IReadOnlyDictionary<int, string> Names { get; }

    /// <summary>
    /// 用内存里的类别表构造配置（COCO 等外部格式转换后没有 data.yaml 时使用）。
    /// <para><see cref="Train"/> / <see cref="Val"/> 为 null，表示"由调用方直接给出样本"。</para>
    /// </summary>
    public static YoloDatasetConfig FromNames(
        IReadOnlyList<string> names, string descriptorPath = "", string rootPath = "")
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
            throw new ArgumentException("类别表不能为空。", nameof(names));

        var table = new Dictionary<int, string>(names.Count);
        for (int index = 0; index < names.Count; index++)
            table[index] = names[index];

        return new YoloDatasetConfig(
            string.IsNullOrEmpty(descriptorPath) ? string.Empty : Path.GetFullPath(descriptorPath),
            string.IsNullOrEmpty(rootPath) ? string.Empty : Path.GetFullPath(rootPath),
            train: null,
            val: null,
            test: null,
            nc: names.Count,
            table);
    }

    /// <summary>读取并校验 data.yaml。</summary>
    public static YoloDatasetConfig Load(string dataYamlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataYamlPath);

        string fullPath = Path.GetFullPath(dataYamlPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"data.yaml 不存在：{fullPath}", fullPath);

        IReadOnlyDictionary<string, object?> yaml = MiniYaml.ParseFile(fullPath);
        string baseDirectory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"无法确定 data.yaml 所在目录：{fullPath}");

        string rootPath = yaml.TryGetValue("path", out object? pathValue) && pathValue is string pathText
            && !string.IsNullOrWhiteSpace(pathText)
                ? ResolveDirectory(baseDirectory, pathText)
                : baseDirectory;

        IReadOnlyDictionary<int, string> names = ParseNames(yaml);

        int? nc = yaml.TryGetValue("nc", out object? ncValue)
            ? MiniYaml.AsInt(ncValue, "nc")
            : null;

        if (nc is { } declared && declared != names.Count)
        {
            throw new FormatException(
                $"data.yaml：nc={declared} 与 names 数量 {names.Count} 不一致。");
        }

        return new YoloDatasetConfig(
            fullPath,
            rootPath,
            ReadSplit(yaml, "train"),
            ReadSplit(yaml, "val"),
            ReadSplit(yaml, "test"),
            nc,
            names);
    }

    /// <summary>把 split 名（train / val / test）解析成绝对图片目录。</summary>
    public string ResolveSplitDirectory(string split)
    {
        string? relative = split.ToLowerInvariant() switch
        {
            "train" => Train,
            "val" or "valid" => Val,
            "test" => Test,
            _ => throw new ArgumentException($"未知 split '{split}'；可选 train / val / test。", nameof(split)),
        };

        if (string.IsNullOrWhiteSpace(relative))
            throw new InvalidOperationException($"data.yaml 没有声明 '{split}' 数据集。");

        return ResolveDirectory(RootPath, relative);
    }

    private static string ReadSplit(IReadOnlyDictionary<string, object?> yaml, string key)
    {
        if (!yaml.TryGetValue(key, out object? value) || value is null)
            return string.Empty;

        return value switch
        {
            string text => text,
            List<object?> list when list.Count > 0 && list[0] is string first => first,
            _ => throw new FormatException($"data.yaml：'{key}' 必须是路径字符串（或路径列表）。"),
        };
    }

    private static IReadOnlyDictionary<int, string> ParseNames(IReadOnlyDictionary<string, object?> yaml)
    {
        if (!yaml.TryGetValue("names", out object? value) || value is null)
            throw new FormatException("data.yaml：缺少 names（类别表）。");

        var names = new Dictionary<int, string>();

        switch (value)
        {
            case List<object?> list:
                for (int i = 0; i < list.Count; i++)
                    names[i] = list[i] as string
                        ?? throw new FormatException($"data.yaml：names[{i}] 不是字符串。");
                break;

            case Dictionary<string, object?> map:
                foreach ((string key, object? item) in map)
                {
                    if (!int.TryParse(key, out int index))
                        throw new FormatException($"data.yaml：names 的键 '{key}' 不是整数索引。");
                    names[index] = item as string
                        ?? throw new FormatException($"data.yaml：names['{key}'] 不是字符串。");
                }
                break;

            default:
                throw new FormatException("data.yaml：names 必须是列表或索引→名称的映射。");
        }

        if (names.Count == 0)
            throw new FormatException("data.yaml：names 为空。");

        for (int i = 0; i < names.Count; i++)
        {
            if (!names.ContainsKey(i))
                throw new FormatException($"data.yaml：names 缺少索引 {i}（类别索引必须从 0 连续）。");
        }

        return names;
    }

    private static string ResolveDirectory(string baseDirectory, string relative)
    {
        string combined = Path.IsPathRooted(relative)
            ? relative
            : Path.Combine(baseDirectory, relative);

        return Path.GetFullPath(combined);
    }
}

