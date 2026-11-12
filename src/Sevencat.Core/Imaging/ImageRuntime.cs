using ImageFacade;
using ImageFacade.Backends;

namespace Sevencat.Core.Imaging;

/// <summary>
/// 图像后端注册点。Core 只依赖 ImageFacade 抽象；具体后端（SkiaSharp / 软后端）由应用启动时注入。
/// </summary>
public static class ImageRuntime
{
    private static readonly object Gate = new();

    public static bool IsConfigured { get; private set; }

    /// <summary>注册全局解码后端；重复调用只生效第一次。</summary>
    public static void Use(IImageBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);

        lock (Gate)
        {
            if (IsConfigured)
            {
                return;
            }

            ImageEngine.Default = new ImageEngine(backend, ownsBackend: true);
            IsConfigured = true;
        }
    }

    public static ImageEngine Engine => ImageEngine.Default
        ?? throw new InvalidOperationException("尚未注册图像后端，请先调用 ImageRuntime.Use(...)");
}
