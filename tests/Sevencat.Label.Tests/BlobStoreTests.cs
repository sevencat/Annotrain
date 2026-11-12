using System.Text;
using Sevencat.Core.Storage;
using Xunit;

namespace Sevencat.Label.Tests;

public class BlobStoreTests
{
    private static (LocalBlobStore Store, StorageKey Key) Create(TestWorkspace workspace, string relative)
    {
        var location = StorageLocation.FromPath(workspace.Root);
        return (new LocalBlobStore(location), new StorageKey(location, relative));
    }

    [Fact]
    public async Task Write_Then_Read_RoundTrips()
    {
        using var workspace = new TestWorkspace();
        (LocalBlobStore store, StorageKey key) = Create(workspace, "labels/a.txt");

        using var payload = new MemoryStream(Encoding.UTF8.GetBytes("0 0.5 0.5 0.1 0.1"));
        await store.WriteAsync(key, payload);

        Assert.True(await store.ExistsAsync(key));

        await using Stream stream = await store.OpenReadAsync(key);
        using var reader = new StreamReader(stream);
        Assert.Equal("0 0.5 0.5 0.1 0.1", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Write_IsAtomic_NoTempFileLeft()
    {
        using var workspace = new TestWorkspace();
        (LocalBlobStore store, StorageKey key) = Create(workspace, "a.json");

        await store.WriteAsync(key, new MemoryStream("{}"u8.ToArray()));
        await store.WriteAsync(key, new MemoryStream("{\"a\":1}"u8.ToArray()));

        string[] files = Directory.GetFiles(Path.Combine(workspace.Root), "*", SearchOption.AllDirectories);
        Assert.Single(files);
        Assert.DoesNotContain(".tmp", files[0]);
    }

    [Fact]
    public async Task ReadRange_LimitsLength()
    {
        using var workspace = new TestWorkspace();
        (LocalBlobStore store, StorageKey key) = Create(workspace, "big.json");
        await store.WriteAsync(key, new MemoryStream(Encoding.UTF8.GetBytes("0123456789")));

        await using Stream stream = await store.OpenReadRangeAsync(key, 2, 4);
        var buffer = new byte[16];
        int read = await stream.ReadAsync(buffer);

        Assert.Equal(4, read);
        Assert.Equal("2345", Encoding.UTF8.GetString(buffer, 0, read));
    }

    [Fact]
    public async Task List_ReturnsRelativePaths()
    {
        using var workspace = new TestWorkspace();
        (LocalBlobStore store, StorageKey key) = Create(workspace, "");
        workspace.WriteText("images/train/a.jpg", "x");
        workspace.WriteText("images/train/b.jpg", "x");
        workspace.WriteText("labels/a.txt", "0 0.5 0.5 0.1 0.1");

        var files = new List<string>();
        await foreach (BlobEntry entry in store.ListAsync(key))
        {
            files.Add(entry.RelativePath);
        }

        Assert.Equal(3, files.Count);
        Assert.Contains("images/train/a.jpg", files);
        Assert.All(files, path => Assert.DoesNotContain('\\', path));
    }

    [Fact]
    public async Task Resolve_RejectsPathTraversal()
    {
        using var workspace = new TestWorkspace();
        (LocalBlobStore store, StorageKey key) = Create(workspace, "../outside.txt");

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(key, new MemoryStream("x"u8.ToArray())));
    }

    [Fact]
    public void Parse_HandlesFileUri()
    {
        StorageLocation location = StorageLocation.Parse("file:///C:/data/set");

        Assert.Equal("file", location.Scheme);
        Assert.Equal("C:\\data\\set", location.Root);
    }

    [Fact]
    public void Parse_TreatsBarePathAsFile()
    {
        StorageLocation location = StorageLocation.Parse("D:/datasets/yolo");

        Assert.True(location.IsFile);
        Assert.Equal("D:\\datasets\\yolo", location.Root);
    }
}
