using Microsoft.Extensions.DependencyInjection;
using Sevencat.Core.Dataset;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Storage;
using Sevencat.Label;
using Xunit;

namespace Sevencat.Label.Tests;

/// <summary>新建项目后必须能被探测到、并被浏览器打开（界面上「新建」的数据链路）。</summary>
public class ProjectCreateTests
{
    [Fact]
    public async Task NewProject_IsDetectedAndOpenableByBrowser()
    {
        using var workspace = new TestWorkspace();
        using ServiceProvider provider = BuildProvider();

        var projects = provider.GetRequiredService<TinyLabelProjectService>();

        // 数据目录初始化（建空库）→ 新建项目只插一条记录
        TinyLabelWorkspace.EnsureDatabase(StorageLocation.FromPath(workspace.Root));

        long projectId = await projects.CreateAsync(StorageLocation.FromPath(workspace.Root), "新项目");

        string databasePath = Path.Combine(workspace.Root, TinyLabelProjectCatalog.DefaultDatabaseFile);
        Assert.True(File.Exists(databasePath), $"数据库文件不存在：{databasePath}");

        // 库连接是进程内常驻的（TinyLabelDatabase.Current），读文件头要按共享读打开
        using var stream = new FileStream(
            databasePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        byte[] header = new byte[16];
        int read = stream.Read(header, 0, header.Length);
        Assert.True(read >= 16, $"数据库文件只有 {read} 字节（SQLite 头缺失，探测会失败）");

        var catalog = provider.GetRequiredService<TinyLabelProjectCatalog>();
        TinyLabelProjectSummary summary =
            Assert.Single(await catalog.ListAsync(StorageLocation.FromPath(workspace.Root)));
        Assert.Equal(projectId, summary.Id);

        var resolver = provider.GetRequiredService<DatasetStoreResolver>();
        DatasetProbe probe = await resolver.DetectAsync(StorageLocation.FromPath(workspace.Root));
        Assert.Equal(DatasetFormat.TinyLabelDb, probe.Format);

        var browser = provider.GetRequiredService<LabelProjectBrowser>();
        OpenedProject opened = await browser.OpenAsync(workspace.Root, projectId);

        Assert.Equal(projectId, opened.ProjectId);
        Assert.Equal("新项目", opened.ProjectName);
        Assert.Empty(opened.Images);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAnnotrainDatasets();
        services.AddAnnotrainLabel();
        return services.BuildServiceProvider();
    }
}
