using Microsoft.Extensions.DependencyInjection;
using Sevencat.Core.Dataset;
using Sevencat.Core.Imaging;
using Sevencat.Core.Model;
using Sevencat.Core.Storage;
using Xunit;

namespace Sevencat.Train.Engine.Tests;

/// <summary>训练端只依赖 Core：平铺数据集也必须能读出框（codec 由 AddAnnotrainDatasets 统一注册）。</summary>
public class FlatDatasetTests
{
    [Fact]
    public async Task CoreOnlyContainer_ReadsFlatLabels()
    {
        string root = Path.Combine(Path.GetTempPath(), "annotrain-train-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            File.WriteAllText(Path.Combine(root, "a.jpg"), "x");
            File.WriteAllText(
                Path.Combine(root, "a.json"),
                """
                {"version":"5.0","imagePath":"a.jpg","imageWidth":640,"imageHeight":480,
                 "shapes":[{"label":"cat","shape_type":"rectangle","points":[[10,20],[110,120]]}]}
                """);

            var services = new ServiceCollection();
            services.AddAnnotrainDatasets();
            services.AddSingleton<IImageInfoSource>(new FixedImageInfoSource(640, 480));
            using ServiceProvider provider = services.BuildServiceProvider();

            var resolver = provider.GetRequiredService<DatasetStoreResolver>();
            DatasetOpenResult result = await resolver.OpenAsync(StorageLocation.FromPath(root));

            Assert.Equal(DatasetFormat.Flat, result.Store.Format);

            DatasetSample sample = Assert.Single(await result.Store.OpenSplit("all").LoadSamplesAsync());
            BoxAnnotation box = Assert.Single(sample.Boxes);

            Assert.Equal("cat", result.Store.Classes.NameOf(box.ClassId));
            Assert.Equal(10, box.X1, 1);
            Assert.Equal(120, box.Y2, 1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixedImageInfoSource(int width, int height) : IImageInfoSource
    {
        public Task<ImageInfo?> ReadAsync(ImageRef image, CancellationToken ct = default)
            => Task.FromResult<ImageInfo?>(new ImageInfo
            {
                RelativePath = image.RelativePath,
                Width = width,
                Height = height,
            });
    }
}
