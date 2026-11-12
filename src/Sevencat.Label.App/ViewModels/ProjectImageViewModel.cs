using Sevencat.Core.Dataset;
using Sevencat.Label;

namespace Sevencat.Label.App.ViewModels;

/// <summary>项目明细表格里的一行：split + 图片 + 框数。</summary>
public sealed class ProjectImageViewModel(ProjectImage image)
{
    public ProjectImage Image { get; } = image;

    public DatasetSample Sample => Image.Sample;

    public string Split => Image.Split;

    public string FileName => Image.FileName;

    public string RelativePath => Sample.Image.RelativePath;

    public int BoxCount => Image.BoxCount;

    /// <summary>表格「尺寸」列。</summary>
    public string SizeText => $"{Sample.Width}×{Sample.Height}";

    /// <summary>表格「标注」列。</summary>
    public string BoxText => BoxCount == 0 ? "未标注" : $"{BoxCount} 框";

    public string Summary => $"{Split} · {Sample.Width}×{Sample.Height} · {BoxCount} 框";
}
