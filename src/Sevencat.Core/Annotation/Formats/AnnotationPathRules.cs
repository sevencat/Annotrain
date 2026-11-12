using Sevencat.Core.Annotation;
using Sevencat.Core.Model;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Annotation.Formats;

/// <summary>labelme 习惯：标注与图片同目录、同名、扩展名换成 codec 的第一个扩展名。</summary>
public sealed class SiblingAnnotationPathRule : IAnnotationPathRule
{
    public StorageKey Resolve(ImageRef image, IAnnotationCodec codec)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(codec);

        string extension = codec.Extensions.FirstOrDefault() ?? ".json";
        string path = Path.ChangeExtension(image.RelativePath, extension).Replace('\\', '/');

        return new StorageKey(image.Location, path);
    }
}

/// <summary>
/// YOLO 习惯：<c>images/train/a.jpg</c> → <c>labels/train/a.txt</c>。
/// 路径里没有 <c>images</c> 段时退回同级目录。
/// </summary>
public sealed class YoloAnnotationPathRule : IAnnotationPathRule
{
    public StorageKey Resolve(ImageRef image, IAnnotationCodec codec)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(codec);

        string extension = codec.Extensions.FirstOrDefault() ?? ".txt";
        const string imagesSegment = "/images/";
        const string labelsSegment = "/labels/";

        string relative = image.RelativePath.Replace('\\', '/');
        string withExtension = Path.ChangeExtension(relative, extension).Replace('\\', '/');

        string replaced = withExtension.StartsWith("images/", StringComparison.OrdinalIgnoreCase)
            ? "labels/" + withExtension["images/".Length..]
            : withExtension.Contains(imagesSegment, StringComparison.OrdinalIgnoreCase)
                ? withExtension.Replace(imagesSegment, labelsSegment, StringComparison.OrdinalIgnoreCase)
                : withExtension;

        return new StorageKey(image.Location, replaced);
    }
}
