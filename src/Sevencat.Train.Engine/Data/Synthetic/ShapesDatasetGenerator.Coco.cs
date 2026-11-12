using System.Globalization;
using System.Text.Json;

namespace Sevencat.Train.Engine.Data.Synthetic;

/// <summary>COCO 落盘时的一张图（<paramref name="Shapes"/> 是像素 xyxy 框）。</summary>
internal sealed record CocoImageEntry(
    long Id,
    string FileName,
    int Width,
    int Height,
    IReadOnlyList<SyntheticShape> Shapes);

/// <summary>把生成的形状写成 COCO json（RF-DETR / Roboflow 与通用 COCO 共用同一份结构）。</summary>
public static partial class ShapesDatasetGenerator
{
    /// <summary>
    /// 写 COCO 标注：<c>images</c> / <c>annotations</c> / <c>categories</c> 三个数组。
    /// <para>bbox 是绝对像素 <c>[x, y, w, h]</c>；category_id 从 1 开始（COCO 约定），与 YOLO 的 0 基类别差 1。</para>
    /// </summary>
    private static void WriteCocoJson(string path, IReadOnlyList<CocoImageEntry> entries)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();

        writer.WritePropertyName("info");
        writer.WriteStartObject();
        writer.WriteString("description", "Sevencat.Train.Engine synthetic shapes");
        writer.WriteString("version", "1.0");
        writer.WriteEndObject();

        writer.WritePropertyName("licenses");
        writer.WriteStartArray();
        writer.WriteEndArray();

        writer.WritePropertyName("images");
        writer.WriteStartArray();
        foreach (CocoImageEntry entry in entries)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", entry.Id);
            writer.WriteString("file_name", entry.FileName);
            writer.WriteNumber("width", entry.Width);
            writer.WriteNumber("height", entry.Height);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WritePropertyName("annotations");
        writer.WriteStartArray();

        long annotationId = 1;
        foreach (CocoImageEntry entry in entries)
        {
            foreach (SyntheticShape shape in entry.Shapes)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", annotationId++);
                writer.WriteNumber("image_id", entry.Id);
                writer.WriteNumber("category_id", shape.ClassId + 1);
                writer.WriteNumber("iscrowd", 0);

                writer.WritePropertyName("bbox");
                writer.WriteStartArray();
                writer.WriteNumberValue(shape.X1);
                writer.WriteNumberValue(shape.Y1);
                writer.WriteNumberValue(Math.Max(1, shape.Width));
                writer.WriteNumberValue(Math.Max(1, shape.Height));
                writer.WriteEndArray();

                writer.WriteNumber("area", Math.Max(1, shape.Width) * (double)Math.Max(1, shape.Height));
                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();

        writer.WritePropertyName("categories");
        writer.WriteStartArray();
        for (int index = 0; index < ClassNames.Count; index++)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", index + 1);
            writer.WriteString("name", ClassNames[index]);
            writer.WriteString("supercategory", "shape");
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteEndObject();
        writer.Flush();
    }

    /// <summary>COCO json 的默认文件名（RF-DETR / Roboflow 约定）。</summary>
    public const string RoboflowAnnotationFileName = "_annotations.coco.json";
}
