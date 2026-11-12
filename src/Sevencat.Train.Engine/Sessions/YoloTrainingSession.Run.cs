using System.Globalization;
using Sevencat.Train.Engine.Core;
using Sevencat.Train.Engine.Data.Images;
using Sevencat.Train.Engine.Data.Yolo;
using Sevencat.Train.Engine.Models.Yolo;
using Sevencat.Train.Engine.Training;
using Sevencat.Train.Engine.Training.Yolo;
using TorchSharp;
using static TorchSharp.torch;

namespace Sevencat.Train.Engine.Sessions;

/// <summary>训练执行部分：数据集装配、损失、优化器与训练循环（与 CLI 的 yolo train 同一条路径）。</summary>
public sealed partial class YoloTrainingSession
{
    private static IReadOnlyList<EpochMetrics> Run(
        YoloTrainingOptions options,
        TrainControl control,
        IProgress<TrainProgress>? progress,
        Action<string>? log)
    {
        Device device = DeviceFactory.Create(options.Device);

        // YOLO（data.yaml）与 COCO（json）在这里统一成同一份样本列表。
        (YoloDataset trainSet, YoloDataset? validationSet) = DetectionDatasetSource.Open(
            options.DataYaml, options.CocoJson);
        int classCount = trainSet.Config.Names.Count;

        if (classCount <= 0)
            throw new InvalidOperationException($"{options.Descriptor} 里没有类别定义（names 为空）。");

        string output = options.OutputDirectory
            ?? Path.Combine("runs", "gui-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(output);

        YoloScale scale = YoloScales.Parse(options.Scale);
        YoloModelFamily family = YoloScales.ParseFamily(options.Family);
        YoloDetectionModel model = YoloArchitectures.Create(device, family, scale, classCount);

        log?.Invoke($"data  : {options.Descriptor} · {trainSet.Samples.Count} 张 · {classCount} 类");
        log?.Invoke($"val   : {(validationSet is null ? "(无验证集)" : $"{validationSet.Samples.Count} 张")}");
        log?.Invoke($"model : {model.Architecture} ({YoloArchitectures.CountParameters(model)} params)");
        log?.Invoke($"device: {DeviceFactory.DescribeDetailed(options.Device)}");
        log?.Invoke($"output: {output}");

        var optimizer = torch.optim.AdamW(model.parameters(), lr: options.LearningRate, weight_decay: 0.0005f);
        var ema = new Ema(model, decay: 0.999f);
        var loss = new YoloDetectionLoss(options.ImageSize, options.ImageSize, classCount) { TrackComponents = true };

        int batchesPerEpoch = Math.Max(1, (trainSet.Samples.Count + options.BatchSize - 1) / options.BatchSize);
        int totalSteps = (options.Epochs * batchesPerEpoch) + 1;
        int warmupSteps = Math.Min(Math.Max(1, 3 * batchesPerEpoch), Math.Max(0, totalSteps - 1));
        var schedule = new CosineSchedule(options.LearningRate, totalSteps, warmupSteps, options.LearningRate * 0.01f);

        var trainer = new Trainer(
            model,
            optimizer,
            loss,
            new TrainerOptions { Epochs = options.Epochs, GradientClipNorm = 10f, Shuffle = false },
            new Rng(1),
            ema);

        using var trainLoader = new YoloSampleLoader(new YoloPreprocessOptions
        {
            Width = options.ImageSize,
            Height = options.ImageSize,
        });
        using var validationLoader = new YoloSampleLoader(new YoloPreprocessOptions
        {
            Width = options.ImageSize,
            Height = options.ImageSize,
        });

        var reports = new List<TrainProgress>(options.Epochs);
        float bestMap50 = 0f;
        float lastMap95 = 0f;

        IReadOnlyList<EpochMetrics> history = trainer.Fit(
            _ => BuildEpochBatches(),
            schedule,
            metrics =>
            {
                (float map50, float map95) = EvaluateMap(
                    model, validationSet, validationLoader, options, classCount, device);

                var item = new TrainProgress(
                    metrics.Epoch, options.Epochs, metrics.Step, metrics.Loss, metrics.LearningRate, map50, map95);

                reports.Add(item);
                lastMap95 = map95;

                log?.Invoke(
                    $"epoch {metrics.Epoch + 1}/{options.Epochs}  loss {metrics.Loss:F4}  "
                    + $"mAP50 {map50:F3}  mAP50-95 {map95:F3}");

                // 先报进度再存盘：曲线不必等磁盘 IO。
                progress?.Report(item);

                if (map50 > bestMap50)
                {
                    bestMap50 = map50;
                    Save("best.safetensors", metrics, map50, map95);
                }

                if (options.SaveEvery > 0 && (metrics.Epoch + 1) % options.SaveEvery == 0)
                    Save("model.safetensors", metrics, map50, map95);
            },
            control);

        EpochMetrics last = history[^1];
        Save("model.safetensors", last, bestMap50, lastMap95);

        Checkpoint.Save(
            Path.Combine(output, "model-ema.safetensors"),
            ema.Shadow,
            BuildMetadata(last, last.Loss, bestMap50, lastMap95, model, options.ImageSize, options.Device));

        WriteReport(output, options, model, reports, bestMap50);
        log?.Invoke($"checkpoint: {Path.Combine(output, "model.safetensors")}");
        log?.Invoke($"report    : {Path.Combine(output, "train.json")}");

        return history;

        IEnumerable<Batch> BuildEpochBatches()
        {
            IReadOnlyList<YoloSample> samples = trainSet.Samples;

            for (int start = 0; start < samples.Count; start += options.BatchSize)
            {
                int count = Math.Min(options.BatchSize, samples.Count - start);
                var images = new List<YoloImageSample>(count);

                for (int offset = 0; offset < count; offset++)
                    images.Add(trainLoader.Load(samples[start + offset]));

                YoloBatchResult packed = YoloBatchBuilder.Build(images);

                yield return new Batch(
                    TorchInterop.FromArray(
                        packed.Inputs,
                        new long[] { packed.BatchSize, packed.Channels, packed.Height, packed.Width },
                        device),
                    TorchInterop.FromArray(
                        packed.Targets,
                        new long[] { packed.BatchSize, packed.MaxBoxes, YoloBatchBuilder.TargetColumns },
                        device));
            }
        }

        void Save(string fileName, EpochMetrics metrics, float map50, float map95)
        {
            Checkpoint.Save(
                Path.Combine(output, fileName),
                model,
                BuildMetadata(metrics, metrics.Loss, map50, map95, model, options.ImageSize, options.Device));

            // 优化器状态用 TorchSharp 自己的 bin 格式（模型权重仍是可互转的 safetensors）。
            optimizer.save_state_dict(Path.Combine(output, "optimizer.bin"));
        }
    }
}


