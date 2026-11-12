using ImageFacade;
using Sevencat.Core.Imaging;

namespace Sevencat.Train.Engine.Data.Images;

/// <summary>
/// 图像编解码门面。后端由宿主在 exe 入口注册（<see cref="ImageRuntime.Use"/>，本项目统一 SkiaSharp），
/// 训练引擎自己不注册任何后端，也不假设某种编解码实现。
/// </summary>
public static class ImageCodecs
{
    /// <summary>取宿主注册的图像引擎；未注册时由 <see cref="ImageRuntime"/> 抛出带指引的异常。</summary>
    public static ImageEngine Engine => ImageRuntime.Engine;

    /// <summary>
    /// 把打包的 RGB24 像素写成图片（调试预览 / 测试用）。
    /// </summary>
    public static void SaveRgb24(ReadOnlySpan<byte> pixels, int width, int height, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        long expected = (long)width * height * 3;
        if (width <= 0 || height <= 0 || pixels.Length != expected)
        {
            throw new ArgumentException(
                $"RGB24 数据长度 {pixels.Length} 与 {width}x{height}x3 = {expected} 不一致。",
                nameof(pixels));
        }

        using FacadeImage image = Engine.FromPixels(
            pixels,
            width,
            height,
            width * 3,
            PixelFormat.Rgb24);
        image.Save(path, new SaveOptions());
    }
}
