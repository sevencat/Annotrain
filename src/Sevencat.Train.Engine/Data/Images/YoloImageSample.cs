using Sevencat.Train.Engine.Data.Geometry;

namespace Sevencat.Train.Engine.Data.Images;

/// <summary>一个检测目标框：类别 + 网络输入坐标系下的 <c>(x1,y1,x2,y2)</c> 像素值。</summary>
public readonly record struct YoloTargetBox(int ClassId, float X1, float Y1, float X2, float Y2)
{
    /// <summary>框宽。</summary>
    public float Width => X2 - X1;

    /// <summary>框高。</summary>
    public float Height => Y2 - Y1;

    /// <summary>中心点 x。</summary>
    public float CenterX => (X1 + X2) / 2f;

    /// <summary>中心点 y。</summary>
    public float CenterY => (Y1 + Y2) / 2f;
}

/// <summary>
/// 一张经过 letterbox（可选增强）的检测样本：网络尺寸的 RGB 像素 + 映射后的目标框。
/// <para>
/// 保留 RGB 像素而不是直接转成 CHW 浮点：mosaic 等后续增强要在像素层面拼图，
/// 训练组 batch 时再调用 <see cref="ToChw"/> 转成模型输入，避免重复解码。
/// </para>
/// </summary>
public sealed class YoloImageSample
{
    internal YoloImageSample(
        string imagePath,
        int sourceWidth,
        int sourceHeight,
        int modelWidth,
        int modelHeight,
        byte[] rgb24,
        LetterboxTransform transform,
        IReadOnlyList<YoloTargetBox> boxes,
        bool flipped,
        bool isMosaic = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(rgb24);
        ArgumentNullException.ThrowIfNull(boxes);

        long expected = (long)modelWidth * modelHeight * 3;
        if (rgb24.Length != expected)
        {
            throw new ArgumentException(
                $"像素缓冲区 {rgb24.Length} B 与 {modelWidth}x{modelHeight}x3 = {expected} B 不一致。",
                nameof(rgb24));
        }

        ImagePath = imagePath;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        ModelWidth = modelWidth;
        ModelHeight = modelHeight;
        Rgb24 = rgb24;
        Transform = transform;
        Boxes = boxes;
        Flipped = flipped;
        IsMosaic = isMosaic;
    }

    /// <summary>原图路径。</summary>
    public string ImagePath { get; }

    /// <summary>原图宽（letterbox 之前）。</summary>
    public int SourceWidth { get; }

    /// <summary>原图高（letterbox 之前）。</summary>
    public int SourceHeight { get; }

    /// <summary>网络输入宽。</summary>
    public int ModelWidth { get; }

    /// <summary>网络输入高。</summary>
    public int ModelHeight { get; }

    /// <summary>网络尺寸的 RGB24 像素（行连续，stride = <c>ModelWidth * 3</c>）。</summary>
    public byte[] Rgb24 { get; }

    /// <summary>原图 → 网络输入的坐标变换（含 scale 与 pad）。</summary>
    public LetterboxTransform Transform { get; }

    /// <summary>目标框（网络输入像素坐标）。</summary>
    public IReadOnlyList<YoloTargetBox> Boxes { get; }

    /// <summary>是否做过水平翻转。</summary>
    public bool Flipped { get; }

    /// <summary>
    /// 是否来自 mosaic 拼贴。为 true 时 <see cref="SourceWidth"/> / <see cref="SourceHeight"/>
    /// 表示 2 倍尺寸的拼贴画布，<see cref="Transform"/> 描述的是「画布 → 随机裁剪窗口」。
    /// </summary>
    public bool IsMosaic { get; }

    /// <summary>转成 <c>[3, H, W]</c> 的 F32 数组，取值 <c>[0,1]</c>（RGB 平面顺序）。</summary>
    public float[] ToChw()
    {
        int plane = ModelWidth * ModelHeight;
        var chw = new float[plane * 3];

        for (int index = 0, source = 0; index < plane; index++, source += 3)
        {
            chw[index] = Rgb24[source] * (1f / 255f);
            chw[plane + index] = Rgb24[source + 1] * (1f / 255f);
            chw[2 * plane + index] = Rgb24[source + 2] * (1f / 255f);
        }

        return chw;
    }

    /// <summary>取网络输入坐标系下某个像素的 RGB 值（预览 / 调试用）。</summary>
    public (byte R, byte G, byte B) GetPixel(int x, int y)
    {
        if (x < 0 || x >= ModelWidth || y < 0 || y >= ModelHeight)
        {
            throw new ArgumentOutOfRangeException(
                nameof(x), x, $"像素坐标必须在 [0,{ModelWidth})x[0,{ModelHeight}) 内。");
        }

        int offset = (y * ModelWidth + x) * 3;
        return (Rgb24[offset], Rgb24[offset + 1], Rgb24[offset + 2]);
    }
}
