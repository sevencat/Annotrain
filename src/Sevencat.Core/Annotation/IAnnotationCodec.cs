using Sevencat.Core.Model;

namespace Sevencat.Core.Annotation;

/// <summary>读标注时的上下文：图片尺寸与类别表，codec 不自己去读图。</summary>
public sealed record AnnotationReadContext(ImageInfo Image, ClassCatalog Classes);

/// <summary>写标注时的上下文：目标图片与类别表（YOLO 需要宽高做归一化）。</summary>
public sealed record AnnotationWriteContext(
    string ImageRelativePath,
    int ImageWidth,
    int ImageHeight,
    ClassCatalog Classes);

/// <summary>解析结果：文档 + 可读警告（越界坐标、未知类别、坏行等）。</summary>
public sealed record AnnotationParseResult(AnnotationDocument Document, IReadOnlyList<string> Warnings);

/// <summary>
/// 单图标注格式（一图一文件）：YOLO txt / LabelMe json / VOC xml（未实现）。
/// 只收发 Stream，不接触路径，天然支持本地与远端。
/// </summary>
public interface IAnnotationCodec
{
    /// <summary>稳定标识：<c>yolo</c> / <c>labelme</c> / <c>voc</c>。</summary>
    string Id { get; }

    IReadOnlyCollection<string> Extensions { get; }

    AnnotationParseResult Read(Stream stream, AnnotationReadContext context);

    void Write(Stream stream, AnnotationDocument document, AnnotationWriteContext context);
}

/// <summary>
/// 数据集级标注格式（一个 json 管全部图片）：COCO / Roboflow。
/// 与 <see cref="IAnnotationCodec"/> 不同，它需要知道整个数据集的图片清单。
/// </summary>
public interface IDatasetAnnotationCodec
{
    string Id { get; }

    DatasetAnnotations Read(string json);

    string Write(DatasetAnnotations annotations);
}

/// <summary>数据集级标注的内存形态：图片相对路径 → 标注文档。</summary>
public sealed class DatasetAnnotations
{
    public ClassCatalog Classes { get; init; } = ClassCatalog.Empty;

    public Dictionary<string, AnnotationDocument> Images { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Warnings { get; init; } = [];
}
