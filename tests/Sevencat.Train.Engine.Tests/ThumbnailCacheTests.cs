using ImageFacade;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Imaging;
using Sevencat.Core.Storage;
using Xunit;

namespace Sevencat.Train.Engine.Tests;

public sealed class ThumbnailCacheTests
{
    [Fact]
    public async Task EnsureAsync_WritesThumbnailUnderProjectThumbs()
    {
        string dir = Path.Combine(Path.GetTempPath(), "annotrain-thumb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var root = StorageLocation.FromPath(dir);
            const long projectId = 7;
            const string relative = "sub/sample.png";

            string source = Path.Combine(TinyLabelWorkspace.ImageDirectory(root, projectId), "sub", "sample.png");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);

            using (FacadeImage image = ImageRuntime.Engine.Create(1200, 800, Rgba32.White))
            {
                image.Save(source, new SaveOptions { Format = ImageFormat.Png });
            }

            string? thumb = await ThumbnailCache.EnsureAsync(root, projectId, relative, source, 512);

            Assert.NotNull(thumb);
            Assert.StartsWith(TinyLabelWorkspace.ThumbDirectory(root, projectId), thumb, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(thumb));
            Assert.Equal(".jpg", Path.GetExtension(thumb), ignoreCase: true);

            using (FacadeImage loaded = ImageRuntime.Engine.Load(thumb!, new LoadOptions()))
            {
                Assert.Equal(512, loaded.Width);
                Assert.InRange(loaded.Height, 341, 342);
            }

            // 原图没更新就不重写（列表反复加载不会重复解码）
            DateTime first = File.GetLastWriteTimeUtc(thumb!);
            string? again = await ThumbnailCache.EnsureAsync(root, projectId, relative, source, 512);

            Assert.Equal(thumb, again);
            Assert.Equal(first, File.GetLastWriteTimeUtc(thumb!));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
