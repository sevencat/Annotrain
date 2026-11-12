using Sevencat.Core.Storage;

namespace Sevencat.Core.Dataset.Import;

/// <summary>
/// 导入参数：把现成的 YOLO / RF-DETR(COCO) 数据集整份搬进「图片目录 + SQLite」项目库。
/// 源只读，导入后得到的是一个可编辑的标注项目，原目录不受影响。
/// </summary>
/// <param name="Source">源数据集根（探测到 data.yaml / COCO json 的目录）。</param>
/// <param name="Destination">目标数据集根：<c>annotrain.db</c> 与 <c>projects/</c> 都放这里。</param>
/// <param name="ProjectName">项目名；为空时取源目录名。</param>
/// <param name="Format">强制指定源形态；为空时用探测结论。</param>
/// <param name="Splits">只导入这些 split（<c>train</c> / <c>val</c> / <c>test</c>）；为空表示全部。</param>
public sealed record DatasetImportOptions(
    StorageLocation Source,
    StorageLocation Destination,
    string? ProjectName = null,
    DatasetFormat? Format = null,
    IReadOnlyList<string>? Splits = null);

/// <summary>导入结果：计数、类别、split 与告警，界面直接展示。</summary>
public sealed record DatasetImportReport(
    DatasetFormat SourceFormat,
    StorageLocation Source,
    string DatabasePath,
    long ProjectId,
    IReadOnlyList<string> Splits,
    IReadOnlyDictionary<string, int> ImagesPerSplit,
    IReadOnlyList<string> Classes,
    int ImagesImported,
    int ImagesSkipped,
    int AnnotatedImages,
    int BoxesImported,
    int BoxesDropped,
    IReadOnlyList<string> Warnings,
    TimeSpan Duration)
{
    public bool HasWarnings => Warnings.Count > 0;

    public string Summary =>
        $"从 {SourceFormat.ToDisplayName()} 导入：{ImagesImported} 张图 / {BoxesImported} 个框" +
        (ImagesSkipped > 0 ? $"，跳过 {ImagesSkipped} 张" : string.Empty) +
        (BoxesDropped > 0 ? $"，丢弃 {BoxesDropped} 个框" : string.Empty) +
        $"，split {string.Join(" / ", Splits)}";
}
