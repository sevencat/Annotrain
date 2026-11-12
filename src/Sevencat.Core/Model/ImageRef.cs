namespace Sevencat.Core.Model;

/// <summary>EXIF 方向（数值与 EXIF Orientation 1–8 一致）。</summary>
public enum ImageOrientation
{
    TopLeft = 1,
    TopRight = 2,
    BottomRight = 3,
    BottomLeft = 4,
    LeftTop = 5,
    RightTop = 6,
    RightBottom = 7,
    LeftBottom = 8,
}

/// <summary>
/// 图片引用：<see cref="RelativePath"/> 相对数据集根，<see cref="Location"/> 指定存储后端。
/// 本地与远端（s3）都用同一套引用，读写都走 <c>IBlobStore</c>。
/// </summary>
public sealed record ImageRef(string RelativePath, Storage.StorageLocation Location)
{
    public string FileName => Path.GetFileName(RelativePath);

    public string? Directory => Path.GetDirectoryName(RelativePath)?.Replace('\\', '/');

    public override string ToString() => $"{Location}/{RelativePath}";
}

/// <summary>图片元信息；<see cref="Width"/>/<see cref="Height"/> 为 EXIF 归一后尺寸。</summary>
public sealed record ImageInfo
{
    public required string RelativePath { get; init; }

    /// <summary>EXIF 归一后的宽（即显示/标注使用的宽）。</summary>
    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>文件内实际存储的宽（未旋转）。</summary>
    public int RawWidth { get; init; }

    public int RawHeight { get; init; }

    public ImageOrientation Orientation { get; init; } = ImageOrientation.TopLeft;

    public long FileSizeBytes { get; init; }

    public string FileName => Path.GetFileName(RelativePath);

    /// <summary>宽高是否因 EXIF 方向发生交换。</summary>
    public bool IsRotated => Width != RawWidth || Height != RawHeight;

    public PixelRect Bounds => new(0, 0, Width, Height);
}

/// <summary>常见图片扩展名（大小写不敏感）。</summary>
public static class ImageFiles
{
    public static readonly IReadOnlySet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe",
        ".png",
        ".bmp",
        ".webp",
        ".tif", ".tiff",
    };

    public static bool IsSupported(string pathOrName) => SupportedExtensions.Contains(Path.GetExtension(pathOrName));
}
