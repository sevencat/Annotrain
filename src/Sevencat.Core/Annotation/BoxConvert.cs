using Sevencat.Core.Model;

namespace Sevencat.Core.Annotation;

/// <summary>
/// 坐标换算统一入口。内部一律原图像素 xyxy；只在读写格式时换算。
/// 所有换算都做边界裁剪，避免脏数据把框画到图外。
/// </summary>
public static class BoxConvert
{
    /// <summary>原图像素矩形 → YOLO 归一化 center/wh。</summary>
    public static (double CenterX, double CenterY, double Width, double Height) ToYolo(RectD box, int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            return (0, 0, 0, 0);
        }

        return (
            (box.X + (box.Width / 2)) / imageWidth,
            (box.Y + (box.Height / 2)) / imageHeight,
            box.Width / imageWidth,
            box.Height / imageHeight);
    }

    /// <summary>YOLO 归一化 center/wh → 原图像素矩形。</summary>
    public static RectD FromYolo(double centerX, double centerY, double width, double height, int imageWidth, int imageHeight)
    {
        double w = width * imageWidth;
        double h = height * imageHeight;
        double cx = centerX * imageWidth;
        double cy = centerY * imageHeight;

        return RectD.FromLtrb(cx - (w / 2), cy - (h / 2), cx + (w / 2), cy + (h / 2));
    }

    /// <summary>COCO 绝对像素 xywh → 矩形。</summary>
    public static RectD FromXywh(double x, double y, double width, double height) => new(x, y, width, height);

    /// <summary>矩形 → COCO 绝对像素 xywh。</summary>
    public static (double X, double Y, double Width, double Height) ToXywh(RectD box) => (box.X, box.Y, box.Width, box.Height);

    /// <summary>裁剪到图像范围；完全在图外则返回空矩形。</summary>
    public static RectD Clamp(RectD box, int imageWidth, int imageHeight)
    {
        double left = Math.Clamp(box.X, 0, imageWidth);
        double top = Math.Clamp(box.Y, 0, imageHeight);
        double right = Math.Clamp(box.Right, 0, imageWidth);
        double bottom = Math.Clamp(box.Bottom, 0, imageHeight);

        return right <= left || bottom <= top ? RectD.Empty : RectD.FromLtrb(left, top, right, bottom);
    }

    /// <summary>两条矩形框的 IoU（评估与 GT 对比用）。</summary>
    public static double IntersectionOverUnion(RectD a, RectD b)
    {
        double left = Math.Max(a.X, b.X);
        double top = Math.Max(a.Y, b.Y);
        double right = Math.Min(a.Right, b.Right);
        double bottom = Math.Min(a.Bottom, b.Bottom);

        double intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        double union = a.Area + b.Area - intersection;

        return union <= 0 ? 0 : intersection / union;
    }

    /// <summary>矩形 → 四个角点（labelme / canvas 用，顺序：左上、右上、右下、左下）。</summary>
    public static IReadOnlyList<PointD> ToCorners(RectD rect) =>
    [
        new PointD(rect.X, rect.Y),
        new PointD(rect.Right, rect.Y),
        new PointD(rect.Right, rect.Bottom),
        new PointD(rect.X, rect.Bottom),
    ];

    /// <summary>任意形状的点集 → 外包矩形。</summary>
    public static RectD BoundsOf(IReadOnlyList<PointD> points)
        => new Shape { Id = "-", Type = ShapeType.Polygon, Points = points }.Bounds;
}
