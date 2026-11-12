namespace Sevencat.Train.Engine.Training.Yolo;

/// <summary>一个锚点：所属层 stride、格点坐标（cell 单位）与像素中心。</summary>
public readonly record struct YoloAnchor(int Stride, float CellX, float CellY, float PixelX, float PixelY);

/// <summary>
/// P3/P4/P5 的锚点网格，顺序必须与检测头输出一致：
/// 先 P3（stride 8）逐行、再 P4（16）、最后 P5（32）。
/// </summary>
public static class YoloAnchorGrid
{
    /// <summary>YOLOv8 的三个下采样倍数。</summary>
    public static readonly int[] Strides = { 8, 16, 32 };

    /// <summary>按输入尺寸生成锚点；尺寸必须是 32 的倍数。</summary>
    public static YoloAnchor[] Create(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "图像尺寸必须大于 0。");

        var anchors = new List<YoloAnchor>(width * height / 16);

        foreach (int stride in Strides)
        {
            if (width % stride != 0 || height % stride != 0)
            {
                throw new InvalidOperationException(
                    $"图像尺寸 {width}x{height} 必须是 {stride} 的倍数。");
            }

            int rows = height / stride;
            int columns = width / stride;

            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < columns; x++)
                {
                    float cellX = x + 0.5f;
                    float cellY = y + 0.5f;
                    anchors.Add(new YoloAnchor(stride, cellX, cellY, cellX * stride, cellY * stride));
                }
            }
        }

        return anchors.ToArray();
    }
}
