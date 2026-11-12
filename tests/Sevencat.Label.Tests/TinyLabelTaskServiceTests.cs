using Microsoft.Extensions.DependencyInjection;
using Sevencat.Core.Dataset;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Simulation;
using Sevencat.Core.Storage;
using Xunit;

namespace Sevencat.Label.Tests;

/// <summary>Web 端任务流（下一张 / 租约 / 跳过 / 退回 / 标注版本）的回归测试。</summary>
public class TinyLabelTaskServiceTests
{
    [Fact]
    public async Task NextImage_Lease_SkipReturn_AndAnnotationRevision_Work()
    {
        using var workspace = new TestWorkspace();
        using ServiceProvider provider = BuildProvider();

        var registry = provider.GetRequiredService<BlobStoreRegistry>();
        var catalog = provider.GetRequiredService<TinyLabelProjectCatalog>();
        var tasks = provider.GetRequiredService<TinyLabelTaskService>();

        string datasetRoot = Path.Combine(workspace.Root, "dataset");
        var generator = new SyntheticImageGenerator(new SyntheticOptions(
            ImageCount: 5,
            Width: 64,
            Height: 48,
            MinShapes: 1,
            MaxShapes: 2,
            Seed: 11));

        await SyntheticLabelingSession.ImportAsync(
            generator.Generate(),
            registry,
            new SyntheticImportOptions(datasetRoot));

        // 模拟流程生成的图都是 labeled，这里退回 pending 当成待标池
        ResetToPending(datasetRoot);

        StorageLocation root = StorageLocation.FromPath(datasetRoot);
        long projectId = Assert.Single(await catalog.ListAsync(root)).Id;

        // 取下一张：拿到就带自己的租约与负责人
        TinyLabelImageTask first = Assert.IsType<TinyLabelImageTask>(await tasks.TakeNextAsync(root, projectId, userId: 7));
        Assert.Equal(7, first.LockedBy);
        Assert.Equal(7, first.AssigneeId);
        Assert.True(first.IsLockedAt(DateTime.UtcNow));

        // 别人抢同一张 → 冲突，并带回最新状态
        TinyLabelImageLockedException conflict = await Assert.ThrowsAsync<TinyLabelImageLockedException>(
            () => tasks.ClaimAsync(root, first.Id, userId: 8));
        Assert.Equal(first.Id, conflict.Current.Id);

        // after 往下走，不会把同一张再发一次
        TinyLabelImageTask second = Assert.IsType<TinyLabelImageTask>(
            await tasks.TakeNextAsync(root, projectId, userId: 7, afterImageId: first.Id));
        Assert.NotEqual(first.Id, second.Id);

        // 跳过：状态变 skipped、租约释放
        TinyLabelImageTask skipped = await tasks.SkipAsync(root, second.Id, 7, "看不清");
        Assert.Equal("skipped", skipped.State);
        Assert.Equal("看不清", skipped.Remark);
        Assert.False(skipped.IsLockedAt(DateTime.UtcNow));

        // 退回池子：回到 pending、负责人清空
        TinyLabelImageTask returned = await tasks.ReturnToPoolAsync(root, first.Id, 7, "交回去");
        Assert.Equal("pending", returned.State);
        Assert.Null(returned.AssigneeId);

        // 标注：没存过 → null；存一次 revision 0→1；再用旧版本存 → 冲突
        Assert.Null(await tasks.GetAnnotationAsync(root, first.Id));

        TinyLabelAnnotationDoc doc = await tasks.SaveAnnotationAsync(root, first.Id, expectedRevision: 0, "{\"shapes\":[]}", 7);
        Assert.Equal(1, doc.Revision);
        Assert.Equal(1, Assert.IsType<TinyLabelImageTask>(await tasks.GetAsync(root, first.Id)).Revision);

        await Assert.ThrowsAsync<TinyLabelRevisionConflictException>(
            () => tasks.SaveAnnotationAsync(root, first.Id, expectedRevision: 0, "{\"shapes\":[]}", 7));

        // 统计：5 张里 1 张 labeled、1 张 skipped，其余还是 pending
        TinyLabelProjectStats stats = Assert.IsType<TinyLabelProjectStats>(await tasks.GetStatsAsync(root, projectId));
        Assert.Equal(5, stats.Total);
        Assert.Equal(1, stats.Labeled);
        Assert.Equal(1, stats.Skipped);
        Assert.Equal(3, stats.Pending);
    }

    private static void ResetToPending(string datasetRoot)
    {
        string databasePath = Path.Combine(datasetRoot, TinyLabelProjectCatalog.DefaultDatabaseFile);

        using TinyLabelDatabase database = TinyLabelDatabase.Open(databasePath, createIfMissing: false);

        database.Orm.Update<ImageEntity>()
            .Set(row => row.State, TinyLabelTaskService.StatePending)
            .Set(row => row.Revision, 0)
            .Set(row => row.LockedBy, (long?)null)
            .Set(row => row.AssigneeId, (long?)null)
            .Where(row => row.Id > 0)
            .ExecuteAffrows();

        database.Orm.Delete<AnnotationEntity>().Where(row => row.ImageId > 0).ExecuteAffrows();
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddAnnotrainDatasets();
        services.AddLogging();

        return services.BuildServiceProvider();
    }
}
