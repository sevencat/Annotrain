using Sevencat.Train.Engine.Core;

namespace Sevencat.Train.Engine.Data.Synthetic;

/// <summary>一个形状样本：类别下标 + 像素包围盒（xyxy）。</summary>
public readonly record struct SyntheticShape(int ClassId, int X1, int Y1, int X2, int Y2)
{
    /// <summary>宽（像素）。</summary>
    public int Width => X2 - X1;

    /// <summary>高（像素）。</summary>
    public int Height => Y2 - Y1;
}

/// <summary>
/// 纯托管光栅化：在 RGB24 缓冲区上填充椭圆 / 矩形，并采样互不重叠的形状。
/// <para>
/// 为什么自己画而不用图片库：数据集生成只依赖「写像素」，自己画可以不引入绘图库、
/// 结果完全确定（同一 seed 逐像素一致），也方便在测试里直接断言像素。
/// </para>
/// </summary>
public static class ShapeRasterizer
{
    /// <summary>椭圆（类别 0 = 圆）填色。</summary>
    public static void FillEllipse(byte[] pixels, int width, int height, SyntheticShape shape, (byte R, byte G, byte B) color)
    {
        double centerX = (shape.X1 + shape.X2) / 2.0;
        double centerY = (shape.Y1 + shape.Y2) / 2.0;
        double radiusX = Math.Max(0.5, shape.Width / 2.0);
        double radiusY = Math.Max(0.5, shape.Height / 2.0);

        int top = Math.Clamp(shape.Y1, 0, height - 1);
        int bottom = Math.Clamp(shape.Y2, 0, height - 1);
        int left = Math.Clamp(shape.X1, 0, width - 1);
        int right = Math.Clamp(shape.X2, 0, width - 1);

        for (int y = top; y <= bottom; y++)
        {
            double dy = (y + 0.5 - centerY) / radiusY;

            for (int x = left; x <= right; x++)
            {
                double dx = (x + 0.5 - centerX) / radiusX;

                if ((dx * dx) + (dy * dy) <= 1.0)
                    SetPixel(pixels, width, x, y, color);
            }
        }
    }

