using System.Globalization;
using System.Text;
using Sevencat.Core.Annotation;
using Sevencat.Core.Localization;
using Sevencat.Core.Model;

namespace Sevencat.Core.Annotation.Formats;

/// <summary>
/// YOLO 标签文件：每行 <c>class cx cy w h</c>（归一化）。只支持检测框；
/// 分割（多边形）与 OBB 按字段数识别后拒绝写入，避免破坏原文件。
/// </summary>
public sealed class YoloTxtCodec : IAnnotationCodec
{
    public string Id => "yolo";

    public IReadOnlyCollection<string> Extensions { get; } = [".txt"];

    public AnnotationParseResult Read(Stream stream, AnnotationReadContext context)
    {
        var warnings = new List<string>();
        var shapes = new List<Shape>();

        if (context.Image.Width <= 0 || context.Image.Height <= 0)
        {
            return new AnnotationParseResult(
                new AnnotationDocument { ImagePath = context.Image.RelativePath },
                ["图片尺寸未知，无法换算 YOLO 归一化坐标"]);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        int lineNumber = 0;

        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            line = line.Trim();

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5)
            {
                warnings.Add($"第 {lineNumber} 行字段数为 {parts.Length}，不是检测框（需要 5 列），已跳过");
                continue;
            }

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int classId))
            {
                warnings.Add($"第 {lineNumber} 行类别不是整数，已跳过");
                continue;
            }

            if (!TryParse(parts[1], out double cx) || !TryParse(parts[2], out double cy)
                || !TryParse(parts[3], out double w) || !TryParse(parts[4], out double h))
            {
                warnings.Add($"第 {lineNumber} 行坐标不是数字，已跳过");
                continue;
            }

            if (w <= 0 || h <= 0)
            {
                warnings.Add($"第 {lineNumber} 行宽高非正，已跳过");
                continue;
            }

            RectD rect = BoxConvert.Clamp(
                BoxConvert.FromYolo(cx, cy, w, h, context.Image.Width, context.Image.Height),
                context.Image.Width,
                context.Image.Height);

            if (rect.IsEmpty)
            {
                warnings.Add($"第 {lineNumber} 行框完全在图外，已跳过");
                continue;
            }

            string label = context.Classes.NameOf(classId);
            if (label.Length == 0)
            {
                warnings.Add($"第 {lineNumber} 行类别 {classId} 不在类别表中（共 {context.Classes.Count} 类）");
            }

            shapes.Add(new Shape
            {
                Id = IdGenerator.NewId(),
                Type = ShapeType.Rectangle,
                Points = BoxConvert.ToCorners(rect),
                Label = label,
            });
        }

        var document = new AnnotationDocument
        {
            ImagePath = context.Image.RelativePath,
            ImageWidth = context.Image.Width,
            ImageHeight = context.Image.Height,
            Shapes = shapes,
        };

        return new AnnotationParseResult(document, warnings);
    }

    public void Write(Stream stream, AnnotationDocument document, AnnotationWriteContext context)
    {
        if (context.ImageWidth <= 0 || context.ImageHeight <= 0)
        {
            throw new InvalidOperationException(Text.Get("Error.ImageSizeUnknown"));
        }

        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { NewLine = "\n" };

        foreach (Shape shape in document.Shapes)
        {
            if (!shape.IsClosed)
            {
                continue;
            }

            int classId = context.Classes.IndexOf(shape.Label);
            if (classId < 0)
            {
                // 未知类别不写入：宁可少一个框，也不写 classId = -1 破坏数据集
                continue;
            }

            RectD rect = BoxConvert.Clamp(shape.Bounds, context.ImageWidth, context.ImageHeight);
            if (rect.IsEmpty)
            {
                continue;
            }

            (double cx, double cy, double w, double h) = BoxConvert.ToYolo(rect, context.ImageWidth, context.ImageHeight);
            writer.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{classId} {cx:F6} {cy:F6} {w:F6} {h:F6}"));
        }
    }

    private static bool TryParse(string value, out double result)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
}
