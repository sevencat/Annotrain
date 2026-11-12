using Sevencat.Core.Annotation;
using Sevencat.Core.Annotation.Formats;
using Sevencat.Core.Dataset;
using Sevencat.Core.Model;
using Xunit;

namespace Sevencat.Label.Tests;

public class CodecTests
{
    private static AnnotationReadContext Context(int width = 640, int height = 480) => new(
        new ImageInfo { RelativePath = "images/train/a.jpg", Width = width, Height = height },
        ClassCatalog.FromNames(["cat", "dog"]));

    [Fact]
    public void YoloTxt_RoundTrip_KeepsPixelBox()
    {
        var codec = new YoloTxtCodec();
        var document = new AnnotationDocument
        {
            ImagePath = "images/train/a.jpg",
            ImageWidth = 640,
            ImageHeight = 480,
            Shapes =
            [
                new Shape
                {
                    Id = "1",
                    Type = ShapeType.Rectangle,
                    Points = BoxConvert.ToCorners(RectD.FromLtrb(100, 50, 300, 250)),
                    Label = "cat",
                },
                new Shape
                {
                    Id = "2",
                    Type = ShapeType.Rectangle,
                    Points = BoxConvert.ToCorners(RectD.FromLtrb(400, 300, 600, 460)),
                    Label = "dog",
                },
            ],
        };

        using var buffer = new MemoryStream();
        codec.Write(buffer, document, new AnnotationWriteContext("images/train/a.jpg", 640, 480, ClassCatalog.FromNames(["cat", "dog"])));

        buffer.Position = 0;
        AnnotationParseResult result = codec.Read(buffer, Context());

        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Document.Shapes.Count);

        RectD first = result.Document.Shapes[0].Bounds;
        Assert.Equal(100, first.X, 2);
        Assert.Equal(50, first.Y, 2);
        Assert.Equal(200, first.Width, 2);
        Assert.Equal(200, first.Height, 2);
        Assert.Equal("cat", result.Document.Shapes[0].Label);
    }

    [Fact]
    public void YoloTxt_SkipsUnknownClass_OnWrite()
    {
        var codec = new YoloTxtCodec();
        var document = new AnnotationDocument
        {
            Shapes =
            [
                new Shape
                {
                    Id = "1",
                    Type = ShapeType.Rectangle,
                    Points = BoxConvert.ToCorners(RectD.FromLtrb(10, 10, 50, 50)),
                    Label = "not-in-catalog",
                },
            ],
        };

        using var buffer = new MemoryStream();
        codec.Write(buffer, document, new AnnotationWriteContext("a.jpg", 100, 100, ClassCatalog.FromNames(["cat"])));

        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void YoloTxt_ReportsOutOfRangeClass()
    {
        var codec = new YoloTxtCodec();
        using var stream = new MemoryStream("5 0.5 0.5 0.2 0.2\n"u8.ToArray());

        AnnotationParseResult result = codec.Read(stream, Context());

        Assert.Single(result.Document.Shapes);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Labelme_RoundTrip_KeepsMetaAndShapes()
    {
        var codec = new LabelmeCodec();
        var document = new AnnotationDocument
        {
            ImagePath = "a.jpg",
            ImageWidth = 100,
            ImageHeight = 80,
            Shapes =
            [
                new Shape
                {
                    Id = "s1",
                    Type = ShapeType.Rectangle,
                    Points = BoxConvert.ToCorners(RectD.FromLtrb(1, 2, 30, 40)),
                    Label = "cat",
                },
            ],
            Meta = new DocumentMeta { Id = "doc-1", Revision = 3 },
        };

        using var buffer = new MemoryStream();
        codec.Write(buffer, document, new AnnotationWriteContext("a.jpg", 100, 80, ClassCatalog.FromNames(["cat"])));

        buffer.Position = 0;
        AnnotationParseResult result = codec.Read(buffer, Context(100, 80));

        Assert.Equal("doc-1", result.Document.Meta.Id);
        Assert.Equal(3, result.Document.Meta.Revision);
        Assert.Equal("cat", Assert.Single(result.Document.Shapes).Label);
    }

    [Fact]
    public void Labelme_WritesAnnotrainNamespace()
    {
        var codec = new LabelmeCodec();
        var document = new AnnotationDocument { ImagePath = "a.jpg", ImageWidth = 10, ImageHeight = 10 };

        using var buffer = new MemoryStream();
        codec.Write(buffer, document, new AnnotationWriteContext("a.jpg", 10, 10, ClassCatalog.Empty));

        string json = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        Assert.Contains("\"annotrain\"", json);
        Assert.DoesNotContain("\"tinylabel\"", json);
    }

    [Fact]
    public void Coco_RoundTrip_KeepsBoxesAndClasses()
    {
        var codec = new CocoDatasetCodec();
        var annotations = new DatasetAnnotations
        {
            Classes = ClassCatalog.FromNames(["cat", "dog"]),
            Images = new Dictionary<string, AnnotationDocument>(StringComparer.OrdinalIgnoreCase)
            {
                ["a.jpg"] = new AnnotationDocument
                {
                    ImagePath = "a.jpg",
                    ImageWidth = 200,
                    ImageHeight = 100,
                    Shapes =
                    [
                        new Shape
                        {
                            Id = "1",
                            Type = ShapeType.Rectangle,
                            Points = BoxConvert.ToCorners(RectD.FromLtrb(10, 20, 60, 80)),
                            Label = "dog",
                        },
                    ],
                },
            },
        };

        string json = codec.Write(annotations);
        DatasetAnnotations result = codec.Read(json);

        Assert.Equal(2, result.Classes.Count);
        Assert.True(result.Images.ContainsKey("a.jpg"));

        Shape shape = Assert.Single(result.Images["a.jpg"].Shapes);
        Assert.Equal("dog", shape.Label);
        Assert.Equal(10, shape.Bounds.X, 3);
        Assert.Equal(50, shape.Bounds.Width, 3);
    }

    [Fact]
    public void YoloDataConfig_ParsesNamesMapAndSplits()
    {
        const string yaml = """
                            path: .
                            train: images/train
                            val: images/val
                            names:
                              0: cat
                              1: dog
                            """;

        YoloDataConfig config = YoloDataConfig.Parse(yaml);

        Assert.True(config.IsValid);
        Assert.Equal(["cat", "dog"], config.Names);
        Assert.Equal("images/train", config.Train);
    }

    [Fact]
    public void YoloDataConfig_ParsesInlineNamesList()
    {
        const string yaml = """
                            train: images/train
                            val: images/val
                            names: ['a', 'b', 'c']
                            """;

        YoloDataConfig config = YoloDataConfig.Parse(yaml);

        Assert.Equal(["a", "b", "c"], config.Names);
    }
}
