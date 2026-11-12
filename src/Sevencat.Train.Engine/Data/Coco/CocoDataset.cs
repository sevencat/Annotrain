using System.Text.Json;

namespace Sevencat.Train.Engine.Data.Coco;

/// <summary>COCO 图片条目。</summary>
public sealed record CocoImage(long Id, string FileName, int Width, int Height);

/// <summary>COCO 标注（bbox 为绝对像素 <c>[x, y, w, h]</c>）。</summary>
public sealed record CocoAnnotation(
    long Id,
    long ImageId,
    long CategoryId,
    float X,
    float Y,
    float Width,
    float Height,
    bool IsCrowd);

/// <summary>COCO 类别。</summary>
public sealed record CocoCategory(long Id, string Name);

/// <summary>
/// COCO / Roboflow 风格数据集（RF-DETR 的输入格式）。
/// <para>只解析 JSON 结构，不读图片像素。</para>
/// </summary>
public sealed class CocoDataset
{
    private CocoDataset(
        string jsonPath,
        IReadOnlyList<CocoImage> images,
        IReadOnlyList<CocoAnnotation> annotations,
        IReadOnlyList<CocoCategory> categories)
    {
        JsonPath = jsonPath;
        Images = images;
        Annotations = annotations;
        Categories = categories;
    }

    public string JsonPath { get; }

    public IReadOnlyList<CocoImage> Images { get; }

    public IReadOnlyList<CocoAnnotation> Annotations { get; }

    public IReadOnlyList<CocoCategory> Categories { get; }

    /// <summary>类别 id → 名称。</summary>
    public IReadOnlyDictionary<long, string> ClassNames =>
        Categories.ToDictionary(category => category.Id, category => category.Name);

    /// <summary>读取 COCO 标注文件。</summary>
    public static CocoDataset Load(string jsonPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);

        string fullPath = Path.GetFullPath(jsonPath);
        using FileStream stream = File.OpenRead(fullPath);
        using JsonDocument document = JsonDocument.Parse(stream);

        JsonElement root = document.RootElement;
        var images = new List<CocoImage>();
        var annotations = new List<CocoAnnotation>();
        var categories = new List<CocoCategory>();

        if (root.TryGetProperty("images", out JsonElement imagesElement))
        {
            foreach (JsonElement item in imagesElement.EnumerateArray())
            {
                images.Add(new CocoImage(
                    item.GetProperty("id").GetInt64(),
                    RequireString(item, "file_name", fullPath),
                    RequireInt(item, "width", fullPath),
                    RequireInt(item, "height", fullPath)));
            }
        }

        if (root.TryGetProperty("categories", out JsonElement categoriesElement))
        {
            foreach (JsonElement item in categoriesElement.EnumerateArray())
            {
                categories.Add(new CocoCategory(
                    item.GetProperty("id").GetInt64(),
                    RequireString(item, "name", fullPath)));
            }
        }

        if (root.TryGetProperty("annotations", out JsonElement annotationsElement))
        {
            foreach (JsonElement item in annotationsElement.EnumerateArray())
            {
                JsonElement bbox = item.TryGetProperty("bbox", out JsonElement bboxElement)
                    ? bboxElement
                    : throw new InvalidDataException($"{fullPath}：标注缺少 bbox。");

                if (bbox.GetArrayLength() != 4)
                    throw new InvalidDataException($"{fullPath}：bbox 必须是 [x, y, w, h]。");

                annotations.Add(new CocoAnnotation(
                    item.GetProperty("id").GetInt64(),
                    item.GetProperty("image_id").GetInt64(),
                    item.GetProperty("category_id").GetInt64(),
                    bbox[0].GetSingle(),
                    bbox[1].GetSingle(),
                    bbox[2].GetSingle(),
                    bbox[3].GetSingle(),
                    item.TryGetProperty("iscrowd", out JsonElement crowd) && crowd.GetInt32() != 0));
            }
        }

        if (images.Count == 0)
            throw new InvalidDataException($"{fullPath}：images 为空。");
        if (categories.Count == 0)
            throw new InvalidDataException($"{fullPath}：categories 为空。");

        return new CocoDataset(fullPath, images, annotations, categories);
    }

    /// <summary>把某张图的标注转成归一化 <c>[x, y, w, h]</c>（YOLO 风格）。</summary>
    public static (float ClassId, float X, float Y, float Width, float Height) ToYolo(
        CocoAnnotation annotation,
        CocoImage image,
        IReadOnlyDictionary<long, int> categoryToIndex)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(categoryToIndex);

        if (!categoryToIndex.TryGetValue(annotation.CategoryId, out int classIndex))
        {
            throw new KeyNotFoundException(
                $"类别 id {annotation.CategoryId} 不在 categoryToIndex 里。");
        }

        float width = annotation.Width / image.Width;
        float height = annotation.Height / image.Height;
        float x = annotation.X / image.Width + width / 2f;
        float y = annotation.Y / image.Height + height / 2f;

        return (classIndex, x, y, width, height);
    }

    private static string RequireString(JsonElement item, string property, string path)
    {
        if (!item.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{path}：缺少字符串字段 '{property}'。");
        return value.GetString() ?? string.Empty;
    }

    private static int RequireInt(JsonElement item, string property, string path)
    {
        if (!item.TryGetProperty(property, out JsonElement value) || !value.TryGetInt32(out int result))
            throw new InvalidDataException($"{path}：缺少整数字段 '{property}'。");

        return result;
    }
}
