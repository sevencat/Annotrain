namespace Sevencat.Train.Engine.Data.Images;

/// <summary>打包 RGB24 像素上的增强与颜色空间运算（内部实现，避免把像素细节散到加载器里）。</summary>
internal static class RgbAugment
{
    /// <summary>原地水平翻转（每行左右镜像）。</summary>
    public static void FlipHorizontal(Span<byte> rgb, int width, int height)
    {
        int rowBytes = width * 3;

        for (int y = 0; y < height; y++)
        {
            int row = y * rowBytes;
            for (int left = 0, right = width - 1; left < right; left++, right--)
            {
                int a = row + left * 3;
                int b = row + right * 3;

                (rgb[a], rgb[b]) = (rgb[b], rgb[a]);
                (rgb[a + 1], rgb[b + 1]) = (rgb[b + 1], rgb[a + 1]);
                (rgb[a + 2], rgb[b + 2]) = (rgb[b + 2], rgb[a + 2]);
            }
        }
    }

    /// <summary>
    /// 原地做 HSV 扰动：色相平移 <paramref name="hueDegrees"/>，饱和度 / 明度乘以比例。
    /// <para>输入输出都是 RGB24；色相按 0..360° 计算，避免依赖 OpenCV 的 0..180 约定。</para>
    /// </summary>
    public static void AdjustHsv(Span<byte> rgb, float hueDegrees, float saturationScale, float valueScale)
    {
        for (int offset = 0; offset < rgb.Length; offset += 3)
        {
            RgbToHsv(rgb[offset], rgb[offset + 1], rgb[offset + 2], out float hue, out float saturation, out float value);

            hue += hueDegrees;
            hue %= 360f;
            if (hue < 0f)
                hue += 360f;

            saturation = Math.Clamp(saturation * saturationScale, 0f, 1f);
            value = Math.Clamp(value * valueScale, 0f, 1f);

            HsvToRgb(hue, saturation, value, out byte r, out byte g, out byte b);
            rgb[offset] = r;
            rgb[offset + 1] = g;
            rgb[offset + 2] = b;
        }
    }

    private static void RgbToHsv(byte r, byte g, byte b, out float hue, out float saturation, out float value)
    {
        float red = r / 255f;
        float green = g / 255f;
        float blue = b / 255f;

        float max = MathF.Max(red, MathF.Max(green, blue));
        float min = MathF.Min(red, MathF.Min(green, blue));
        float delta = max - min;

        hue = 0f;
        saturation = max <= 0f ? 0f : delta / max;
        value = max;

        if (delta <= 0f)
            return;

        if (max == red)
            hue = 60f * (((green - blue) / delta) % 6f);
        else if (max == green)
            hue = 60f * ((blue - red) / delta + 2f);
        else
            hue = 60f * ((red - green) / delta + 4f);

        if (hue < 0f)
            hue += 360f;
    }

    private static void HsvToRgb(float hue, float saturation, float value, out byte r, out byte g, out byte b)
    {
        float chroma = value * saturation;
        float sector = hue / 60f;
        float x = chroma * (1f - MathF.Abs(sector % 2f - 1f));
        float match = value - chroma;

        (float red, float green, float blue) = sector switch
        {
            < 1f => (chroma, x, 0f),
            < 2f => (x, chroma, 0f),
            < 3f => (0f, chroma, x),
            < 4f => (0f, x, chroma),
            < 5f => (x, 0f, chroma),
            _ => (chroma, 0f, x),
        };

        r = ToByte(red + match);
        g = ToByte(green + match);
        b = ToByte(blue + match);
    }

    private static byte ToByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}
