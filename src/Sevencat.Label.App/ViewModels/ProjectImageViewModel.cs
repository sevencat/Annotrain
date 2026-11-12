using Sevencat.Core.Dataset;
using Sevencat.Label;

namespace Sevencat.Label.App.ViewModels;

/// <summary>数据集浏览器里的一行：所属 split + 图片 + 框。</summary>
public sealed class ProjectImageViewModel(ProjectImage image)
{
    public ProjectImage Image { get; } = image;

    public DatasetSample Sample => Image.Sample;

    public string Split => Image.Split;

    public string FileName => Image.FileName;

    public string RelativePath => Sample.Image.RelativePath;

    public int BoxCount => Image.BoxCount;

    public string Summary => $"{Split} · {Sample.Width}×{Sample.Height} · {BoxCount} 框";
}

/// <summary>叠在预览图上的一个框（原图像素坐标，随 Viewbox 一起缩放）。</summary>
public sealed class BoxOverlay(double x, double y, double width, double height, int classId)
{
    public double X { get; } = x;

    public double Y { get; } = y;

    public double Width { get; } = width;

    public double Height { get; } = height;

    public int ClassId { get; } = classId;
}
