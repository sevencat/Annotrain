using ImageFacade;
using Sevencat.Core.Model;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Imaging;

/// <summary>按 <see cref="ImageRef"/> 读取图片元信息（宽高 / EXIF 方向），不返回像素。</summary>
public interface IImageInfoSource
{
    Task<ImageInfo?> ReadAsync(ImageRef image, CancellationToken ct = default);
}

/// <summary>
/// 基于 <see cref="BlobStoreRegistry"/> 的实现：按图片自己的 <see cref="ImageRef.Location"/> 取存储，
/// 同一进程里本地与远端混用也能工作；有解码后端时用 ImageFacade 读（能带 EXIF 方向），
/// 没注册后端或解码失败时退化到 <see cref="HeaderImageInfoSource"/>。
/// </summary>
public sealed class BlobImageInfoSource : IImageInfoSource
{
    private readonly BlobStoreRegistry _registry;
    private readonly IImageInfoSource _fallback;

    public BlobImageInfoSource(BlobStoreRegistry registry, IImageInfoSource? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        _registry = registry;
        // 训练端、批处理进程没有图像后端，只靠文件头也能拿到宽高（归一化坐标必需）
        _fallback = fallback ?? new HeaderImageInfoSource(registry);
    }

    public async Task<ImageInfo?> ReadAsync(ImageRef image, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (!ImageRuntime.IsConfigured)
        {
            return await _fallback.ReadAsync(image, ct).ConfigureAwait(false);
        }

        IBlobStore store = _registry.Get(image.Location);
        var key = new StorageKey(image.Location, image.RelativePath);

        try
        {
            await using Stream stream = await store.OpenReadAsync(key, ct).ConfigureAwait(false);
            using FacadeImage facade = ImageRuntime.Engine.Load(stream, new LoadOptions { PreserveMetadata = true });

            int rawWidth = facade.Metadata.Width > 0 ? facade.Metadata.Width : facade.Width;
            int rawHeight = facade.Metadata.Height > 0 ? facade.Metadata.Height : facade.Height;

            return new ImageInfo
            {
                RelativePath = image.RelativePath,
                Width = facade.Width,
                Height = facade.Height,
                RawWidth = rawWidth,
                RawHeight = rawHeight,
                Orientation = MapOrientation(facade.Metadata.Orientation),
            };
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ImageBackendException)
        {
            return await _fallback.ReadAsync(image, ct).ConfigureAwait(false);
        }
    }

    private static ImageOrientation MapOrientation(int orientation) => orientation switch
    {
        2 => ImageOrientation.TopRight,
        3 => ImageOrientation.BottomRight,
        4 => ImageOrientation.BottomLeft,
        5 => ImageOrientation.LeftTop,
        6 => ImageOrientation.RightTop,
        7 => ImageOrientation.RightBottom,
        8 => ImageOrientation.LeftBottom,
        _ => ImageOrientation.TopLeft,
    };
}
