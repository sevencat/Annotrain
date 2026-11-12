using System.Buffers.Binary;
using Sevencat.Core.Model;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Imaging;

/// <summary>
/// 只读文件头取宽高（PNG / JPEG / BMP / GIF），不依赖任何图像解码后端。
/// 训练端、无 UI 的批处理进程靠它就能算归一化坐标，避免为了读尺寸去装 SkiaSharp。
/// </summary>
public sealed class HeaderImageInfoSource(BlobStoreRegistry registry) : IImageInfoSource
{
    /// <summary>文件头最多读这么多字节（JPEG 的 SOF 可能靠后）。</summary>
    public const int ProbeBytes = 64 * 1024;

    public async Task<ImageInfo?> ReadAsync(ImageRef image, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        IBlobStore store = registry.Get(image.Location);
        var key = new StorageKey(image.Location, image.RelativePath);

        var buffer = new byte[ProbeBytes];
        int read = 0;

        try
        {
            await using Stream stream = await store.OpenReadRangeAsync(key, 0, ProbeBytes, ct).ConfigureAwait(false);

            while (read < buffer.Length)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
                if (count <= 0)
                {
                    break;
                }

                read += count;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }

        if (read < 24)
        {
            return null;
        }

        if (!TryReadSize(buffer.AsSpan(0, read), out int width, out int height) || width <= 0 || height <= 0)
        {
            return null;
        }

        return new ImageInfo
        {
            RelativePath = image.RelativePath,
            Width = width,
            Height = height,
            RawWidth = width,
            RawHeight = height,
        };
    }

    /// <summary>按魔数判格式并取宽高；认不出来返回 <c>false</c>。</summary>
    public static bool TryReadSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (data.Length < 24)
        {
            return false;
        }

        // PNG：IHDR 固定在第 16 字节起
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            width = BinaryPrimitives.ReadInt32BigEndian(data[16..20]);
            height = BinaryPrimitives.ReadInt32BigEndian(data[20..24]);
            return true;
        }

        // GIF：宽高小端 16 位
        if (data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F')
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(data[6..8]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(data[8..10]);
            return true;
        }

        // BMP
        if (data[0] == (byte)'B' && data[1] == (byte)'M')
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(data[18..22]);
            height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data[22..26]));
            return true;
        }

        // JPEG：扫标记找 SOF0/1/2
        if (data[0] == 0xFF && data[1] == 0xD8)
        {
            return TryReadJpeg(data, out width, out height);
        }

        return false;
    }

    private static bool TryReadJpeg(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;

        int index = 2;

        while (index + 9 < data.Length)
        {
            if (data[index] != 0xFF)
            {
                index++;
                continue;
            }

            byte marker = data[index + 1];
            index += 2;

            // 无长度的标记
            if (marker is 0xD8 or 0xD9 or 0x01 || marker is >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            if (index + 1 >= data.Length)
            {
                return false;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(data[index..(index + 2)]);
            if (length < 2)
            {
                return false;
            }

            // SOF0..SOF3 / SOF5..SOF7 / SOF9..SOF11 / SOF13..SOF15
            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                if (index + 7 >= data.Length)
                {
                    return false;
                }

                height = BinaryPrimitives.ReadUInt16BigEndian(data[(index + 3)..(index + 5)]);
                width = BinaryPrimitives.ReadUInt16BigEndian(data[(index + 5)..(index + 7)]);
                return width > 0 && height > 0;
            }

            index += length;
        }

        return false;
    }
}
