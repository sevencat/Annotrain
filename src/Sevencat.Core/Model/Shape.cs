namespace Sevencat.Core.Model;

/// <summary>形状类型。<c>OrientedRectangle</c> 对应 labelme 的 <c>orientation</c>。</summary>
public enum ShapeType
{
    Rectangle,
    OrientedRectangle,
    Polygon,
    Circle,
    Line,
    Mask,
    /// <summary>labelme 的 points：只读兼容。</summary>
    Point,
    /// <summary>labelme 的 linestrip：只读兼容。</summary>
    Linestrip,
    Unknown,
}

public static class ShapeTypes
{
    public static string ToLabelme(ShapeType type) => type switch
    {
        ShapeType.Rectangle => "rectangle",
        ShapeType.OrientedRectangle => "orientation",
        ShapeType.Polygon => "polygon",
        ShapeType.Circle => "circle",
        ShapeType.Line => "line",
        ShapeType.Mask => "mask",
        ShapeType.Point => "point",
        ShapeType.Linestrip => "linestrip",
        _ => "polygon",
    };

    public static ShapeType Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "rectangle" => ShapeType.Rectangle,
        "orientation" => ShapeType.OrientedRectangle,
        "polygon" => ShapeType.Polygon,
        "circle" => ShapeType.Circle,
        "line" => ShapeType.Line,
        "mask" => ShapeType.Mask,
        "point" => ShapeType.Point,
        "linestrip" => ShapeType.Linestrip,
        null or "" => ShapeType.Polygon,
        _ => ShapeType.Unknown,
    };

    /// <summary>是否需要至少 3 个点构成闭合区域。</summary>
    public static bool IsClosed(ShapeType type) => type is
        ShapeType.Rectangle or ShapeType.OrientedRectangle or ShapeType.Polygon or ShapeType.Mask;
}

/// <summary>
/// 不可变形状。编辑采用「生成新实例 + 撤销栈记录替换」，禁止原地修改点集。
/// </summary>
public sealed record Shape
{
    private static readonly IReadOnlyDictionary<string, bool> EmptyFlags = new Dictionary<string, bool>(0);

    public required string Id { get; init; }

    public required ShapeType Type { get; init; }

    /// <summary>图像坐标（像素）。</summary>
    public required IReadOnlyList<PointD> Points { get; init; }

    public string Label { get; init; } = string.Empty;

    public int? GroupId { get; init; }

    public string? Description { get; init; }

    public IReadOnlyDictionary<string, bool> Flags { get; init; } = EmptyFlags;

    /// <summary>mask 形状的附加数据；首版不处理，读进来原样写回。</summary>
    public string? MaskBase64 { get; init; }

    public bool IsClosed => ShapeTypes.IsClosed(Type);

    /// <summary>外包矩形。</summary>
    public RectD Bounds
    {
        get
        {
            if (Points.Count == 0)
            {
                return RectD.Empty;
            }

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (PointD point in Points)
            {
                if (point.X < minX)
                {
                    minX = point.X;
                }

                if (point.Y < minY)
                {
                    minY = point.Y;
                }

                if (point.X > maxX)
                {
                    maxX = point.X;
                }

                if (point.Y > maxY)
                {
                    maxY = point.Y;
                }
            }

            return RectD.FromLtrb(minX, minY, maxX, maxY);
        }
    }

    public Shape WithPoints(IReadOnlyList<PointD> points) => this with { Points = points };

    public Shape WithLabel(string label) => this with { Label = label };

    public Shape Translate(double dx, double dy)
    {
        if (Points.Count == 0)
        {
            return this;
        }

        var moved = new PointD[Points.Count];
        for (int i = 0; i < Points.Count; i++)
        {
            moved[i] = Points[i].Offset(dx, dy);
        }

        return this with { Points = moved };
    }

    public bool HitTest(PointD point, double tolerance)
    {
        foreach (PointD vertex in Points)
        {
            if (vertex.DistanceTo(point) <= tolerance)
            {
                return true;
            }
        }

        return IsClosed && ContainsPoint(point);
    }

    /// <summary>点是否落在外包框内（先粗判，精确判定在命中测试里逐步细化）。</summary>
    public bool ContainsPoint(PointD point) => Bounds.Contains(point);
}
