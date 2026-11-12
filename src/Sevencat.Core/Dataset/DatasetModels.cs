using Sevencat.Core.Annotation;
using Sevencat.Core.Localization;
using Sevencat.Core.Model;

namespace Sevencat.Core.Dataset;

/// <summary>数据集形态。「存储后端」是另一个维度（file / s3），不要混在一起。首期只支持这三种。</summary>
public enum DatasetFormat
{
    Unknown = 0,

    /// <summary>标准 YOLO：<c>data.yaml</c> + <c>images/{split}</c> + <c>labels/{split}</c>。</summary>
    Yolo = 1,

    /// <summary>标准 RF-DETR：COCO json（<c>train/_annotations.coco.json</c> 或 <c>annotations/*.json</c>）。</summary>
    Coco = 2,

    /// <summary>图片目录 + SQLite：图片是一堆文件，类别与标注都在库里。</summary>
    TinyLabelDb = 3,

    /// <summary>多个命中，需用户选。</summary>
    Ambiguous = 9,
}

public static class DatasetFormats
{
    public static string ToId(this DatasetFormat format) => format switch
    {
        DatasetFormat.Yolo => "yolo",
        DatasetFormat.Coco => "coco",
        DatasetFormat.TinyLabelDb => "tinylabel",
        DatasetFormat.Ambiguous => "ambiguous",
        _ => "unknown",
    };

    public static DatasetFormat ParseId(string? id) => id?.Trim().ToLowerInvariant() switch
    {
        "yolo" => DatasetFormat.Yolo,
        "coco" or "rfdetr" => DatasetFormat.Coco,
        "tinylabel" or "db" => DatasetFormat.TinyLabelDb,
        "ambiguous" => DatasetFormat.Ambiguous,
        _ => DatasetFormat.Unknown,
    };

    /// <summary>界面展示用（跟随当前语言）。</summary>
    public static string ToDisplayName(this DatasetFormat format) => format switch
    {
        DatasetFormat.Yolo => Text.Get("Dataset.Format.Yolo"),
        DatasetFormat.Coco => Text.Get("Dataset.Format.Coco"),
        DatasetFormat.TinyLabelDb => Text.Get("Dataset.Format.TinyLabelDb"),
        DatasetFormat.Ambiguous => Text.Get("Dataset.Format.Ambiguous"),
        _ => Text.Get("Dataset.Format.Unknown"),
    };
}

/// <summary>一个命中的候选形态及其入口文件。</summary>
/// <param name="Format">形态。</param>
/// <param name="DescriptorPath">该形态的入口文件（data.yaml / coco json / db）；不需要描述文件时为 <c>null</c>。</param>
/// <param name="Message">给人看的一句话说明。</param>
public sealed record DatasetCandidate(DatasetFormat Format, string? DescriptorPath, string Message);

/// <summary>探测结果。</summary>
/// <param name="Format">识别出的形态。</param>
/// <param name="Root">数据集根（描述文件所在目录）。</param>
/// <param name="DescriptorPath">可直接打开的入口：data.yaml / coco json / db 文件。</param>
/// <param name="Alternatives">同时命中的其它候选（歧义时给用户选）。</param>
/// <param name="Message">给人看的一句话说明；失败时写明期望结构。</param>
public sealed record DatasetProbe(
    DatasetFormat Format,
    Storage.StorageLocation Root,
    string? DescriptorPath,
    IReadOnlyList<string> Alternatives,
    string Message)
{
    public bool IsRecognized => Format is not (DatasetFormat.Unknown or DatasetFormat.Ambiguous);

    /// <summary>
    /// 命中的全部候选（含最终选中项）。歧义时用户选定形态后，据此取该形态自己的入口文件，
    /// 避免把 data.yaml 的路径交给 COCO Provider。
    /// </summary>
    public IReadOnlyList<DatasetCandidate> Candidates { get; init; } = [];

    /// <summary>按形态取候选；没有返回 <c>null</c>。</summary>
    public DatasetCandidate? CandidateOf(DatasetFormat format)
        => Candidates.FirstOrDefault(candidate => candidate.Format == format);
}

/// <summary>训练侧统一的样本模型：格式差异在各 Provider 内部消化。</summary>
public sealed record DatasetSample(
    ImageRef Image,
    int Width,
    int Height,
    IReadOnlyList<BoxAnnotation> Boxes)
{
    public bool IsEmpty => Boxes.Count == 0;
}

/// <summary>检测框：原图像素 xyxy + 类别下标。</summary>
public sealed record BoxAnnotation(int ClassId, float X1, float Y1, float X2, float Y2)
{
    public float Width => X2 - X1;

    public float Height => Y2 - Y1;

    public RectD ToRect() => RectD.FromLtrb(X1, Y1, X2, Y2);

    public static BoxAnnotation FromRect(int classId, RectD rect)
        => new(classId, (float)rect.X, (float)rect.Y, (float)rect.Right, (float)rect.Bottom);
}

/// <summary>某个 split 的只读样本集。</summary>
public interface IDatasetSplit
{
    string Name { get; }

    /// <summary>列举本身是 IO（远端是网络请求），一次加载成列表便于训练期反复遍历。</summary>
    Task<IReadOnlyList<DatasetSample>> LoadSamplesAsync(CancellationToken ct = default);
}

/// <summary>打开后的数据集：只描述结构，不读像素。</summary>
public interface IDatasetStore
{
    string Id { get; }

    DatasetFormat Format { get; }

    /// <summary>描述/索引所在位置。</summary>
    Storage.StorageLocation Root { get; }

    /// <summary>图片位置；DB 形态下可与 <see cref="Root"/> 分离。</summary>
    Storage.StorageLocation ImageLocation { get; }

    ClassCatalog Classes { get; }

    IReadOnlyList<string> Splits { get; }

    IDatasetSplit OpenSplit(string split);
}

/// <summary>一种形态的能力声明，界面据此决定能不能编辑/导出。</summary>
public sealed record DatasetStoreCapabilities(
    bool CanWrite,
    bool HasSplits,
    bool IsReadOnlyTemporary = false);

/// <summary>数据集形态提供者：能否处理 + 打开。</summary>
public interface IDatasetStoreProvider
{
    string Id { get; }

    DatasetFormat Format { get; }

    DatasetStoreCapabilities Capabilities { get; }

    bool CanOpen(DatasetProbe probe);

    Task<IDatasetStore> OpenAsync(DatasetProbe probe, CancellationToken ct = default);

    /// <summary>
    /// 需要额外指定标识的形态覆盖它：<c>tinylabel</c> 一个库里可能有多个项目，靠 <paramref name="datasetId"/> 选。
    /// 其它形态忽略该参数。
    /// </summary>
    Task<IDatasetStore> OpenAsync(DatasetProbe probe, string? datasetId, CancellationToken ct = default)
        => OpenAsync(probe, ct);
}

/// <summary>格式探测：只读文件名与文件头，不做全量解析。</summary>
public interface IDatasetProbe
{
    Task<DatasetProbe> DetectAsync(Storage.StorageLocation location, CancellationToken ct = default);
}
