using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Sevencat.Core.Annotation;
using Sevencat.Core.Dataset;
using Sevencat.Core.Dataset.Export;
using Sevencat.Core.Dataset.Import;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Model;
using Sevencat.Core.Simulation;
using Sevencat.Core.Storage;
using Sevencat.Label;
using Xunit;

namespace Sevencat.Label.Tests;

public class DatasetImportTests
{
    [Fact]
    public async Task Imports_YoloDataset_WithImagesLabelsAndSplits()
    {
        using var source = new TestWorkspace();
        using var target = new TestWorkspace();

        int expectedBoxes = WriteYoloDataset(source);

        using ServiceProvider provider = BuildProvider(target.OpenDatabase());
        var importer = provider.GetRequiredService<DatasetImportService>();

        DatasetImportReport report = await importer.ImportAsync(new DatasetImportOptions(
            StorageLocation.FromPath(source.Root),
            StorageLocation.FromPath(target.Root),
            ProjectName: "导入项目"));

        Assert.Equal(DatasetFormat.Yolo, report.SourceFormat);
        Assert.Equal(4, report.ImagesImported);
        Assert.Equal(expectedBoxes, report.BoxesImported);
        Assert.Equal(0, report.ImagesSkipped);
        Assert.Equal(0, report.BoxesDropped);
        Assert.Equal(new[] { "train", "val" }, report.Splits);
        Assert.Equal(2, report.ImagesPerSplit["train"]);
        Assert.Equal(2, report.ImagesPerSplit["val"]);
        Assert.Equal(new[] { "square", "triangle", "circle" }, report.Classes);
        Assert.True(File.Exists(report.DatabasePath));

        // 导入结果必须是一个能直接打开的标注项目
        var resolver = provider.GetRequiredService<DatasetStoreResolver>();
        DatasetOpenResult opened = await resolver.OpenAsync(StorageLocation.FromPath(target.Root));

        try
        {
            Assert.Equal(DatasetFormat.TinyLabelDb, opened.Store.Format);
            Assert.Equal("导入项目", ((Sevencat.Core.Dataset.TinyLabel.TinyLabelDatasetStore)opened.Store).ProjectName);

            IReadOnlyList<DatasetSample> train = await opened.Store.OpenSplit("train").LoadSamplesAsync();
            IReadOnlyList<DatasetSample> val = await opened.Store.OpenSplit("val").LoadSamplesAsync();

            Assert.Equal(2, train.Count);
            Assert.Equal(2, val.Count);
            Assert.Equal(expectedBoxes, train.Sum(sample => sample.Boxes.Count) + val.Sum(sample => sample.Boxes.Count));
            Assert.All(train.Concat(val), sample => Assert.Equal(96, sample.Width));
        }
        finally
        {
            (opened.Store as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public async Task Imports_RfDetrCoco_AndMapsValidToVal()
    {
        using var source = new TestWorkspace();
        using var target = new TestWorkspace();

        WriteCocoDataset(source);

        using ServiceProvider provider = BuildProvider(target.OpenDatabase());
        var importer = provider.GetRequiredService<DatasetImportService>();

        DatasetImportReport report = await importer.ImportAsync(new DatasetImportOptions(
            StorageLocation.FromPath(source.Root),
            StorageLocation.FromPath(target.Root)));

        Assert.Equal(DatasetFormat.Coco, report.SourceFormat);
        Assert.Equal(2, report.ImagesImported);
        Assert.Equal(2, report.BoxesImported);
        Assert.Equal(new[] { "train", "val" }, report.Splits);
        Assert.Equal(1, report.ImagesPerSplit["train"]);
        Assert.Equal(1, report.ImagesPerSplit["val"]);
        Assert.Equal(new[] { "cat", "dog" }, report.Classes);
    }

    [Fact]
    public async Task Export_Then_Import_KeepsImagesBoxesAndSplits()
    {
        using var workspace = new TestWorkspace();
        using var export = new TestWorkspace();
        using var reopened = new TestWorkspace();

        WriteDbProject(workspace);

        using ServiceProvider provider = BuildProvider(workspace.OpenDatabase());
        var resolver = provider.GetRequiredService<DatasetStoreResolver>();
        var exportService = provider.GetRequiredService<DatasetExportService>();

        DatasetOpenResult opened = await resolver.OpenAsync(StorageLocation.FromPath(workspace.Root));

        try
        {
            await exportService.ExportAsync(
                opened.Store,
                new DatasetExportOptions(DatasetFormat.Yolo, StorageLocation.FromPath(export.Root)));
        }
        finally
        {
            (opened.Store as IDisposable)?.Dispose();
        }

        // 导入写的是当前库：换个库（reopened 目录）就换套容器
        using ServiceProvider importProvider = BuildProvider(reopened.OpenDatabase());
        var importer = importProvider.GetRequiredService<DatasetImportService>();

        DatasetImportReport report = await importer.ImportAsync(new DatasetImportOptions(
            StorageLocation.FromPath(export.Root),
            StorageLocation.FromPath(reopened.Root)));

        Assert.Equal(2, report.ImagesImported);
        Assert.Equal(1, report.BoxesImported);
        Assert.Equal(new[] { "train", "val" }, report.Splits);
        Assert.Equal(new[] { "cat", "dog" }, report.Classes);
        Assert.Equal(1, report.AnnotatedImages);
    }

    [Fact]
    public async Task Browser_ListsImagesAndBoxesOfImportedProject()
    {
        using var source = new TestWorkspace();
        using var target = new TestWorkspace();

        int expectedBoxes = WriteYoloDataset(source);

        using ServiceProvider provider = BuildProvider(target.OpenDatabase());
        var importer = provider.GetRequiredService<DatasetImportService>();

        await importer.ImportAsync(new DatasetImportOptions(
            StorageLocation.FromPath(source.Root),
            StorageLocation.FromPath(target.Root),
            ProjectName: "浏览项目"));

        var browser = provider.GetRequiredService<LabelProjectBrowser>();
        OpenedProject project = await browser.OpenAsync(target.Root);

        Assert.Equal("浏览项目", project.ProjectName);
        Assert.Equal(new[] { "train", "val" }, project.Splits);
        Assert.Equal(4, project.Images.Count);
        Assert.Equal(expectedBoxes, project.BoxCount);
        Assert.Equal(2, project.OfSplit("val").Count);
        Assert.All(project.Images, image => Assert.Equal(96, image.Sample.Width));
    }

    [Fact]
    public async Task Catalog_ListsEveryProjectInWorkspace()
    {
        using var source = new TestWorkspace();
        using var target = new TestWorkspace();

        WriteYoloDataset(source);

        using ServiceProvider provider = BuildProvider(target.OpenDatabase());
        var importer = provider.GetRequiredService<DatasetImportService>();
        var catalog = provider.GetRequiredService<TinyLabelProjectCatalog>();
        var browser = provider.GetRequiredService<LabelProjectBrowser>();

        DatasetImportReport first = await importer.ImportAsync(new DatasetImportOptions(
            StorageLocation.FromPath(source.Root), StorageLocation.FromPath(target.Root), "第一"));

        DatasetImportReport second = await importer.ImportAsync(new DatasetImportOptions(
            StorageLocation.FromPath(source.Root), StorageLocation.FromPath(target.Root), "第二"));

        IReadOnlyList<TinyLabelProjectSummary> projects =
            await catalog.ListAsync(StorageLocation.FromPath(target.Root));

        // 列表按「未结束优先 + 创建时间倒序」，所以后建的项目排在前面
        Assert.Equal(new[] { "第二", "第一" }, projects.Select(project => project.Name));
        Assert.All(projects, project => Assert.Equal(4, project.ImageCount));
        Assert.All(projects, project => Assert.Equal(4, project.LabeledCount));
        Assert.Equal(new[] { "train", "val" }, projects[0].Splits);

        // 多项目库必须按 Id 打开，不能再依赖「库里只有一个项目」
        OpenedProject opened = await browser.OpenAsync(target.Root, second.ProjectId);

        Assert.Equal("第二", opened.ProjectName);
        Assert.Equal(second.ProjectId, opened.ProjectId);
        Assert.Equal(4, opened.Images.Count);
        Assert.NotEqual(first.ProjectId, second.ProjectId);
    }

    [Fact]
    public async Task Catalog_ReturnsEmptyForDirectoryWithoutDatabase()
    {
        using var empty = new TestWorkspace();

        using ServiceProvider provider = BuildProvider(empty.OpenDatabase());
        var catalog = provider.GetRequiredService<TinyLabelProjectCatalog>();

        IReadOnlyList<TinyLabelProjectSummary> projects =
            await catalog.ListAsync(StorageLocation.FromPath(empty.Root));

        Assert.Empty(projects);
    }

    [Fact]
    public async Task ProjectService_CreatesEmptyProjectInWorkspace()
    {
        using var workspace = new TestWorkspace();

        using ServiceProvider provider = BuildProvider(workspace.OpenDatabase());
        var service = provider.GetRequiredService<TinyLabelProjectService>();
        var catalog = provider.GetRequiredService<TinyLabelProjectCatalog>();

        // 库启动时就建好；「新建项目」只往库里插一行
        long projectId = await service.CreateAsync(
            StorageLocation.FromPath(workspace.Root),
            "空项目",
            "备注写这里");

        TinyLabelProjectSummary project =
            Assert.Single(await catalog.ListAsync(StorageLocation.FromPath(workspace.Root)));

        Assert.Equal(projectId, project.Id);
        Assert.Equal("空项目", project.Name);
        Assert.Equal("备注写这里", project.Description);
        Assert.Equal(0, project.ImageCount);
        Assert.Empty(project.Splits);
    }

    [Fact]
    public async Task ProjectService_RejectsEmptyName()
    {
        using var workspace = new TestWorkspace();

        using ServiceProvider provider = BuildProvider(workspace.OpenDatabase());
        var service = provider.GetRequiredService<TinyLabelProjectService>();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(StorageLocation.FromPath(workspace.Root), "   "));
    }

    [Fact]
    public async Task Import_RejectsTargetEqualToSource()
    {
        using var workspace = new TestWorkspace();
        WriteYoloDataset(workspace);

        using ServiceProvider provider = BuildProvider(workspace.OpenDatabase());
        var importer = provider.GetRequiredService<DatasetImportService>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(new DatasetImportOptions(
            StorageLocation.FromPath(workspace.Root),
            StorageLocation.FromPath(workspace.Root))));
    }

    [Fact]
    public async Task Import_RejectsTargetInAnotherDatabase()
    {
        using var workspace = new TestWorkspace();
        using var source = new TestWorkspace();
        using var other = new TestWorkspace();

        WriteYoloDataset(source);

        // 当前库启动时就定了：导入只能写它，不能借 Destination 另开一个库
        using ServiceProvider provider = BuildProvider(workspace.OpenDatabase());
        var importer = provider.GetRequiredService<DatasetImportService>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(new DatasetImportOptions(
            StorageLocation.FromPath(source.Root),
            StorageLocation.FromPath(other.Root))));
    }

