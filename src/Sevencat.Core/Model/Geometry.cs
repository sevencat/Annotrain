namespace Sevencat.Core.Model;

/// <summary>图像坐标系中的点（单位：像素，原点左上，Y 向下）。</summary>
public readonly record struct PointD(double X, double Y)
{
    public static PointD Origin => new(0, 0);

    public double DistanceTo(PointD other)
    {
        double dx = X - other.X;
        double dy = Y - other.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    public PointD Offset(double dx, double dy) => new(X + dx, Y + dy);
}

/// <summary>双精度矩形（X/Y 为左上角）。</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public static RectD Empty => new(0, 0, 0, 0);

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double Area => Width <= 0 || Height <= 0 ? 0 : Width * Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public double CenterX => X + (Width / 2);

    public double CenterY => Y + (Height / 2);

    public static RectD FromLtrb(double left, double top, double right, double bottom)
        => new(left, top, right - left, bottom - top);

    public static RectD FromPoints(PointD a, PointD b)
        => FromLtrb(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    public RectD Inflate(double dx, double dy)
        => new(X - dx, Y - dy, Width + (2 * dx), Height + (2 * dy));

    public bool Contains(PointD p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;
}

/// <summary>整型像素矩形（区域解码、掩码等用）。</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public static PixelRect Empty => new(0, 0, 0, 0);

    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public PixelRect Intersect(PixelRect other)
    {
        int left = Math.Max(X, other.X);
        int top = Math.Max(Y, other.Y);
        int right = Math.Min(Right, other.Right);
        int bottom = Math.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top ? Empty : new PixelRect(left, top, right - left, bottom - top);
    }
}
