using Microsoft.Extensions.DependencyInjection;
using Sevencat.Core.Dataset;
using Sevencat.Core.Simulation;
using Sevencat.Core.Storage;
using Sevencat.Train.Engine.Runners;
using Xunit;

namespace Sevencat.Train.Engine.Tests;

/// <summary>
/// 真训练冒烟：用模拟数据集（图片目录 + SQLite）跑 1 个 epoch，
/// 确认「数据集 → 批次 → 损失 → 检查点」这条链路真的通。
/// </summary>
public class YoloTrainingRunnerTests
{
    [Fact]
    public async Task Train_OneEpoch_WritesCheckpointAndReport()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "annotrain-train", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        try
        {
            var services = new ServiceCollection();
            services.AddAnnotrainDatasets();
            services.AddLogging();
            services.AddSingleton<YoloDatasetTrainingRunner>();
            using ServiceProvider provider = services.BuildServiceProvider();

            var registry = provider.GetRequiredService<BlobStoreRegistry>();
            var generator = new SyntheticImageGenerator(new SyntheticOptions(
                ImageCount: 4,
                Width: 64,
                Height: 64,
                MinShapes: 1,
                MaxShapes: 2,
                Seed: 5));

            IReadOnlyList<SyntheticImage> images = generator.Generate();
            string datasetRoot = Path.Combine(workspace, "dataset");

            await SyntheticLabelingSession.ImportAsync(
                images,
                registry,
                new SyntheticImportOptions(datasetRoot));

            DatasetStoreResolver resolver = provider.GetRequiredService<DatasetStoreResolver>();
            DatasetOpenResult opened = await resolver.OpenAsync(StorageLocation.FromPath(datasetRoot));

            try
            {
                string output = Path.Combine(workspace, "runs");
                var messages = new List<string>();

                TrainingRunSummary summary = await provider
                    .GetRequiredService<YoloDatasetTrainingRunner>()
                    .TrainAsync(
                        opened.Store,
                        new TrainingRunOptions(
                            StorageLocation.FromPath(output),
                            Epochs: 1,
                            BatchSize: 2,
                            ImageSize: 64)
                        {
                            Log = message => messages.Add(message),
                        });

                Assert.False(summary.DryRun);
                Assert.Equal(4, summary.Images);
                Assert.Equal(SyntheticImageGenerator.ClassNames.Count, summary.Classes.Count);
                Assert.NotNull(summary.Architecture);
                Assert.True(summary.BestMap50 >= 0f && summary.BestMap50 <= 1f);

                Assert.True(File.Exists(Path.Combine(output, "model.safetensors")), "缺少 model.safetensors");
                Assert.True(File.Exists(Path.Combine(output, "best.safetensors")), "缺少 best.safetensors");
                Assert.True(File.Exists(Path.Combine(output, "train.json")), "缺少 train.json");
                Assert.True(File.Exists(Path.Combine(output, "optimizer.bin")), "缺少 optimizer.bin");

                Assert.Contains(messages, message => message.StartsWith("epoch 1/1", StringComparison.Ordinal));
            }
            finally
            {
                (opened.Store as IDisposable)?.Dispose();
            }
        }
        finally
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch (IOException)
            {
                // 训练产物可能仍被占用：临时目录留给系统清理
            }
        }
    }
}
