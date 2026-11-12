using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sevencat.Core.Annotation;
using Sevencat.Core.Localization;
using Sevencat.Core.Model;

namespace Sevencat.Core.Annotation.Formats;

/// <summary>
/// labelme JSON 读写。文件对 labelme 完全兼容；本项目额外信息集中在 <c>annotrain</c> 字段，
/// labelme 打开时会忽略未知字段。移植自 TinyLabel 的 LabelmeSerializer。
/// </summary>
public sealed class LabelmeCodec : IAnnotationCodec
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string Id => "labelme";

    public IReadOnlyCollection<string> Extensions { get; } = [".json"];

    public AnnotationParseResult Read(Stream stream, AnnotationReadContext context)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return Deserialize(reader.ReadToEnd(), context.Image.RelativePath);
    }

    public void Write(Stream stream, AnnotationDocument document, AnnotationWriteContext context)
    {
        string json = Serialize(document);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true);
        writer.Write(json);
    }

    public static string Serialize(AnnotationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var dto = new LabelmeFileDto
        {
            Version = string.IsNullOrWhiteSpace(document.Version) ? "5.0.1" : document.Version,
            ImagePath = document.ImagePath,
            ImageWidth = document.ImageWidth,
            ImageHeight = document.ImageHeight,
            Flags = document.Flags.Count == 0 ? null : new Dictionary<string, bool>(document.Flags),
            Shapes = document.Shapes.Select(ToDto).ToList(),
            Annotrain = new AnnotrainDto
            {
                Id = document.Meta.Id,
                Revision = document.Meta.Revision,
                UpdatedAt = document.Meta.UpdatedAt?.ToUniversalTime().ToString("O"),
                UpdatedBy = document.Meta.UpdatedBy,
                Review = document.Review is { } review
                    ? new ReviewDto
                    {
                        State = ToStateString(review.State),
                        Comment = review.Comment,
                        Reviewer = review.Reviewer,
                        ReviewedAt = review.ReviewedAt?.ToUniversalTime().ToString("O"),
                    }
                    : null,
            },
        };

        return JsonSerializer.Serialize(dto, Options);
    }

    public static AnnotationParseResult Deserialize(string json, string? fallbackImagePath = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException(Text.Get("Error.LabelFileEmpty"));
        }

        LabelmeFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<LabelmeFileDto>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(Text.Get("Error.LabelFileInvalidJson"), ex);
        }

        if (dto is null)
        {
            throw new InvalidDataException(Text.Get("Error.LabelFileNull"));
        }

        var warnings = new List<string>();
        var shapes = new List<Shape>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (LabelmeShapeDto shapeDto in dto.Shapes ?? [])
        {
            ShapeType type = ShapeTypes.Parse(shapeDto.ShapeType);
            if (type == ShapeType.Unknown)
            {
                warnings.Add($"未知形状类型 '{shapeDto.ShapeType}'，已按只读方式保留");
            }

            var points = new List<PointD>();
            foreach (double[] pair in shapeDto.Points ?? [])
            {
                if (pair is { Length: >= 2 })
                {
                    points.Add(new PointD(pair[0], pair[1]));
                }
                else
                {
                    warnings.Add("忽略一个坐标不完整的点");
                }
            }

            if (points.Count == 0 && type != ShapeType.Unknown)
            {
                warnings.Add("忽略一个没有坐标的形状");
                continue;
            }

            string id = string.IsNullOrWhiteSpace(shapeDto.Id) ? IdGenerator.NewId() : shapeDto.Id;
            while (!usedIds.Add(id))
            {
                id = IdGenerator.NewId();
            }

            shapes.Add(new Shape
            {
                Id = id,
                Type = type,
                Points = points,
                Label = shapeDto.Label ?? string.Empty,
                GroupId = shapeDto.GroupId,
                Description = string.IsNullOrEmpty(shapeDto.Description) ? null : shapeDto.Description,
                Flags = shapeDto.Flags is { Count: > 0 }
                    ? new Dictionary<string, bool>(shapeDto.Flags)
                    : new Dictionary<string, bool>(0),
                MaskBase64 = shapeDto.Mask,
            });
        }

        var meta = new DocumentMeta
        {
            Id = string.IsNullOrWhiteSpace(dto.Annotrain?.Id) ? IdGenerator.NewId() : dto.Annotrain.Id,
            Revision = Math.Max(1, dto.Annotrain?.Revision ?? 1),
            UpdatedBy = dto.Annotrain?.UpdatedBy ?? "local",
            UpdatedAt = ParseTime(dto.Annotrain?.UpdatedAt),
        };

        var document = new AnnotationDocument
        {
            Version = string.IsNullOrWhiteSpace(dto.Version) ? "5.0.1" : dto.Version,
            ImagePath = string.IsNullOrWhiteSpace(dto.ImagePath) ? fallbackImagePath ?? string.Empty : dto.ImagePath,
            ImageData = dto.ImageData,
            ImageWidth = dto.ImageWidth,
            ImageHeight = dto.ImageHeight,
            Flags = dto.Flags ?? new Dictionary<string, bool>(0),
            Shapes = shapes,
            Review = dto.Annotrain?.Review is { } review
                ? new ReviewInfo
                {
                    State = ParseState(review.State),
                    Comment = review.Comment,
                    Reviewer = review.Reviewer,
                    ReviewedAt = ParseTime(review.ReviewedAt),
                }
                : null,
            Meta = meta,
        };

        return new AnnotationParseResult(document, warnings);
    }

    private static LabelmeShapeDto ToDto(Shape shape) => new()
    {
        Label = shape.Label,
        ShapeType = ShapeTypes.ToLabelme(shape.Type),
        Points = shape.Points.Select(p => new[] { Math.Round(p.X, 2), Math.Round(p.Y, 2) }).ToList(),
        GroupId = shape.GroupId,
        Description = shape.Description,
        Flags = shape.Flags.Count == 0 ? null : new Dictionary<string, bool>(shape.Flags),
        Id = shape.Id,
        Mask = shape.MaskBase64,
    };

    private static DateTimeOffset? ParseTime(string? value)
        => DateTimeOffset.TryParse(value, out DateTimeOffset parsed) ? parsed : null;

    private static string ToStateString(ReviewState state) => state switch
    {
        ReviewState.Draft => "draft",
        ReviewState.Submitted => "submitted",
        ReviewState.Accepted => "accepted",
        ReviewState.Rejected => "rejected",
        _ => "none",
    };

    private static ReviewState ParseState(string? value) => value?.ToLowerInvariant() switch
    {
        "draft" => ReviewState.Draft,
        "submitted" => ReviewState.Submitted,
        "accepted" => ReviewState.Accepted,
        "rejected" => ReviewState.Rejected,
        _ => ReviewState.None,
    };

    private sealed class LabelmeFileDto
    {
        [JsonPropertyName("version")] public string? Version { get; set; }

        [JsonPropertyName("flags")] public Dictionary<string, bool>? Flags { get; set; }

        [JsonPropertyName("shapes")] public List<LabelmeShapeDto>? Shapes { get; set; }

        [JsonPropertyName("imagePath")] public string? ImagePath { get; set; }

        [JsonPropertyName("imageData")] public string? ImageData { get; set; }

        [JsonPropertyName("imageHeight")] public int ImageHeight { get; set; }

        [JsonPropertyName("imageWidth")] public int ImageWidth { get; set; }

        [JsonPropertyName("annotrain")] public AnnotrainDto? Annotrain { get; set; }
    }

    private sealed class LabelmeShapeDto
    {
        [JsonPropertyName("label")] public string? Label { get; set; }

        [JsonPropertyName("points")] public List<double[]>? Points { get; set; }

        [JsonPropertyName("group_id")] public int? GroupId { get; set; }

        [JsonPropertyName("description")] public string? Description { get; set; }

        [JsonPropertyName("shape_type")] public string? ShapeType { get; set; }

        [JsonPropertyName("flags")] public Dictionary<string, bool>? Flags { get; set; }

        [JsonPropertyName("id")] public string? Id { get; set; }

        [JsonPropertyName("mask")] public string? Mask { get; set; }
    }

    private sealed class AnnotrainDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }

        [JsonPropertyName("revision")] public int Revision { get; set; }

        [JsonPropertyName("updatedAt")] public string? UpdatedAt { get; set; }

        [JsonPropertyName("updatedBy")] public string? UpdatedBy { get; set; }

        [JsonPropertyName("review")] public ReviewDto? Review { get; set; }
    }

    private sealed class ReviewDto
    {
        [JsonPropertyName("state")] public string? State { get; set; }

        [JsonPropertyName("comment")] public string? Comment { get; set; }

        [JsonPropertyName("reviewer")] public string? Reviewer { get; set; }

        [JsonPropertyName("reviewedAt")] public string? ReviewedAt { get; set; }
    }
}
