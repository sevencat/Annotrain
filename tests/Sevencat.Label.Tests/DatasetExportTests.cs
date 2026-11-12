using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sevencat.Core.Dataset;
using Sevencat.Core.Dataset.Export;
using Sevencat.Core.Storage;
using Xunit;

namespace Sevencat.Label.Tests;

public class DatasetExportTests
{
    [Fact]
    public async Task Exports_Database_ToYolo()
    {
        using var workspace = new TestWorkspace();
        using var output = new TestWorkspace();
        TinyLabelSeed.Create(workspace.Root);

        using ServiceProvider provider = BuildProvider();
        var resolver = provider.GetRequiredService<DatasetStoreResolver>();
        var exportService = provider.GetRequiredService<DatasetExportService>();

        DatasetOpenResult opened = await resolver.OpenAsync(StorageLocation.FromPath(workspace.Root));

        try
        {
            DatasetExportReport report = await exportService.ExportAsync(
                opened.Store,
                new DatasetExportOptions(DatasetFormat.Yolo, StorageLocation.FromPath(output.Root)));

            Assert.Equal(2, report.ImagesExported);
            Assert.Equal(1, report.BoxesExported);
            Assert.False(report.HasWarnings);

            string yaml = File.ReadAllText(Path.Combine(output.Root, "data.yaml"));
            Assert.Contains("names:", yaml, StringComparison.Ordinal);
            Assert.Contains("  0: cat", yaml, StringComparison.Ordinal);
            Assert.Contains("train: images/train", yaml, StringComparison.Ordinal);
            Assert.Contains("val: images/val", yaml, StringComparison.Ordinal);
            // 与 TinyLabel 一致：path 写绝对路径，整个目录拷走也能训练
            Assert.Contains($"path: {output.Root.Replace('\\', '/')}", yaml, StringComparison.Ordinal);

            Assert.Equal("cat\ndog\n", File.ReadAllText(Path.Combine(output.Root, "classes.txt")).Replace("\r\n", "\n"));

            string labels = File.ReadAllText(Path.Combine(output.Root, "labels", "train", "a.txt"));
            Assert.Equal("0 0.093750 0.145833 0.156250 0.208333\n", labels.Replace("\r\n", "\n"));

            Assert.True(File.Exists(Path.Combine(output.Root, "images", "train", "a.jpg")));
            Assert.True(File.Exists(Path.Combine(output.Root, "labels", "val", "b.txt")));
        }
        finally
        {
            (opened.Store as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public async Task Exports_Database_ToRfDetrCoco()
    {
        using var workspace = new TestWorkspace();
        using var output = new TestWorkspace();
        TinyLabelSeed.Create(workspace.Root);

        using ServiceProvider provider = BuildProvider();
        var resolver = provider.GetRequiredService<DatasetStoreResolver>();
        var exportService = provider.GetRequiredService<DatasetExportService>();

        DatasetOpenResult opened = await resolver.OpenAsync(StorageLocation.FromPath(workspace.Root));

        try
        {
            DatasetExportReport report = await exportService.ExportAsync(
                opened.Store,
                new DatasetExportOptions(DatasetFormat.Coco, StorageLocation.FromPath(output.Root)));

            Assert.Equal(2, report.ImagesExported);
            Assert.Equal(1, report.BoxesExported);

            // RF-DETR 认 train / valid 两个目录
            string trainJson = Path.Combine(output.Root, "train", "_annotations.coco.json");
            string validJson = Path.Combine(output.Root, "valid", "_annotations.coco.json");
            Assert.True(File.Exists(trainJson));
            Assert.True(File.Exists(validJson));

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(trainJson));
            JsonElement root = document.RootElement;

            JsonElement category = root.GetProperty("categories")[0];
            Assert.Equal("cat", category.GetProperty("name").GetString());
            // COCO 约定 category_id 从 1 开始（与内部 classId 相差 1）
            int categoryId = category.GetProperty("id").GetInt32();

            Assert.Equal("a.jpg", root.GetProperty("images")[0].GetProperty("file_name").GetString());

            JsonElement annotation = root.GetProperty("annotations")[0];
            Assert.Equal(categoryId, annotation.GetProperty("category_id").GetInt32());

            JsonElement bbox = annotation.GetProperty("bbox");
            Assert.Equal(10, bbox[0].GetDouble(), 1);
            Assert.Equal(20, bbox[1].GetDouble(), 1);
            Assert.Equal(100, bbox[2].GetDouble(), 1);
            Assert.Equal(100, bbox[3].GetDouble(), 1);

            Assert.True(File.Exists(Path.Combine(output.Root, "train", "a.jpg")));
        }
        finally
        {
            (opened.Store as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public async Task Export_RejectsWritingBackToSourceRoot()
    {
        using var workspace = new TestWorkspace();
        TinyLabelSeed.Create(workspace.Root);

        using ServiceProvider provider = BuildProvider();
        var resolver = provider.GetRequiredService<DatasetStoreResolver>();
        var exportService = provider.GetRequiredService<DatasetExportService>();

        DatasetOpenResult opened = await resolver.OpenAsync(StorageLocation.FromPath(workspace.Root));

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => exportService.ExportAsync(
                opened.Store,
                new DatasetExportOptions(DatasetFormat.Yolo, StorageLocation.FromPath(workspace.Root))));
        }
        finally
        {
            (opened.Store as IDisposable)?.Dispose();
        }
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddAnnotrainDatasets();
        return services.BuildServiceProvider();
    }
}
