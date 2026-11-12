using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Model;
using Sevencat.Core.Storage;

namespace Sevencat.Label.App.ViewModels;

/// <summary>
/// 标注窗口里可以连续标注的一张图：行数据 + 图片引用 + 它所在的 blob store。
/// 「保存并继续」在同一窗口里换图，靠的就是这份列表（不用关窗再开）。
/// </summary>
public sealed record AnnotationTarget(TinyLabelImageRow Row, ImageRef Image, IBlobStore Store);
