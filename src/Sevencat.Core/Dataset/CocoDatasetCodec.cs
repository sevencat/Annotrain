using System.Text.Json;
using System.Text.Json.Serialization;
using Sevencat.Core.Annotation;
using Sevencat.Core.Localization;
using Sevencat.Core.Model;

namespace Sevencat.Core.Dataset;

/// <summary>
/// COCO / Roboflow 数据集级标注（RF-DETR 的输入格式）：一个 json 描述全部图片与框。
/// bbox 为绝对像素 <c>[x, y, w, h]</c>；写回时 categories 从 1 开始编号。
/// </summary>
public sealed class CocoDatasetCodec : IDatasetAnnotationCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    public string Id => "coco";

    public DatasetAnnotations Read(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException(Text.Get("Error.CocoFileEmpty"));
        }

        CocoFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<CocoFileDto>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(Text.Get("Error.CocoInvalidJson"), ex);
        }

        if (dto is null)
        {
            throw new InvalidDataException(Text.Get("Error.CocoFileNull"));
        }

        var warnings = new List<string>();

        // categoryId → 顺序下标（0 起），与 YOLO 的 classId 语义对齐
        List<CocoCategoryDto> categories = dto.Categories ?? [];
        var classIndex = new Dictionary<long, int>(categories.Count);
        var names = new List<string>(categories.Count);

        for (int i = 0; i < categories.Count; i++)
        {
            classIndex[categories[i].Id] = i;
            names.Add(categories[i].Name ?? $"class-{categories[i].Id}");
        }

        var images = new Dictionary<long, CocoImageDto>(dto.Images?.Count ?? 0);
        foreach (CocoImageDto image in dto.Images ?? [])
        {
            images[image.Id] = image;
        }

        var perImage = new Dictionary<long, List<Shape>>(images.Count);

        foreach (CocoAnnotationDto annotation in dto.Annotations ?? [])
        {
            if (!images.TryGetValue(annotation.ImageId, out CocoImageDto? image))
            {
                warnings.Add($"标注 {annotation.Id} 指向不存在的图片 {annotation.ImageId}，已跳过");
                continue;
            }

            if (!classIndex.TryGetValue(annotation.CategoryId, out int classId))
            {
                warnings.Add($"标注 {annotation.Id} 的类别 {annotation.CategoryId} 不在 categories 中，已跳过");
                continue;
            }

            if (annotation.Bbox is not { Length: >= 4 })
            {
                warnings.Add($"标注 {annotation.Id} 的 bbox 不完整，已跳过");
                continue;
            }

            RectD rect = BoxConvert.Clamp(
                BoxConvert.FromXywh(annotation.Bbox[0], annotation.Bbox[1], annotation.Bbox[2], annotation.Bbox[3]),
                image.Width,
                image.Height);

            if (rect.IsEmpty)
            {
                continue;
            }

            if (!perImage.TryGetValue(image.Id, out List<Shape>? shapes))
            {
                shapes = [];
                perImage[image.Id] = shapes;
            }

            shapes.Add(new Shape
            {
                Id = IdGenerator.NewId(),
                Type = ShapeType.Rectangle,
                Points = BoxConvert.ToCorners(rect),
                Label = names[classId],
            });
        }

        var docs = new Dictionary<string, AnnotationDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (CocoImageDto image in images.Values)
        {
            string relative = NormalizeRelative(image.FileName);
            docs[relative] = new AnnotationDocument
            {
                ImagePath = relative,
                ImageWidth = image.Width,
                ImageHeight = image.Height,
                Shapes = perImage.TryGetValue(image.Id, out List<Shape>? shapes) ? shapes : [],
            };
        }

        return new DatasetAnnotations
        {
            Classes = ClassCatalog.FromNames(names),
            Images = docs,
            Warnings = warnings,
        };
    }

    public string Write(DatasetAnnotations annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);

        var categories = annotations.Classes.Names
            .Select((name, index) => new CocoCategoryDto
            {
                Id = index + 1,
                Name = name,
                Supercategory = "none",
            })
            .ToList();

        var images = new List<CocoImageDto>();
        var boxes = new List<CocoAnnotationDto>();
        long imageId = 0;
        long annotationId = 0;

        foreach ((string relative, AnnotationDocument document) in annotations.Images.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            imageId++;
            images.Add(new CocoImageDto
            {
                Id = imageId,
                FileName = NormalizeRelative(relative),
                Width = document.ImageWidth,
                Height = document.ImageHeight,
            });

            foreach (Shape shape in document.Shapes)
            {
                if (!shape.IsClosed)
                {
                    continue;
                }

                int classId = annotations.Classes.IndexOf(shape.Label);
                if (classId < 0)
                {
                    continue;
                }

                RectD rect = BoxConvert.Clamp(shape.Bounds, document.ImageWidth, document.ImageHeight);
                if (rect.IsEmpty)
                {
                    continue;
                }

                annotationId++;
                boxes.Add(new CocoAnnotationDto
                {
                    Id = annotationId,
                    ImageId = imageId,
                    CategoryId = classId + 1,
                    Bbox = [rect.X, rect.Y, rect.Width, rect.Height],
                    Area = rect.Area,
                    Iscrowd = 0,
                });
            }
        }

        var dto = new CocoFileDto
        {
            Images = images,
            Annotations = boxes,
            Categories = categories,
        };

        return JsonSerializer.Serialize(dto, Options);
    }

    /// <summary>COCO 里可能带 <c>train/xxx.jpg</c>，统一成正斜杠相对路径。</summary>
    private static string NormalizeRelative(string? path)
        => (path ?? string.Empty).Replace('\\', '/').TrimStart('/');

    private sealed class CocoFileDto
    {
        [JsonPropertyName("images")] public List<CocoImageDto>? Images { get; set; }

        [JsonPropertyName("annotations")] public List<CocoAnnotationDto>? Annotations { get; set; }

        [JsonPropertyName("categories")] public List<CocoCategoryDto>? Categories { get; set; }
    }

    private sealed class CocoImageDto
    {
        [JsonPropertyName("id")] public long Id { get; set; }

        [JsonPropertyName("file_name")] public string? FileName { get; set; }

        [JsonPropertyName("width")] public int Width { get; set; }

        [JsonPropertyName("height")] public int Height { get; set; }
    }

    private sealed class CocoAnnotationDto
    {
        [JsonPropertyName("id")] public long Id { get; set; }

        [JsonPropertyName("image_id")] public long ImageId { get; set; }

        [JsonPropertyName("category_id")] public long CategoryId { get; set; }

        [JsonPropertyName("bbox")] public double[]? Bbox { get; set; }

        [JsonPropertyName("area")] public double Area { get; set; }

        [JsonPropertyName("iscrowd")] public int Iscrowd { get; set; }
    }

    private sealed class CocoCategoryDto
    {
        [JsonPropertyName("id")] public long Id { get; set; }

        [JsonPropertyName("name")] public string? Name { get; set; }

        [JsonPropertyName("supercategory")] public string? Supercategory { get; set; }
    }
}
