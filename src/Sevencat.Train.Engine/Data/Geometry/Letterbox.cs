namespace Sevencat.Train.Engine.Data.Geometry;

/// <summary>
/// letterbox（等比缩放 + 居中填充）的坐标变换。
/// <para>
/// 正向把原图像素坐标映射到网络输入坐标；<see cref="Inverse"/> 把检测框映射回原图。
/// <c>stretch=true</c> 时允许非等比缩放（YOLO 的 <c>rect=False</c> 之外的另一种选择）。
/// </para>
/// </summary>
public readonly record struct LetterboxTransform(
    float ScaleX,
    float ScaleY,
    float PadX,
    float PadY,
    int SourceWidth,
    int SourceHeight,
    int TargetWidth,
    int TargetHeight)
{
    /// <summary>按原图/目标尺寸计算变换。</summary>
    public static LetterboxTransform Compute(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        bool stretch = false)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "原图尺寸必须大于 0");
        if (targetWidth <= 0 || targetHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetWidth), "目标尺寸必须大于 0");

        float scaleX = (float)targetWidth / sourceWidth;
        float scaleY = (float)targetHeight / sourceHeight;

        if (!stretch)
        {
            float scale = MathF.Min(scaleX, scaleY);
            scaleX = scale;
            scaleY = scale;
        }

        float padX = (targetWidth - sourceWidth * scaleX) / 2f;
        float padY = (targetHeight - sourceHeight * scaleY) / 2f;

        return new LetterboxTransform(
            scaleX,
            scaleY,
            padX,
            padY,
            sourceWidth,
            sourceHeight,
            targetWidth,
            targetHeight);
    }

    /// <summary>像素坐标：原图 → 网络输入。</summary>
    public (float X, float Y, float Width, float Height) Forward(float x, float y, float width, float height) =>
        (x * ScaleX + PadX, y * ScaleY + PadY, width * ScaleX, height * ScaleY);

    /// <summary>像素坐标：网络输入 → 原图。</summary>
    public (float X, float Y, float Width, float Height) Inverse(float x, float y, float width, float height) =>
        ((x - PadX) / ScaleX, (y - PadY) / ScaleY, width / ScaleX, height / ScaleY);

    /// <summary>归一化坐标（相对原图尺寸）：原图 → 网络输入。</summary>
    public (float X, float Y, float Width, float Height) ForwardNormalized(
        float x,
        float y,
        float width,
        float height) =>
        Forward(x * SourceWidth, y * SourceHeight, width * SourceWidth, height * SourceHeight);

    /// <summary>归一化坐标（相对原图尺寸）：网络输入 → 原图。</summary>
    public (float X, float Y, float Width, float Height) InverseNormalized(
        float x,
        float y,
        float width,
        float height)
    {
        (float rx, float ry, float rw, float rh) = Inverse(x, y, width, height);
        return (rx / SourceWidth, ry / SourceHeight, rw / SourceWidth, rh / SourceHeight);
    }

    public override string ToString() =>
        $"letterbox {SourceWidth}x{SourceHeight} -> {TargetWidth}x{TargetHeight} "
        + $"(scale {ScaleX:0.###}x{ScaleY:0.###}, pad {PadX:0.##},{PadY:0.##})";
}
