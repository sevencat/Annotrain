using Sevencat.Core.Model;

namespace Sevencat.Core.Simulation;

/// <summary>模拟图里画的形状种类，同时决定标注类别名。</summary>
public enum SyntheticShapeKind
{
    Square = 0,
    Triangle = 1,
    Circle = 2,
}

/// <summary>生成参数。</summary>
/// <param name="ImageCount">生成多少张。</param>
/// <param name="Width">图片宽（像素）。</param>
/// <param name="Height">图片高（像素）。</param>
/// <param name="MinShapes">每张最少几个形状。</param>
/// <param name="MaxShapes">每张最多几个形状。</param>
/// <param name="Seed">随机种子；固定后结果可复现。</param>
public sealed record SyntheticOptions(
    int ImageCount = 8,
    int Width = 320,
    int Height = 240,
    int MinShapes = 1,
    int MaxShapes = 3,
    int? Seed = 20261112);

/// <summary>一个形状的真值：类别 + 轴对齐框（像素）。</summary>
public sealed record SyntheticShape(SyntheticShapeKind Kind, string Label, RectD Bounds);

/// <summary>一张模拟图：PNG 字节 + 真值。文件名带序号，split 在导入时分配。</summary>
public sealed record SyntheticImage(
    string FileName,
    int Width,
    int Height,
    byte[] Png,
    IReadOnlyList<SyntheticShape> Shapes)
{
    public int ShapeCount => Shapes.Count;
}

/// <summary>
/// 生成"正方形 / 三角形 / 圆形"的模拟图，平铺输出（这一步故意不带标注）。
/// 只负责画图与真值，落盘由调用方走 <c>IBlobStore</c>。
/// </summary>
public sealed class SyntheticImageGenerator(SyntheticOptions options)
{
    /// <summary>三个类别名，顺序即导出后的 classId。</summary>
    public static IReadOnlyList<string> ClassNames { get; } = ["square", "triangle", "circle"];

    private static readonly Rgb24Color Background = Rgb24Color.FromHex("F3F4F6");
    private static readonly Rgb24Color SquareColor = Rgb24Color.FromHex("3B82F6");
    private static readonly Rgb24Color TriangleColor = Rgb24Color.FromHex("22C55E");
    private static readonly Rgb24Color CircleColor = Rgb24Color.FromHex("EF4444");

    public IReadOnlyList<SyntheticImage> Generate()
    {
        var random = new Random(options.Seed ?? Environment.TickCount);
        var images = new List<SyntheticImage>(options.ImageCount);

        for (int index = 0; index < options.ImageCount; index++)
        {
            var canvas = new Rgb24Canvas(options.Width, options.Height, Background);
            IReadOnlyList<SyntheticShape> shapes = DrawShapes(canvas, random);

            images.Add(new SyntheticImage(
                $"img_{index:D4}.png",
                options.Width,
                options.Height,
                canvas.EncodePng(),
                shapes));
        }

        return images;
    }

    private IReadOnlyList<SyntheticShape> DrawShapes(Rgb24Canvas canvas, Random random)
    {
        int target = random.Next(options.MinShapes, Math.Max(options.MinShapes, options.MaxShapes) + 1);
        var shapes = new List<SyntheticShape>(target);

        int attempts = 0;

        while (shapes.Count < target && attempts++ < target * 40)
        {
            SyntheticShapeKind kind = (SyntheticShapeKind)random.Next(0, 3);
            double side = random.Next(28, Math.Max(40, Math.Min(options.Width, options.Height) / 3));

            double maxX = canvas.Width - side - 4;
            double maxY = canvas.Height - side - 4;

            if (maxX <= 6 || maxY <= 6)
            {
                break;
            }

            double left = random.NextDouble() * (maxX - 4) + 4;
            double top = random.NextDouble() * (maxY - 4) + 4;

            // 圆用外接正方形当搜索区，保证几何上不越界
            RectD candidate = kind switch
            {
                SyntheticShapeKind.Circle => RectD.FromLtrb(left, top, left + side, top + side),
                SyntheticShapeKind.Triangle => RectD.FromLtrb(left, top, left + side, top + side),
                _ => RectD.FromLtrb(left, top, left + side, top + side),
            };

            if (shapes.Any(shape => Intersects(shape.Bounds, candidate)))
            {
                continue;
            }

            Draw(canvas, kind, candidate);
            shapes.Add(new SyntheticShape(kind, LabelOf(kind), candidate));
        }

        return shapes;
    }

    private static void Draw(Rgb24Canvas canvas, SyntheticShapeKind kind, RectD bounds)
    {
        switch (kind)
        {
            case SyntheticShapeKind.Square:
                canvas.FillRect(bounds, SquareColor);
                break;

            case SyntheticShapeKind.Triangle:
                canvas.FillTriangle(
                    new PointD(bounds.X + (bounds.Width / 2), bounds.Y),
                    new PointD(bounds.Right, bounds.Bottom),
                    new PointD(bounds.X, bounds.Bottom),
                    TriangleColor);
                break;

            default:
                canvas.FillEllipse(
                    bounds.X + (bounds.Width / 2),
                    bounds.Y + (bounds.Height / 2),
                    bounds.Width / 2,
                    bounds.Height / 2,
                    CircleColor);
                break;
        }
    }

    public static string LabelOf(SyntheticShapeKind kind) => kind switch
    {
        SyntheticShapeKind.Square => "square",
        SyntheticShapeKind.Triangle => "triangle",
        _ => "circle",
    };

    private static bool Intersects(RectD a, RectD b)
        => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;
}
