namespace Sevencat.Train.Engine.Data.Images;

/// <summary>RGB24 画布：mosaic 拼贴用的最小实现（只做整块拷贝与裁剪，无缩放）。</summary>
internal sealed class RgbCanvas
{
    public RgbCanvas(int width, int height, byte fill)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "画布尺寸必须大于 0。");

        Width = width;
        Height = height;
        Pixels = new byte[width * height * 3];
        Array.Fill(Pixels, fill);
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    /// <summary>把 <paramref name="source"/> 贴到 (<paramref name="x"/>, <paramref name="y"/>)；越界部分自动裁掉。</summary>
    public void Blit(byte[] source, int sourceWidth, int sourceHeight, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (sourceWidth <= 0 || sourceHeight <= 0 ||
            source.Length != sourceWidth * sourceHeight * 3)
        {
            throw new ArgumentException(
                $"源图尺寸 {sourceWidth}x{sourceHeight} 与数据长度 {source.Length} 不匹配。",
                nameof(source));
        }

        int sourceX = Math.Max(0, -x);
        int sourceY = Math.Max(0, -y);
        int targetX = Math.Max(0, x);
        int targetY = Math.Max(0, y);
        int copyWidth = Math.Min(sourceWidth - sourceX, Width - targetX);
        int copyHeight = Math.Min(sourceHeight - sourceY, Height - targetY);

        if (copyWidth <= 0 || copyHeight <= 0)
            return;

        for (int row = 0; row < copyHeight; row++)
        {
            int sourceOffset = ((sourceY + row) * sourceWidth + sourceX) * 3;
            int targetOffset = ((targetY + row) * Width + targetX) * 3;
            Array.Copy(source, sourceOffset, Pixels, targetOffset, copyWidth * 3);
        }
    }

    /// <summary>裁剪一块区域（调用方保证落在画布内）。</summary>
    public byte[] Crop(int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > Width || y + height > Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(x), x, $"裁剪区域 ({x},{y},{width},{height}) 超出画布 {Width}x{Height}。");
        }

        var result = new byte[width * height * 3];
        for (int row = 0; row < height; row++)
        {
            int sourceOffset = ((y + row) * Width + x) * 3;
            Array.Copy(Pixels, sourceOffset, result, row * width * 3, width * 3);
        }

        return result;
    }
}
