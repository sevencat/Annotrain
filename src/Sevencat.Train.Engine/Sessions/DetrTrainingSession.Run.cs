using System.Globalization;
using Sevencat.Train.Engine.Core;
using Sevencat.Train.Engine.Data.Images;
using Sevencat.Train.Engine.Data.Yolo;
using Sevencat.Train.Engine.Training;
using Sevencat.Train.Engine.Training.Detr;
using TorchSharp;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Sessions;

/// <summary>训练执行部分：模型装配、损失、优化器、逐轮评估与检查点。</summary>
public sealed partial class DetrTrainingSession
{
    private static IReadOnlyList<EpochMetrics> Run(
        DetrTrainingOptions options,
        TrainControl control,
        IProgress<TrainProgress>? progress,
        Action<string>? log)
    {
        if (options.ImageSize < 64 || options.ImageSize % 224 != 0)
        {
            throw new ArgumentException(
                $"RF-DETR 的 imgsz 必须是 224 的倍数（112 的倍数 ∪ 预处理要求的 32 的倍数），当前 {options.ImageSize}。",
                nameof(options));
        }

        Device device = DeviceFactory.Create(options.Device);

        (YoloDataset trainSet, YoloDataset? validationSet) = DetectionDatasetSource.Open(
            options.DataYaml, options.CocoJson);
        int classCount = trainSet.Config.Names.Count;

        string output = options.OutputDirectory
            ?? Path.Combine("runs", (options.UseRfDetr ? "rfdetr-" : "detr-")
                + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(output);

        Module<Tensor, Tensor> model = BuildModel(options, classCount, device, log);
        long parameters = model.parameters().Sum(parameter => parameter.numel());

        log?.Invoke($"data  : {options.Descriptor} · {trainSet.Samples.Count} 张 · {classCount} 类");
        log?.Invoke($"val   : {(validationSet is null ? "(无验证集)" : $"{validationSet.Samples.Count} 张")}");
        log?.Invoke($"model : {(options.UseRfDetr ? "rfdetr" : "detr")}-detect-{classCount} "
            + $"({parameters} params, Q={options.Queries})");
        log?.Invoke($"device: {DeviceFactory.DescribeDetailed(options.Device)}");
        log?.Invoke($"output: {output}");

        var loss = new DetrLoss(classCount, options.ImageSize, options.ImageSize, groupDetr: options.GroupDetr);
        var optimizer = torch.optim.AdamW(model.parameters(), lr: options.LearningRate, weight_decay: 1e-4f);

        int batchesPerEpoch = Math.Max(1, (trainSet.Samples.Count + options.BatchSize - 1) / options.BatchSize);
        int totalSteps = (options.Epochs * batchesPerEpoch) + 1;
        int warmupSteps = Math.Min(batchesPerEpoch, totalSteps - 1);
        var schedule = new CosineSchedule(options.LearningRate, totalSteps, warmupSteps, options.LearningRate * 0.01f);

        var trainer = new Trainer(
            model,
            optimizer,
            loss,
            new TrainerOptions { Epochs = options.Epochs, GradientClipNorm = 1f, Shuffle = false },
            new Rng(4));

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

                log?.Invoke($"epoch {metrics.Epoch + 1}/{options.Epochs}  loss {metrics.Loss:F4}  "
                    + $"mAP50 {map50:F3}  mAP50-95 {map95:F3}");

                progress?.Report(item);

                if (map50 > bestMap50)
                {
                    bestMap50 = map50;
                    Checkpoint.Save(
                        Path.Combine(output, "best.safetensors"),
                        model,
                        BuildMetadata(options, classCount, metrics));
                }
            },
            control);

        Checkpoint.Save(
            Path.Combine(output, "detr.safetensors"),
            model,
            BuildMetadata(options, classCount, history[^1]));

        WriteReport(output, options, classCount, parameters, reports, bestMap50, lastMap95);
        log?.Invoke($"checkpoint: {Path.Combine(output, "detr.safetensors")}");
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
    }
}
