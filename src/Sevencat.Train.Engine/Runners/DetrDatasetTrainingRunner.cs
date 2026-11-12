using Microsoft.Extensions.Logging;
using Sevencat.Core.Dataset;
using Sevencat.Core.Dataset.Export;
using Sevencat.Core.Simulation;
using Sevencat.Core.Storage;
using Sevencat.Train.Engine.Core;
using Sevencat.Train.Engine.Sessions;
using Sevencat.Train.Engine.Training;

namespace Sevencat.Train.Engine.Runners;

/// <summary>
/// RF-DETR 训练入口：DETR 的数据加载器读盘上的 data.yaml，所以先把
/// <see cref="IDatasetStore"/>（YOLO / COCO / 数据库都行）导出成一份临时 YOLO 数据集，
/// 再交给 <see cref="DetrTrainingSession"/>。训练完删掉临时目录，
/// 检查点与 <c>train.json</c> 落在 <see cref="TrainingRunOptions.Output"/>。
/// </summary>
public sealed class DetrDatasetTrainingRunner(
    DatasetExportService exporter,
    ILogger<DetrDatasetTrainingRunner>? logger = null) : IDatasetTrainingRunner
{
    /// <summary>DETR 的输入边长必须是 224 的倍数（patch 14 × 下采样 16 与 32 的公倍数）。</summary>
    private const int ImageSize = 224;

    /// <summary>DETR 显存占用高，批大小按上限截断，避免直接 OOM。</summary>
    private const int MaxBatchSize = 4;

    /// <inheritdoc />
    public async Task<TrainingRunSummary> TrainAsync(
        IDatasetStore store,
        TrainingRunOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        if (store.Classes.Count == 0)
        {
            throw new InvalidOperationException($"{store.Id} 里没有类别定义（ClassCatalog 为空）。");
        }

        if (!options.Output.IsFile)
        {
            throw new NotSupportedException("训练产物目前只支持本地目录（file://）。");
        }

        string[] splits = options.Splits is { Count: > 0 } ? [.. options.Splits] : [.. store.Splits];

        if (splits.Length == 0)
        {
            throw new InvalidOperationException("数据集里没有可训练的 split（先在标注端划分 train / val）。");
        }

        string output = options.Output.Root;
        Directory.CreateDirectory(output);

        string staging = Path.Combine(Path.GetTempPath(), "sevencat-detr-" + Guid.NewGuid().ToString("N"));

        try
        {
            Log(options, "RF-DETR：先把数据集导出成临时 YOLO 目录（DETR 数据加载器读 data.yaml）…");

            DatasetExportReport report = await exporter
                .ExportAsync(
                    store,
                    new DatasetExportOptions(
                        DatasetFormat.Yolo,
                        StorageLocation.FromPath(staging),
                        splits,
                        CopyImages: true),
                    ct)
                .ConfigureAwait(false);

            Log(options, $"export: {report.Summary}");

            var detrOptions = new DetrTrainingOptions
            {
                DataYaml = Path.Combine(staging, "data.yaml"),
                Epochs = options.Epochs,
                BatchSize = Math.Clamp(options.BatchSize, 1, MaxBatchSize),
                ImageSize = ImageSize,
                LearningRate = 1e-3f,
                OutputDirectory = output,
                Device = DeviceFactory.Parse(options.Device),
                UseRfDetr = true,
            };

            using var session = new DetrTrainingSession();
            using CancellationTokenRegistration cancellation = ct.Register(session.Stop);

            float bestMap50 = 0;
            float bestMap95 = 0;

            IProgress<TrainProgress>? progress = options.Progress is null
                ? null
                : new Progress<TrainProgress>(item =>
                {
                    bestMap50 = Math.Max(bestMap50, item.Map50);
                    bestMap95 = Math.Max(bestMap95, item.Map50_95);

                    options.Progress.Report(new TrainingProgress(
                        item.Epoch,
                        item.TotalEpochs,
                        item.Step,
                        item.Loss,
                        item.LearningRate,
                        item.Map50,
                        item.Map50_95));
                });

            Task<IReadOnlyList<EpochMetrics>> training = session.StartAsync(detrOptions, progress, options.Log);

            // 外部的暂停 / 继续转发给 DETR 会话：训练线程只在批次边界响应，转发是安全的
            using var bridgeCancellation = new CancellationTokenSource();
            Task bridge = Task.Run(
                async () =>
                {
                    while (!bridgeCancellation.IsCancellationRequested)
                    {
                        if (options.Control is { } control)
                        {
                            if (control.IsPaused)
                            {
                                session.Pause();
                            }
                            else
                            {
                                session.Resume();
                            }
                        }

                        try
                        {
                            await Task.Delay(150, bridgeCancellation.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                },
                CancellationToken.None);

            try
            {
                await training.ConfigureAwait(false);
            }
            finally
            {
                await bridgeCancellation.CancelAsync().ConfigureAwait(false);
                await bridge.ConfigureAwait(false);
            }

            return new TrainingRunSummary(
                store.Id,
                report.ImagesExported,
                report.BoxesExported,
                [.. store.Classes.Names],
                splits,
                options.Output,
                DryRun: false,
                report.Warnings)
            {
                BestMap50 = bestMap50,
                BestMap50_95 = bestMap95,
                Architecture = $"rfdetr-detect-{store.Classes.Count}",
            };
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private void Log(TrainingRunOptions options, string message)
    {
        options.Log?.Invoke(message);
        logger?.LogInformation("RF-DETR {Message}", message);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响训练结果
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// 按 <see cref="TrainingRunOptions.Family"/> 把训练分发给 YOLO 或 RF-DETR 运行器
/// （<c>rfdetr</c> 走 DETR，其余走 YOLO）。
/// </summary>
public sealed class DatasetTrainingRunnerDispatcher(
    YoloDatasetTrainingRunner yolo,
    DetrDatasetTrainingRunner detr) : IDatasetTrainingRunner
{
    /// <inheritdoc />
    public Task<TrainingRunSummary> TrainAsync(
        IDatasetStore store,
        TrainingRunOptions options,
        CancellationToken ct = default)
        => string.Equals(options.Family, "rfdetr", StringComparison.OrdinalIgnoreCase)
            ? detr.TrainAsync(store, options, ct)
            : yolo.TrainAsync(store, options, ct);
}
