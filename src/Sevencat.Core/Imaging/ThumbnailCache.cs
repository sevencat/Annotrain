using ImageFacade;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Imaging;

/// <summary>
/// 项目缩略图缓存：文件落在 <c>projects/{id}/thumbs/</c>，目录结构与原图镜像，扩展名统一 <c>.jpg</c>。
/// <para>
/// 列表只读这里的文件，大图不用每帧解码；缩略图按需生成（导入时顺手生成一份），
/// 原图比缩略图新时重新生成。没有注册图像后端时 <see cref="EnsureAsync"/> 返回 null，调用方退回原图。
/// </para>
/// </summary>
public static class ThumbnailCache
{
    /// <summary>缩略图长边默认像素：列表里 40~64px 显示，留 4 倍余量给高分屏。</summary>
    public const int DefaultSize = 512;

    private const int JpegQuality = 82;

    /// <summary>
    /// 缩略图的绝对路径；<paramref name="relativePath"/> 为图片在项目内的相对路径。
    /// 文件名带尺寸（<c>name@512.jpg</c>）：同一张图的不同尺寸各存一份，改尺寸也不会读到旧缓存。
    /// </summary>
    public static string PathOf(StorageLocation root, long projectId, string relativePath, int size = DefaultSize)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        int edge = size <= 0 ? DefaultSize : size;
        string relative = relativePath.Replace('\\', '/').TrimStart('/');
        string name = Path.GetFileNameWithoutExtension(relative);
        string? directory = Path.GetDirectoryName(relative.Replace('/', Path.DirectorySeparatorChar));

        string folder = string.IsNullOrEmpty(directory)
            ? TinyLabelWorkspace.ThumbDirectory(root, projectId)
            : Path.Combine(TinyLabelWorkspace.ThumbDirectory(root, projectId), directory);

        return Path.Combine(folder, $"{name}@{edge}.jpg");
    }

    /// <summary>
    /// 保证缩略图存在且不比原图旧；成功返回缩略图路径，无法生成（没后端 / 解码失败）返回 null。
    /// </summary>
    public static async Task<string?> EnsureAsync(
        StorageLocation root,
        long projectId,
        string relativePath,
        string sourcePath,
        int size = DefaultSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        if (!ImageRuntime.IsConfigured)
        {
            return null;
        }

        int edge = size <= 0 ? DefaultSize : size;
        string thumbPath = PathOf(root, projectId, relativePath, edge);

        try
        {
            if (File.Exists(thumbPath)
                && File.Exists(sourcePath)
                && File.GetLastWriteTimeUtc(thumbPath) >= File.GetLastWriteTimeUtc(sourcePath))
            {
                return thumbPath;
            }

            if (!File.Exists(sourcePath))
            {
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(thumbPath)!);

            byte[] bytes = await EncodeAsync(sourcePath, edge, ct).ConfigureAwait(false);

            // 先写临时文件再改名：并发读列表时不会读到写了一半的 jpg
            string temp = thumbPath + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            File.Move(temp, thumbPath, overwrite: true);

            return thumbPath;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ImageBackendException
                                          or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<byte[]> EncodeAsync(string sourcePath, int size, CancellationToken ct)
    {
        await using FileStream stream = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);

        ct.ThrowIfCancellationRequested();

        return await Task.Run(
            () =>
            {
                using FacadeImage image = ImageRuntime.Engine.Load(
                    stream,
                    new LoadOptions { AutoOrient = true });

                FacadeImage resized = image.Resize(new ResizeOptions
                {
                    Width = size,
                    Height = size,
                    Mode = ResizeMode.Max,
                    Sampler = Sampler.Lanczos3,
                });

                try
                {
                    return resized.Encode(new SaveOptions
                    {
                        Format = ImageFormat.Jpeg,
                        Quality = JpegQuality,
                        BackgroundColor = Rgba32.White,
                    });
                }
                finally
                {
                    if (!ReferenceEquals(resized, image))
                    {
                        resized.Dispose();
                    }
                }
            },
            ct).ConfigureAwait(false);
    }
}
