using Sevencat.Core.Model;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Annotation;

/// <summary>图片 → 标注文件的路径规则（YOLO 的 images/labels 分离、labelme 的同目录同名）。</summary>
public interface IAnnotationPathRule
{
    StorageKey Resolve(ImageRef image, IAnnotationCodec codec);
}
