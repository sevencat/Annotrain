using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;

namespace Sevencat.Train.App.Controls;

/// <summary>Avalonia 位图 → BGRA 字节，交给 Core 的推理前处理。</summary>
internal static class BitmapPixels
{
    public static byte[] ToBgra(Bitmap bitmap, out int width, out int height)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        width = (int)bitmap.PixelSize.Width;
        height = (int)bitmap.PixelSize.Height;

        int stride = width * 4;
        byte[] bytes = new byte[stride * height];

        GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);

        try
        {
            bitmap.CopyPixels(new Avalonia.PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), bytes.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        return bytes;
    }
}
