namespace Sevencat.Train.Engine.Data.Synthetic;

/// <summary>测试数据集的落盘格式。</summary>
public enum SyntheticDatasetFormat
{
    /// <summary>
    /// YOLO（默认，也是本仓库一直用的布局）：
    /// <c>data.yaml</c> + <c>images/{train,val}</c> + <c>labels/{train,val}/*.txt</c>。
    /// 训练页与 <c>yolo train</c> 直接可用。
    /// </summary>
    Yolo = 0,

    /// <summary>
    /// RF-DETR / Roboflow 的 COCO 布局：<c>train/_annotations.coco.json</c>、<c>valid/_annotations.coco.json</c>，
    /// 图片与 json 放在同一目录（RF-DETR 官方脚本就是找 <c>train/_annotations.coco.json</c>）。
    /// </summary>
    RfDetr = 1,

    /// <summary>
    /// COCO 单文件布局：<c>annotations/{train,val}.json</c> + <c>images/{train,val}</c>。
    /// 与 <c>DatasetLocator</c> 的「COCO：annotations/*.json + images」一致。
    /// </summary>
    Coco = 2,
}

/// <summary>生成参数。</summary>
public sealed record ShapesDatasetOptions
{
    /// <summary>输出根目录。</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>落盘格式（默认 YOLO / data.yaml）。</summary>
    public SyntheticDatasetFormat Format { get; init; } = SyntheticDatasetFormat.Yolo;

    /// <summary>训练图片数。</summary>
    public int TrainImages { get; init; } = 200;

    /// <summary>验证图片数（0 表示不生成验证集）。</summary>
    public int ValImages { get; init; } = 40;

    /// <summary>图片边长（正方形；建议 32 的倍数，可直接拿去训练）。</summary>
    public int ImageSize { get; init; } = 512;

    /// <summary>随机种子；同一个种子生成完全相同的像素与标签。</summary>
    public ulong Seed { get; init; } = 2026;

    /// <summary>每张图最少几个形状。</summary>
    public int MinShapes { get; init; } = 1;

    /// <summary>每张图最多几个形状。</summary>
    public int MaxShapes { get; init; } = 5;

    /// <summary>是否额外写一张 <c>preview.png</c>（4×4 缩略图，肉眼检查用）。</summary>
    public bool WritePreview { get; init; } = true;
}

/// <summary>生成结果摘要（界面 / 日志直接展示）。</summary>
public sealed record ShapesDatasetSummary(
    string DescriptorPath,
    SyntheticDatasetFormat Format,
    int TrainImages,
    int ValImages,
    int ImageSize,
    IReadOnlyList<int> ObjectsPerClass,
    string? PreviewPath,
    TimeSpan Elapsed)
{
    /// <summary>YOLO 格式时的 <c>data.yaml</c> 路径；其它格式是主 COCO json。历史字段，保留兼容。</summary>
    public string DataYamlPath => DescriptorPath;
}
