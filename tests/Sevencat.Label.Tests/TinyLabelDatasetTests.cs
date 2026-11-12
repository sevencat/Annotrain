using Microsoft.Extensions.DependencyInjection;
using Sevencat.Core.Annotation;
using Sevencat.Core.Dataset;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Model;
using Sevencat.Core.Storage;
using Xunit;

namespace Sevencat.Label.Tests;

public class TinyLabelDatasetTests
{
    [Fact]
    public async Task Opens_DatabaseDataset_AndReadsBoxes()
    {
        using var workspace = new TestWorkspace();
        TinyLabelSeed.Create(workspace.Root);

        using ServiceProvider provider = BuildProvider();
        var resolver = provider.GetRequiredService<DatasetStoreResolver>();

        DatasetOpenResult result = await resolver.OpenAsync(StorageLocation.FromPath(workspace.Root));

        Assert.Equal(DatasetFormat.TinyLabelDb, result.Store.Format);
        Assert.Equal(["train", "val"], result.Store.Splits);
        Assert.Equal(["cat", "dog"], result.Store.Classes.Names);

        DatasetSample sample = Assert.Single(await result.Store.OpenSplit("train").LoadSamplesAsync());
        BoxAnnotation box = Assert.Single(sample.Boxes);

        Assert.Equal(0, box.ClassId);
        Assert.Equal(10, box.X1, 1);
        Assert.Equal(120, box.Y2, 1);
        // 图片落在数据集根下时给相对根路径，blob store 直接可用
        Assert.Equal($"projects/{((TinyLabelDatasetStore)result.Store).ProjectId}/a.jpg", sample.Image.RelativePath);

        (result.Store as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task MultipleProjects_RequireExplicitSelection()
    {
        using var workspace = new TestWorkspace();
        (_, long secondProjectId) = TinyLabelSeed.Create(workspace.Root, projectCount: 2);

        using ServiceProvider provider = BuildProvider();
        var resolver = provider.GetRequiredService<DatasetStoreResolver>();
        var location = StorageLocation.FromPath(workspace.Root);

        var error = await Assert.ThrowsAsync<DatasetProjectSelectionException>(() => resolver.OpenAsync(location));
        Assert.Equal(2, error.Projects.Count);

        DatasetOpenResult chosen = await resolver.OpenAsync(
            location,
            datasetId: secondProjectId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(secondProjectId, Assert.IsType<TinyLabelDatasetStore>(chosen.Store).ProjectId);

        (chosen.Store as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task AnnotationStore_SavesWithRevisionAndRejectsStaleWrite()
    {
        using var workspace = new TestWorkspace();
        (string dbPath, long projectId) = TinyLabelSeed.Create(workspace.Root, annotationJson: null);

        TinyLabelDatabase database = workspace.OpenDatabaseAt(dbPath);

        using var store = new DatabaseAnnotationStore(database, projectId);
        var image = new ImageRef("a.jpg", StorageLocation.FromPath(workspace.Root));

        Assert.False(await store.ExistsAsync(image));

        var document = new AnnotationDocument
        {
            ImagePath = "a.jpg",
            ImageWidth = 640,
            ImageHeight = 480,
            Shapes =
            [
                new Shape
                {
                    Id = "s1",
                    Type = ShapeType.Rectangle,
                    Points = BoxConvert.ToCorners(RectD.FromLtrb(10, 20, 110, 120)),
                    Label = "cat",
                },
            ],
        };

        AnnotationEnvelope saved = await store.SaveAsync(image, document);

        Assert.Equal(1, saved.Document.Meta.Revision);
        Assert.True(await store.ExistsAsync(image));

        AnnotationEnvelope? loaded = await store.LoadAsync(image);
        Assert.NotNull(loaded);
        Assert.Equal("cat", Assert.Single(loaded.Document.Shapes).Label);

        AnnotationEnvelope second = await store.SaveAsync(image, document, expectedRevision: 1);
        Assert.Equal(2, second.Document.Meta.Revision);

        await Assert.ThrowsAsync<AnnotationConcurrencyException>(
            () => store.SaveAsync(image, document, expectedRevision: 1));
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddAnnotrainDatasets();
        return services.BuildServiceProvider();
    }
}
