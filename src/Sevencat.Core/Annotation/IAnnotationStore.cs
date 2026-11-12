using Sevencat.Core.Localization;
using Sevencat.Core.Model;

namespace Sevencat.Core.Annotation;

/// <summary>加载结果：文档 + 存储键 + 解析警告。</summary>
public sealed record AnnotationEnvelope(
    AnnotationDocument Document,
    string AnnotationPath,
    IReadOnlyList<string> Warnings)
{
    public int Revision => Document.Meta.Revision;
}

/// <summary>保存时版本不匹配。</summary>
public sealed class AnnotationConcurrencyException(int expected, int actual)
    : Exception(Text.Format("Error.ConcurrencyConflict", expected, actual))
{
    public int Expected { get; } = expected;

    public int Actual { get; } = actual;
}

/// <summary>
/// 标注读写抽象。文件式与数据库式实现同一个接口，上层（画布/ViewModel）不关心落在哪。
/// </summary>
public interface IAnnotationStore
{
    /// <summary>读取标注；不存在返回 <c>null</c>。</summary>
    Task<AnnotationEnvelope?> LoadAsync(ImageRef image, CancellationToken ct = default);

    /// <summary>保存标注；传 <paramref name="expectedRevision"/> 时做乐观锁校验。</summary>
    Task<AnnotationEnvelope> SaveAsync(
        ImageRef image,
        AnnotationDocument document,
        int? expectedRevision = null,
        CancellationToken ct = default);

    Task<bool> ExistsAsync(ImageRef image, CancellationToken ct = default);
}
