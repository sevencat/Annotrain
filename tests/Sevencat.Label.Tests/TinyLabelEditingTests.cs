using Microsoft.Extensions.DependencyInjection;
using Sevencat.Core.Dataset;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Simulation;
using Sevencat.Core.Storage;
using Xunit;

namespace Sevencat.Label.Tests;

/// <summary>项目 / 图片 / 类别这三套写操作的回归测试（对应界面上新增的按钮）。</summary>
public class TinyLabelEditingTests
{
    [Fact]
    public async Task Project_UpdateCloseDelete_Works()
    {
        using var workspace = new TestWorkspace();
        using ServiceProvider provider = BuildProvider();

        var projects = provider.GetRequiredService<TinyLabelProjectService>();
        var catalog = provider.GetRequiredService<TinyLabelProjectCatalog>();
        StorageLocation root = StorageLocation.FromPath(workspace.Root);

        TinyLabelWorkspace.EnsureDatabase(root);

        long projectId = await projects.CreateAsync(root, "原名字", "原备注");

        await projects.UpdateAsync(root, projectId, "新名字", "新备注");

        TinyLabelProjectSummary updated = Assert.Single(await catalog.ListAsync(root));
        Assert.Equal("新名字", updated.Name);
        Assert.Equal("新备注", updated.Description);
        Assert.False(updated.IsClosed);

        await projects.SetClosedAsync(root, projectId, closed: true);
        Assert.True(Assert.Single(await catalog.ListAsync(root)).IsClosed);

        await projects.SetClosedAsync(root, projectId, closed: false);
        Assert.False(Assert.Single(await catalog.ListAsync(root)).IsClosed);

        await projects.DeleteAsync(root, projectId);
        Assert.Empty(await catalog.ListAsync(root));
    }

    [Fact]
    public async Task Images_QueryImportAndClasses_Work()
    {
        using var workspace = new TestWorkspace();
        using ServiceProvider provider = BuildProvider();

        var registry = provider.GetRequiredService<BlobStoreRegistry>();
        var images = provider.GetRequiredService<TinyLabelImageService>();
        var classes = provider.GetRequiredService<TinyLabelClassService>();
        var catalog = provider.GetRequiredService<TinyLabelProjectCatalog>();
        var projects = provider.GetRequiredService<TinyLabelProjectService>();

        string datasetRoot = Path.Combine(workspace.Root, "dataset");
        var generator = new SyntheticImageGenerator(new SyntheticOptions(
            ImageCount: 5,
            Width: 64,
            Height: 48,
            MinShapes: 1,
            MaxShapes: 2,
            Seed: 7));

        await SyntheticLabelingSession.ImportAsync(
            generator.Generate(),
            registry,
            new SyntheticImportOptions(datasetRoot));

        StorageLocation root = StorageLocation.FromPath(datasetRoot);
        TinyLabelProjectSummary project = Assert.Single(await catalog.ListAsync(root));

        // 分页：每页 2 张，总数不变
        TinyLabelImagePage first = await images.QueryAsync(root, project.Id, pageIndex: 0, pageSize: 2);
        Assert.Equal(5, first.Total);
        Assert.Equal(2, first.Items.Count);
        Assert.Equal(3, first.PageCount);
        Assert.Equal(64, first.Items[0].Width);
        Assert.Equal(48, first.Items[0].Height);
        Assert.False(string.IsNullOrWhiteSpace(first.Items[0].StateText));

        TinyLabelImagePage last = await images.QueryAsync(root, project.Id, pageIndex: 2, pageSize: 2);
        Assert.Single(last.Items);

        // 状态与备注写回
        TinyLabelImageRow row = first.Items[0];
        await images.SetStateAsync(root, row.Id, "reviewed", "人工通过");
        TinyLabelImagePage reloaded = await images.QueryAsync(root, project.Id, 0, 2);
        TinyLabelImageRow changed = Assert.Single(reloaded.Items, item => item.Id == row.Id);
        Assert.Equal("reviewed", changed.State);
        Assert.Equal("已通过", changed.StateText);
        Assert.Equal("人工通过", changed.Remark);

        // 类别：导入时已有 3 个，追加一个后变成 4 个，改名保留 classId
        IReadOnlyList<TinyLabelClassRow> before = await classes.ListAsync(root, project.Id);
        Assert.Equal(SyntheticImageGenerator.ClassNames.Count, before.Count);

        int index = await classes.AddAsync(root, project.Id, "新增类别");
        Assert.Equal(before.Count, index);

        TinyLabelClassRow added = Assert.Single(
            await classes.ListAsync(root, project.Id),
            item => item.Name == "新增类别");
        Assert.Equal(index, added.OrderIndex);

        await classes.RenameAsync(root, project.Id, added.Id, "改名后的类别");
        Assert.Contains(
            await classes.ListAsync(root, project.Id),
            item => item.Id == added.Id && item.Name == "改名后的类别" && item.OrderIndex == index);

        // 没被标注用到的类别可以删；已标注用到的会拒绝
        await classes.RemoveAsync(root, project.Id, added.Id);
        Assert.DoesNotContain(await classes.ListAsync(root, project.Id), item => item.Id == added.Id);

        // 标注里真正出现过的类别不能删（从 labelme JSON 里取一个 label 名）
        using (var database = TinyLabelDatabase.Open(
                   Path.Combine(datasetRoot, TinyLabelProjectCatalog.DefaultDatabaseFile),
                   createIfMissing: false))
        {
            string json = database.Orm.Select<AnnotationEntity>()
                .Where(row => row.ProjectId == project.Id)
                .First()?.DocJson ?? string.Empty;

            var match = System.Text.RegularExpressions.Regex.Match(json, "\"label\"\\s*:\\s*\"([^\"]+)\"");
            Assert.True(match.Success, "导入的标注里应该带 label");

            string usedName = match.Groups[1].Value;
            TinyLabelClassRow used = Assert.Single(
                await classes.ListAsync(root, project.Id),
                item => item.Name == usedName);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => classes.RemoveAsync(root, project.Id, used.Id));
        }

        // 导入图片进新项目：从磁盘复制一张，跳过重复
        TinyLabelWorkspace.EnsureDatabase(StorageLocation.FromPath(workspace.Root));
        long emptyProject = await projects.CreateAsync(StorageLocation.FromPath(workspace.Root), "空项目");
        string sample = Path.Combine(datasetRoot, "projects", project.Id.ToString());
        string source = Directory.EnumerateFiles(sample, "*", SearchOption.AllDirectories).First();

        TinyLabelImageImportReport report = await images.ImportFilesAsync(
            StorageLocation.FromPath(workspace.Root),
            emptyProject,
            [source],
            split: "train");

        Assert.Equal(1, report.Imported);
        Assert.True(File.Exists(Path.Combine(workspace.Root, "projects", emptyProject.ToString(), Path.GetFileName(source))));

        // 同一张再导一次：按 sha256 去重
        TinyLabelImageImportReport again = await images.ImportFilesAsync(
            StorageLocation.FromPath(workspace.Root),
            emptyProject,
            [source]);
        Assert.Equal(0, again.Imported);
        Assert.Equal(1, again.Skipped);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAnnotrainDatasets();
        return services.BuildServiceProvider();
    }
}
