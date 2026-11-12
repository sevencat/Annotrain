using System.Text;
using Sevencat.Core.Dataset;
using Sevencat.Core.Storage;
using Xunit;

namespace Sevencat.Label.Tests;

public class ProbeTests
{
    private static DatasetProbeService CreateProbe()
        => new(new BlobStoreRegistry([new LocalBlobStoreFactory()]));

    [Fact]
    public async Task Detects_Yolo()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteText("data.yaml", "path: .\ntrain: images/train\nval: images/val\nnames:\n  0: cat\n");
        workspace.WriteText("images/train/a.jpg", "x");
        workspace.WriteText("labels/train/a.txt", "0 0.5 0.5 0.2 0.2\n");

        DatasetProbe probe = await CreateProbe().DetectAsync(StorageLocation.FromPath(workspace.Root));

        Assert.Equal(DatasetFormat.Yolo, probe.Format);
        Assert.Equal("data.yaml", probe.DescriptorPath);
    }

    [Fact]
    public async Task Detects_Coco()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteText(
            "train/_annotations.coco.json",
            """{"images":[{"id":1,"file_name":"a.jpg","width":10,"height":10}],"annotations":[],"categories":[]}""");

        DatasetProbe probe = await CreateProbe().DetectAsync(StorageLocation.FromPath(workspace.Root));

        Assert.Equal(DatasetFormat.Coco, probe.Format);
        Assert.Equal("train/_annotations.coco.json", probe.DescriptorPath);
    }

    [Fact]
    public async Task Detects_Voc()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteText("Annotations/a.xml", "<annotation />");
        workspace.WriteText("JPEGImages/a.jpg", "x");

        DatasetProbe probe = await CreateProbe().DetectAsync(StorageLocation.FromPath(workspace.Root));

        Assert.Equal(DatasetFormat.Voc, probe.Format);
    }

    [Fact]
    public async Task Detects_TinyLabelDatabase()
    {
        using var workspace = new TestWorkspace();
        byte[] header = Encoding.ASCII.GetBytes("SQLite format 3\0");
        workspace.WriteBytes("annotrain.db", header);

        DatasetProbe probe = await CreateProbe().DetectAsync(StorageLocation.FromPath(workspace.Root));

        Assert.Equal(DatasetFormat.TinyLabelDb, probe.Format);
        Assert.Equal("annotrain.db", probe.DescriptorPath);
    }

    [Fact]
    public async Task Detects_Flat()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteText("a.jpg", "x");
        workspace.WriteText("a.json", "{}");

        DatasetProbe probe = await CreateProbe().DetectAsync(StorageLocation.FromPath(workspace.Root));

        Assert.Equal(DatasetFormat.Flat, probe.Format);
    }

    [Fact]
    public async Task Reports_Ambiguous_WhenYoloAndCocoCoexist()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteText("data.yaml", "train: images/train\nnames:\n  0: cat\n");
        workspace.WriteText(
            "annotations/instances.json",
            """{"images":[],"annotations":[],"categories":[]}""");

        DatasetProbe probe = await CreateProbe().DetectAsync(StorageLocation.FromPath(workspace.Root));

        Assert.Equal(DatasetFormat.Ambiguous, probe.Format);
        Assert.NotEmpty(probe.Alternatives);
        Assert.Contains("请选择", probe.Message);
    }

    [Fact]
    public async Task Reports_Unknown_ForEmptyDirectory()
    {
        using var workspace = new TestWorkspace();

        DatasetProbe probe = await CreateProbe().DetectAsync(StorageLocation.FromPath(workspace.Root));

        Assert.Equal(DatasetFormat.Unknown, probe.Format);
        Assert.False(probe.IsRecognized);
    }
}