    /// <summary>矩形填色（类别 1 = 正方形、2 = 长方形）。</summary>
    public static void FillRectangle(byte[] pixels, int width, int height, SyntheticShape shape, (byte R, byte G, byte B) color)
    {
        int top = Math.Clamp(shape.Y1, 0, height - 1);
        int bottom = Math.Clamp(shape.Y2 - 1, 0, height - 1);
        int left = Math.Clamp(shape.X1, 0, width - 1);
        int right = Math.Clamp(shape.X2 - 1, 0, width - 1);

        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
                SetPixel(pixels, width, x, y, color);
        }
    }

    /// <summary>等腰三角形填色（类别 3）：顶点在上边中点，底边在下边。</summary>
    public static void FillTriangle(byte[] pixels, int width, int height, SyntheticShape shape, (byte R, byte G, byte B) color)
    {
        double apexX = (shape.X1 + shape.X2) / 2.0;
        double apexY = shape.Y1;
        double leftX = shape.X1;
        double rightX = shape.X2 - 1;
        double baseY = shape.Y2 - 1;

        int top = Math.Clamp(shape.Y1, 0, height - 1);
        int bottom = Math.Clamp(shape.Y2 - 1, 0, height - 1);
        int left = Math.Clamp(shape.X1, 0, width - 1);
        int right = Math.Clamp(shape.X2 - 1, 0, width - 1);

        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                // 三个叉积同号 → 点在三角形内（含边界）。
                double pointX = x + 0.5;
                double pointY = y + 0.5;

                double c1 = Cross(apexX, apexY, leftX, baseY, pointX, pointY);
                double c2 = Cross(leftX, baseY, rightX, baseY, pointX, pointY);
                double c3 = Cross(rightX, baseY, apexX, apexY, pointX, pointY);

                bool inside = (c1 >= 0 && c2 >= 0 && c3 >= 0) || (c1 <= 0 && c2 <= 0 && c3 <= 0);
                if (inside)
                    SetPixel(pixels, width, x, y, color);
            }
        }
    }

    /// <summary>按类别把形状画到缓冲区上（0=椭圆、3=三角形、其余=矩形）。</summary>
    public static void Draw(
        byte[] pixels, int width, int height, SyntheticShape shape, (byte R, byte G, byte B) color)
    {
        switch (shape.ClassId)
        {
            case 0:
                FillEllipse(pixels, width, height, shape, color);
                break;
            case 3:
                FillTriangle(pixels, width, height, shape, color);
                break;
            default:
                FillRectangle(pixels, width, height, shape, color);
                break;
        }
    }

    private static double Cross(double ax, double ay, double bx, double by, double px, double py) =>
        ((bx - ax) * (py - ay)) - ((by - ay) * (px - ax));

    /// <summary>
    /// 采样一个不与已有形状重叠的包围盒；<paramref name="classNameIndex"/> 为 0/1/2。
    /// <para>尺寸按图片边长等比缩放（以 512 为基准），因此 64 或 1024 的图也能用同一套比例。</para>
    /// </summary>
    public static SyntheticShape? SampleShape(
        Rng rng, int classNameIndex, int imageSize, IReadOnlyList<SyntheticShape> existing)
    {
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(existing);

        int gap = Math.Max(2, imageSize / 64);

        for (int attempt = 0; attempt < 300; attempt++)
        {
            (int shapeWidth, int shapeHeight) = SampleSize(rng, classNameIndex, imageSize);

            if (shapeWidth >= imageSize || shapeHeight >= imageSize)
                continue;

            int x1 = rng.NextInt(imageSize - shapeWidth);
            int y1 = rng.NextInt(imageSize - shapeHeight);
            var candidate = new SyntheticShape(classNameIndex, x1, y1, x1 + shapeWidth, y1 + shapeHeight);

            bool clashes = false;
            foreach (SyntheticShape other in existing)
            {
                if (!(candidate.X2 + gap <= other.X1
                      || other.X2 + gap <= candidate.X1
                      || candidate.Y2 + gap <= other.Y1
                      || other.Y2 + gap <= candidate.Y1))
                {
                    clashes = true;
                    break;
                }
            }

            if (!clashes)
                return candidate;
        }

        return null;
    }

    /// <summary>按类别采样形状尺寸（像素）。</summary>
    private static (int Width, int Height) SampleSize(Rng rng, int classNameIndex, int imageSize)
    {
        double scale = imageSize / 512.0;

        switch (classNameIndex)
        {
            case 0: // 圆：直径 60~220（512 基准）
            {
                int diameter = Scaled(rng, 60, 220, scale);
                return (diameter, diameter);
            }

            case 1: // 正方形：边长 40~180
            {
                int edge = Scaled(rng, 40, 180, scale);
                return (edge, edge);
            }

            default: // 长方形：长边是短边的 2~3.5 倍
            {
                int shortSide = Scaled(rng, 40, 120, scale);
                int longSide = (int)MathF.Round(shortSide * rng.NextFloat(2.0f, 3.5f));
                return rng.NextBool(0.5f) ? (longSide, shortSide) : (shortSide, longSide);
            }

            case 3: // 三角形：底边 80~240，高是底边的 0.7~1.3 倍
            {
                int baseWidth = Scaled(rng, 80, 240, scale);
                int height = (int)MathF.Round(baseWidth * rng.NextFloat(0.7f, 1.3f));
                return (baseWidth, height);
            }
        }
    }

    private static int Scaled(Rng rng, int min, int max, double scale)
    {
        int value = min + rng.NextInt(max - min + 1);
        return Math.Max(4, (int)MathF.Round(value * (float)scale));
    }

    private static void SetPixel(byte[] pixels, int width, int x, int y, (byte R, byte G, byte B) color)
    {
        int offset = ((y * width) + x) * 3;

        if (offset < 0 || offset + 2 >= pixels.Length)
            return;

        pixels[offset] = color.R;
        pixels[offset + 1] = color.G;
        pixels[offset + 2] = color.B;
    }
}