    // ── 造数据 ────────────────────────────────────────────────────────────────

    /// <summary>4 张 96x72 的 PNG：前 2 张 train、后 2 张 val；返回标注框总数。</summary>
    private static int WriteYoloDataset(TestWorkspace workspace)
    {
        var generator = new SyntheticImageGenerator(new SyntheticOptions(4, 96, 72, 1, 2, 20261112));
        IReadOnlyList<SyntheticImage> images = generator.Generate();

        var yaml = new StringBuilder();
        yaml.AppendLine("path: .");
        yaml.AppendLine("train: images/train");
        yaml.AppendLine("val: images/val");
        yaml.AppendLine("names:");
        yaml.AppendLine("  0: square");
        yaml.AppendLine("  1: triangle");
        yaml.AppendLine("  2: circle");
        workspace.WriteText("data.yaml", yaml.ToString());

        workspace.WriteText("classes.txt", string.Join('\n', SyntheticImageGenerator.ClassNames) + "\n");

        int boxes = 0;

        for (int index = 0; index < images.Count; index++)
        {
            SyntheticImage image = images[index];
            string split = index < 2 ? "train" : "val";

            workspace.WriteBytes($"images/{split}/{image.FileName}", image.Png);

            var labels = new StringBuilder();

            foreach (SyntheticShape shape in image.Shapes)
            {
                int classId = IndexOfClass(shape.Label);
                (double cx, double cy, double width, double height) =
                    BoxConvert.ToYolo(shape.Bounds, image.Width, image.Height);

                labels.Append(classId.ToString(CultureInfo.InvariantCulture)).Append(' ')
                    .Append(cx.ToString("F6", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(cy.ToString("F6", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(width.ToString("F6", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(height.ToString("F6", CultureInfo.InvariantCulture)).Append('\n');

                boxes++;
            }

            workspace.WriteText($"labels/{split}/{Path.GetFileNameWithoutExtension(image.FileName)}.txt", labels.ToString());
        }

        return boxes;
    }

    /// <summary>造一个可二次导入的项目库：图片是真的 PNG（导入要读尺寸）。</summary>
    private static void WriteDbProject(TestWorkspace workspace)
    {
        byte[] png = new SyntheticImageGenerator(new SyntheticOptions(1, 640, 480, 1, 1, 11))
            .Generate()[0]
            .Png;

        using var database = TinyLabelDatabase.Open(Path.Combine(workspace.Root, "annotrain.db"));

        long projectId = (long)database.Orm.Insert(new ProjectEntity
        {
            Name = "源项目",
            CreatedAt = DateTime.UtcNow,
        }).ExecuteIdentity();

        database.Orm.Insert(new ProjectClassEntity { ProjectId = projectId, Name = "cat", OrderIndex = 0 }).ExecuteAffrows();
        database.Orm.Insert(new ProjectClassEntity { ProjectId = projectId, Name = "dog", OrderIndex = 1 }).ExecuteAffrows();

        AddSeedImage(database, projectId, workspace, "a.png", "train", png, TinyLabelSeed.AnnotationJson);
        AddSeedImage(database, projectId, workspace, "b.png", "val", png, null);
    }

    private static void AddSeedImage(
        TinyLabelDatabase database,
        long projectId,
        TestWorkspace workspace,
        string relative,
        string split,
        byte[] png,
        string? annotationJson)
    {
        string full = workspace.WriteBytes($"projects/{projectId}/{relative}", png).Replace('\\', '/');

        long imageId = (long)database.Orm.Insert(new ImageEntity
        {
            ProjectId = projectId,
            FileName = relative,
            RelPath = relative,
            FilePath = full,
            Width = 640,
            Height = 480,
            Split = split,
        }).ExecuteIdentity();

        if (annotationJson is null)
        {
            return;
        }

        database.Orm.Insert(new AnnotationEntity
        {
            ImageId = imageId,
            ProjectId = projectId,
            Revision = 0,
            DocJson = annotationJson,
        }).ExecuteAffrows();
    }

    /// <summary>RF-DETR 结构：train/ 与 valid/ 各一个 COCO json，图片与 json 同目录。</summary>
    private static void WriteCocoDataset(TestWorkspace workspace)
    {
        var generator = new SyntheticImageGenerator(new SyntheticOptions(1, 96, 72, 1, 1, 7));
        byte[] png = generator.Generate()[0].Png;

        WriteCocoSplit(workspace, "train", png, "train.png", "cat", RectD.FromLtrb(8, 8, 48, 48));
        WriteCocoSplit(workspace, "valid", png, "valid.png", "dog", RectD.FromLtrb(16, 12, 64, 60));
    }

    private static void WriteCocoSplit(
        TestWorkspace workspace,
        string split,
        byte[] png,
        string fileName,
        string label,
        RectD box)
    {
        workspace.WriteBytes($"{split}/{fileName}", png);

        var annotations = new DatasetAnnotations
        {
            Classes = ClassCatalog.FromNames(["cat", "dog"]),
            Images = new Dictionary<string, AnnotationDocument>(StringComparer.OrdinalIgnoreCase)
            {
                [fileName] = new AnnotationDocument
                {
                    ImagePath = fileName,
                    ImageWidth = 96,
                    ImageHeight = 72,
                    Shapes =
                    [
                        new Shape
                        {
                            Id = "1",
                            Type = ShapeType.Rectangle,
                            Points = BoxConvert.ToCorners(box),
                            Label = label,
                        },
                    ],
                },
            },
        };

        workspace.WriteText($"{split}/_annotations.coco.json", new CocoDatasetCodec().Write(annotations));
    }

    private static int IndexOfClass(string label)
    {
        for (int index = 0; index < SyntheticImageGenerator.ClassNames.Count; index++)
        {
            if (SyntheticImageGenerator.ClassNames[index] == label)
            {
                return index;
            }
        }

        return -1;
    }

    private static ServiceProvider BuildProvider(TinyLabelDatabase database)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(database);
        services.AddSingleton(database.Orm);
        services.AddAnnotrainDatasets();
        services.AddAnnotrainLabel();
        return services.BuildServiceProvider();
    }
}
