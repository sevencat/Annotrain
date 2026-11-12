namespace Sevencat.Core.Model;

/// <summary>审核状态（首版只在本地标记，服务端驱动留到 Web 端）。</summary>
public enum ReviewState
{
    None,
    Draft,
    Submitted,
    Accepted,
    Rejected,
}

public sealed record ReviewInfo
{
    public ReviewState State { get; init; } = ReviewState.None;

    public string? Comment { get; init; }

    public string? Reviewer { get; init; }

    public DateTimeOffset? ReviewedAt { get; init; }
}

/// <summary>文档自身元数据，写进标注 JSON 的 <c>annotrain</c> 字段。</summary>
public sealed record DocumentMeta
{
    public required string Id { get; init; }

    /// <summary>从 1 开始，每次保存递增；用于乐观锁。</summary>
    public int Revision { get; init; } = 1;

    public DateTimeOffset? UpdatedAt { get; init; }

    public string UpdatedBy { get; init; } = "local";
}

/// <summary>单张图片的标注文档。与 labelme JSON 一一对应，额外信息放 <c>annotrain</c> 命名空间。</summary>
public sealed record AnnotationDocument
{
    /// <summary>图片相对路径（相对数据集根）。</summary>
    public string ImagePath { get; init; } = string.Empty;

    public int ImageWidth { get; init; }

    public int ImageHeight { get; init; }

    /// <summary>可选的内嵌图片（base64），默认不写入。</summary>
    public string? ImageData { get; init; }

    public IReadOnlyList<Shape> Shapes { get; init; } = [];

    public IReadOnlyDictionary<string, bool> Flags { get; init; } = new Dictionary<string, bool>(0);

    public string Version { get; init; } = "5.0.1";

    public ReviewInfo? Review { get; init; }

    public DocumentMeta Meta { get; init; } = new() { Id = IdGenerator.NewId() };

    public AnnotationDocument WithShapes(IReadOnlyList<Shape> shapes) => this with { Shapes = shapes };

    public static AnnotationDocument Empty(ImageInfo info) => new()
    {
        ImagePath = info.FileName,
        ImageWidth = info.Width,
        ImageHeight = info.Height,
    };
}
