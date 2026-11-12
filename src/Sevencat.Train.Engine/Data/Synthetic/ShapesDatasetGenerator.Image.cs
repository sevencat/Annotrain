using System.Globalization;
using Sevencat.Train.Engine.Core;
using Sevencat.Train.Engine.Data.Images;

namespace Sevencat.Train.Engine.Data.Synthetic;

/// <summary>单图生成、参数校验、data.yaml 与缩略图落盘。</summary>
public static partial class ShapesDatasetGenerator
{
    /// <summary>生成一张图的像素与标签行（YOLO 文本格式；测试里也直接用它做确定性断言）。</summary>
    public static byte[] GenerateImage(
        Rng rng, ShapesDatasetOptions options, int[] objectsPerClass, out List<string> labelLines)
    {
        byte[] pixels = GenerateImage(rng, options, objectsPerClass, out List<SyntheticShape> shapes);
        labelLines = new List<string>(shapes.Count);

        foreach (SyntheticShape shape in shapes)
            labelLines.Add(FormatYoloLabel(shape, options.ImageSize));

        return pixels;
    }

    /// <summary>生成一张图的像素与形状列表（COCO 需要绝对像素框，所以这里返回结构而不是字符串）。</summary>
    public static byte[] GenerateImage(
        Rng rng, ShapesDatasetOptions options, int[] objectsPerClass, out List<SyntheticShape> shapes)
    {
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(objectsPerClass);

        int size = options.ImageSize;
        var pixels = new byte[size * size * 3];

        (byte R, byte G, byte B) background = (
            (byte)rng.NextInt(60), (byte)rng.NextInt(60), (byte)rng.NextInt(60));

        for (int offset = 0; offset < pixels.Length; offset += 3)
        {
            pixels[offset] = background.R;
            pixels[offset + 1] = background.G;
            pixels[offset + 2] = background.B;
        }

        shapes = new List<SyntheticShape>();

        int wanted = options.MinShapes + rng.NextInt(options.MaxShapes - options.MinShapes + 1);

        for (int index = 0; index < wanted; index++)
        {
            int classId = rng.NextInt(ClassNames.Count);
            SyntheticShape? shape = ShapeRasterizer.SampleShape(rng, classId, size, shapes);

            if (shape is null)
                continue;

            SyntheticShape value = shape.Value;
            shapes.Add(value);
            objectsPerClass[classId]++;

            (byte R, byte G, byte B) color = (
                (byte)(150 + rng.NextInt(106)), (byte)(150 + rng.NextInt(106)), (byte)(150 + rng.NextInt(106)));

            ShapeRasterizer.Draw(pixels, size, size, value, color);
        }

        return pixels;
    }

    /// <summary>YOLO 文本标签：<c>cls cx cy w h</c>（归一化，6 位小数）。</summary>
    private static string FormatYoloLabel(SyntheticShape shape, int imageSize)
    {
        double centerX = (shape.X1 + shape.X2) / 2.0 / imageSize;
        double centerY = (shape.Y1 + shape.Y2) / 2.0 / imageSize;
        double boxWidth = shape.Width / (double)imageSize;
        double boxHeight = shape.Height / (double)imageSize;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{shape.ClassId} {centerX:F6} {centerY:F6} {boxWidth:F6} {boxHeight:F6}");
    }

    private static void Validate(ShapesDatasetOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputDirectory);

        if (options.TrainImages < 0 || options.ValImages < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "图片数量不能为负数。");
        if (options.TrainImages == 0 && options.ValImages == 0)
            throw new ArgumentException("至少要有 1 张训练图或验证图。", nameof(options));
        if (options.ImageSize is < 32 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(options), options.ImageSize, "图片边长必须在 [32,4096]。");
        if (options.MinShapes < 1 || options.MaxShapes < options.MinShapes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.MaxShapes, "形状数量需满足 1 <= MinShapes <= MaxShapes。");
        }
    }

    private static string BuildDataYaml()
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("path: .");
        builder.AppendLine("train: images/train");
        builder.AppendLine("val: images/val");
        builder.AppendLine("names:");

        for (int index = 0; index < ClassNames.Count; index++)
            builder.AppendLine($"  {index}: {ClassNames[index]}");

        return builder.ToString();
    }

    /// <summary>把前 16 张图拼成 4×4 缩略图（最近邻降采样），方便快速目检。</summary>
    private static string? WritePreview(IReadOnlyList<byte[]> tiles, string outputPath, int imageSize)
    {
        if (tiles.Count == 0)
            return null;

        int tile = Math.Max(16, imageSize / 4);
        int sheetSize = tile * 4;
        var sheet = new byte[sheetSize * sheetSize * 3];
        Array.Fill(sheet, (byte)255);

        for (int index = 0; index < tiles.Count; index++)
        {
            byte[] pixels = tiles[index];
            int originX = index % 4 * tile;
            int originY = index / 4 * tile;

            for (int y = 0; y < tile; y++)
            {
                int sourceY = Math.Min(imageSize - 1, y * imageSize / tile);

                for (int x = 0; x < tile; x++)
                {
                    int sourceX = Math.Min(imageSize - 1, x * imageSize / tile);
                    int source = ((sourceY * imageSize) + sourceX) * 3;
                    int target = ((originY + y) * sheetSize + originX + x) * 3;

                    sheet[target] = pixels[source];
                    sheet[target + 1] = pixels[source + 1];
                    sheet[target + 2] = pixels[source + 2];
                }
            }
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        ImageCodecs.SaveRgb24(sheet, sheetSize, sheetSize, outputPath);
        return outputPath;
    }
}
