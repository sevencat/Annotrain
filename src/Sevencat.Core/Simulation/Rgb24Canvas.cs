using System.IO.Compression;
using Sevencat.Core.Model;

namespace Sevencat.Core.Simulation;

/// <summary>RGB 颜色（生成模拟图用，不参与标注数据）。</summary>
public readonly record struct Rgb24Color(byte R, byte G, byte B)
{
    public static Rgb24Color FromHex(string hex)
    {
        string value = hex.TrimStart('#');

        return new Rgb24Color(
            Convert.ToByte(value[..2], 16),
            Convert.ToByte(value.Substring(2, 2), 16),
            Convert.ToByte(value.Substring(4, 2), 16));
    }
}

/// <summary>
/// 极简 RGB24 画布：只做矩形 / 三角形 / 椭圆填充 + PNG 编码。
/// 不引第三方图形库，保证模拟流程在 Core 里可跑、可测。
/// </summary>
public sealed class Rgb24Canvas
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    private readonly byte[] _pixels;

    public Rgb24Canvas(int width, int height, Rgb24Color background)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        Width = width;
        Height = height;
        _pixels = new byte[width * height * 3];
        Fill(background);
    }

    public int Width { get; }

    public int Height { get; }

    public void Fill(Rgb24Color color)
    {
        for (int index = 0; index < _pixels.Length; index += 3)
        {
            _pixels[index] = color.R;
            _pixels[index + 1] = color.G;
            _pixels[index + 2] = color.B;
        }
    }

    public void FillRect(RectD rect, Rgb24Color color)
    {
        int left = Math.Clamp((int)Math.Round(rect.X), 0, Width - 1);
        int top = Math.Clamp((int)Math.Round(rect.Y), 0, Height - 1);
        int right = Math.Clamp((int)Math.Round(rect.Right), 0, Width);
        int bottom = Math.Clamp((int)Math.Round(rect.Bottom), 0, Height);

        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                SetPixel(x, y, color);
            }
        }
    }

    public void FillTriangle(PointD a, PointD b, PointD c, Rgb24Color color)
    {
        double minX = Math.Max(0, Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))));
        double maxX = Math.Min(Width - 1, Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
        double minY = Math.Max(0, Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))));
        double maxY = Math.Min(Height - 1, Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));

        double area = Edge(a, b, c);

        for (int y = (int)minY; y <= (int)maxY; y++)
        {
            for (int x = (int)minX; x <= (int)maxX; x++)
            {
                var point = new PointD(x + 0.5, y + 0.5);
                double w0 = Edge(b, c, point);
                double w1 = Edge(c, a, point);
                double w2 = Edge(a, b, point);

                bool inside = area >= 0
                    ? w0 >= 0 && w1 >= 0 && w2 >= 0
                    : w0 <= 0 && w1 <= 0 && w2 <= 0;

                if (inside)
                {
                    SetPixel(x, y, color);
                }
            }
        }
    }

    public void FillEllipse(double centerX, double centerY, double radiusX, double radiusY, Rgb24Color color)
    {
        if (radiusX <= 0 || radiusY <= 0)
        {
            return;
        }

        int left = Math.Max(0, (int)Math.Floor(centerX - radiusX));
        int right = Math.Min(Width - 1, (int)Math.Ceiling(centerX + radiusX));
        int top = Math.Max(0, (int)Math.Floor(centerY - radiusY));
        int bottom = Math.Min(Height - 1, (int)Math.Ceiling(centerY + radiusY));

        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                double dx = (x + 0.5 - centerX) / radiusX;
                double dy = (y + 0.5 - centerY) / radiusY;

                if ((dx * dx) + (dy * dy) <= 1)
                {
                    SetPixel(x, y, color);
                }
            }
        }
    }

    /// <summary>编码成 8 位 RGB PNG（color type 2），供 <c>IBlobStore</c> 直接写盘。</summary>
    public byte[] EncodePng()
    {
        using var raw = new MemoryStream(_pixels.Length + Height);

        for (int y = 0; y < Height; y++)
        {
            raw.WriteByte(0); // filter: None
            raw.Write(_pixels, y * Width * 3, Width * 3);
        }

        byte[] compressed = ZlibCompress(raw.ToArray());

        using var png = new MemoryStream(compressed.Length + 64);
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)Width);
        WriteBigEndian(header, 4, (uint)Height);
        header[8] = 8;  // bit depth
        header[9] = 2;  // color type: truecolor
        header[10] = 0; // compression
        header[11] = 0; // filter
        header[12] = 0; // interlace

        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed);
        WriteChunk(png, "IEND", []);

        return png.ToArray();
    }

    private void SetPixel(int x, int y, Rgb24Color color)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            return;
        }

        int index = ((y * Width) + x) * 3;
        _pixels[index] = color.R;
        _pixels[index + 1] = color.G;
        _pixels[index + 2] = color.B;
    }

    /// <summary>叉积的 z 分量，用于三角形内外判定（带符号）。</summary>
    private static double Edge(PointD a, PointD b, PointD point)
        => ((b.X - a.X) * (point.Y - a.Y)) - ((b.Y - a.Y) * (point.X - a.X));

    private static byte[] ZlibCompress(byte[] data)
    {
        using var output = new MemoryStream();

        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        stream.Write(length);

        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        uint crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        WriteBigEndian(crcBytes, 0, crc);
        stream.Write(crcBytes);
    }

    private static void WriteBigEndian(Span<byte> target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;

        foreach (byte value in type)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];

        for (uint index = 0; index < 256; index++)
        {
            uint value = index;

            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }
}
