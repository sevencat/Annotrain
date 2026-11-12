using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sevencat.Core.Dataset;
using Sevencat.Core.Simulation;
using Sevencat.Core.Storage;
using Sevencat.Train.Engine.Core;
using Sevencat.Train.Engine.Data.Images;
using Sevencat.Train.Engine.Data.Yolo;
using Sevencat.Train.Engine.Models.Yolo;
using Sevencat.Train.Engine.Training;
using Sevencat.Train.Engine.Training.Yolo;
using TorchSharp;
using static TorchSharp.torch;

namespace Sevencat.Train.Engine.Runners;

/// <summary>
/// 真训练引擎：把 <see cref="IDatasetStore"/>（YOLO / COCO / 图片目录+数据库三种形态统一后的样本）
/// 喂给 YOLO 训练循环，产出 safetensors 检查点与 <c>train.json</c>。
/// <para>
/// 与 OmniTensorSharp 的原实现差异只有一处：数据来源从「data.yaml / COCO json 直接读盘」
/// 换成 <see cref="IDatasetStore"/>，图片字节走 <see cref="IBlobStore"/>，
/// 因此本地目录、数据库、将来的远端后端都走同一条训练路径。
/// </para>
/// </summary>
public sealed class YoloDatasetTrainingRunner(
    BlobStoreRegistry registry,
    ILogger<YoloDatasetTrainingRunner>? logger = null) : IDatasetTrainingRunner
{
    /// <inheritdoc />
    public async Task<TrainingRunSummary> TrainAsync(
        IDatasetStore store,
        TrainingRunOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        int classCount = store.Classes.Count;
        if (classCount <= 0)
        {
            throw new InvalidOperationException($"{store.Id} 里没有类别定义（ClassCatalog 为空）。");
        }

        (string trainSplit, string? valSplit) = ResolveSplits(store, options);

        IReadOnlyList<DatasetSample> trainSamples = await store
            .OpenSplit(trainSplit)
            .LoadSamplesAsync(ct)
            .ConfigureAwait(false);

        if (trainSamples.Count == 0)
        {
            throw new InvalidOperationException($"split {trainSplit} 里没有样本。");
        }

        IReadOnlyList<DatasetSample> validationSamples = valSplit is null
            ? []
            : await store.OpenSplit(valSplit).LoadSamplesAsync(ct).ConfigureAwait(false);

        int totalBoxes = trainSamples.Sum(sample => sample.Boxes.Count)
                         + validationSamples.Sum(sample => sample.Boxes.Count);

        if (!options.Output.IsFile)
        {
            throw new NotSupportedException("训练产物目前只支持本地目录（file://）。");
        }

        string output = options.Output.Root;
        Directory.CreateDirectory(output);

        ComputeDevice computeDevice = DeviceFactory.Parse(options.Device);
        Device device = DeviceFactory.Create(computeDevice);
        YoloScale scale = YoloScales.Parse(options.Scale);
        YoloModelFamily family = YoloScales.ParseFamily(options.Family);
        YoloDetectionModel model = YoloArchitectures.Create(device, family, scale, classCount);

        Log(options, $"data  : {store.Id} · {trainSamples.Count} 张 · {classCount} 类 · {totalBoxes} 个框");
        Log(options, $"val   : {(valSplit is null ? "(无验证集)" : $"{validationSamples.Count} 张")}");
        Log(options, $"model : {model.Architecture} ({YoloArchitectures.CountParameters(model)} params)");
        Log(options, $"device: {DeviceFactory.DescribeDetailed(computeDevice)}");
        Log(options, $"output: {output}");

        var optimizer = torch.optim.AdamW(model.parameters(), lr: options.LearningRate, weight_decay: 0.0005f);
        var ema = new Ema(model, decay: 0.999f);
        var loss = new YoloDetectionLoss(options.ImageSize, options.ImageSize, classCount) { TrackComponents = true };

        int batchesPerEpoch = Math.Max(1, (trainSamples.Count + options.BatchSize - 1) / options.BatchSize);
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
            HorizontalFlipProbability = 0.5f,
            HueGain = 0.015f,
            SaturationGain = 0.7f,
            ValueGain = 0.4f,
        });

        using var validationLoader = new YoloSampleLoader(new YoloPreprocessOptions
        {
            Width = options.ImageSize,
            Height = options.ImageSize,
        });

        float bestMap50 = 0f;
        float lastMap50 = 0f;
        float lastMap95 = 0f;
        bool bestSaved = false;

        // 训练前先落一份「起点」模型：中途取消也能拿到可用权重。
        Save("model.safetensors", new EpochMetrics(0, 0, float.NaN, options.LearningRate));

        IReadOnlyList<EpochMetrics> history;

        // 界面传进来的控制器由界面负责释放；没传就自己建一个（含取消令牌），用完释放。
        bool ownsControl = options.Control is not TrainControl;
        TrainControl control = ownsControl ? new TrainControl(ct) : (TrainControl)options.Control!;

        try
        {
            history = trainer.Fit(
                BatchesForEpoch,
                schedule,
                metrics =>
                {
                    (float map50, float map95) = EvaluateMap(
                        model, validationSamples, validationLoader, options, classCount, device, ct);

                    lastMap50 = map50;
                    lastMap95 = map95;

                    Log(options,
                        $"epoch {metrics.Epoch + 1}/{options.Epochs}  loss {metrics.Loss:F4}  "
                        + $"mAP50 {map50:F3}  mAP50-95 {map95:F3}");

                    options.Progress?.Report(new TrainingProgress(
                        metrics.Epoch, options.Epochs, metrics.Step, metrics.Loss, metrics.LearningRate, map50, map95));

                    // 第一轮无条件存 best：mAP 全为 0（随机初始化）时也要留一份可用权重。
                    if (!bestSaved || map50 > bestMap50)
                    {
                        bestSaved = true;
                        bestMap50 = map50;
                        Save("best.safetensors", metrics, map50, map95);
                    }

                    Save("model.safetensors", metrics, map50, map95);
                },
                control);
        }
        finally
        {
            if (ownsControl)
            {
                control.Dispose();
            }
        }

        ct.ThrowIfCancellationRequested();

        EpochMetrics last = history.Count > 0
            ? history[^1]
            : new EpochMetrics(0, 0, float.NaN, options.LearningRate);

        Save("model.safetensors", last, lastMap50, lastMap95);

        Checkpoint.Save(
            Path.Combine(output, "model-ema.safetensors"),
            ema.Shadow,
            BuildMetadata(last, last.Loss, bestMap50, lastMap95, model, options.ImageSize, options.Device));

        WriteReport(output, store, options, model, history, bestMap50);

        Log(options, $"checkpoint: {Path.Combine(output, "model.safetensors")}");
        Log(options, $"report    : {Path.Combine(output, "train.json")}");

        logger?.LogInformation(
            "训练完成：{Dataset} {Epochs} 轮，best mAP50 {Map50:F3}，产物目录 {Output}",
            store.Id, options.Epochs, bestMap50, output);

        return new TrainingRunSummary(
            store.Id,
            trainSamples.Count + validationSamples.Count,
            totalBoxes,
            [.. store.Classes.Names],
            valSplit is null ? [trainSplit] : [trainSplit, valSplit],
            options.Output,
            DryRun: false,
            [])
        {
            BestMap50 = bestMap50,
            BestMap50_95 = lastMap95,
            Architecture = model.Architecture,
        };

        IEnumerable<Batch> BatchesForEpoch(int epoch)
        {
            // 每个 epoch 重新构建批次：打乱顺序 + 重新采样增强，避免增强结果被缓存。
            var rng = new Rng((ulong)(0x51ED270B + epoch * 7919));
            int[] order = Shuffle(Enumerable.Range(0, trainSamples.Count).ToArray(), rng);

            for (int start = 0; start < order.Length; start += options.BatchSize)
            {
                int count = Math.Min(options.BatchSize, order.Length - start);
                var images = new List<YoloImageSample>(count);

                for (int offset = 0; offset < count; offset++)
                {
                    DatasetSample sample = trainSamples[order[start + offset]];
                    using Stream stream = OpenImage(sample);
                    images.Add(trainLoader.Load(stream, sample.Image.RelativePath, ToLabels(sample), rng));
                }

                YoloBatchResult packed = YoloBatchBuilder.Build(images);

                yield return new Batch(
                    TorchInterop.FromArray(
                        packed.Inputs,
                        [packed.BatchSize, packed.Channels, packed.Height, packed.Width],
                        device),
                    TorchInterop.FromArray(
                        packed.Targets,
                        [packed.BatchSize, packed.MaxBoxes, YoloBatchBuilder.TargetColumns],
                        device));
            }
        }

        void Save(string fileName, EpochMetrics metrics, float map50 = 0f, float map95 = 0f)
        {
            Checkpoint.Save(
                Path.Combine(output, fileName),
                model,
                BuildMetadata(metrics, metrics.Loss, map50, map95, model, options.ImageSize, options.Device));

            // 优化器状态用 TorchSharp 自己的 bin 格式（模型权重仍是可互转的 safetensors）。
            optimizer.save_state_dict(Path.Combine(output, "optimizer.bin"));
        }
    }

    /// <summary>取训练 / 验证 split：优先显式指定，其次按名字猜（train / val）。</summary>
    private static (string Train, string? Validation) ResolveSplits(IDatasetStore store, TrainingRunOptions options)
    {
        IReadOnlyList<string> splits = options.Splits is { Count: > 0 } ? options.Splits : store.Splits;
        if (splits.Count == 0)
        {
            throw new InvalidOperationException("没有可训练的 split：去标注端给图片分配 train/val。");
        }

        string train = splits.FirstOrDefault(split => IsTrain(split)) ?? splits[0];
        string? validation = splits.FirstOrDefault(split => IsValidation(split) && !string.Equals(split, train, StringComparison.OrdinalIgnoreCase));

        return (train, validation);
    }

    private static bool IsTrain(string split)
        => split.Contains("train", StringComparison.OrdinalIgnoreCase);

    private static bool IsValidation(string split)
        => split.Contains("val", StringComparison.OrdinalIgnoreCase);

    private Stream OpenImage(DatasetSample sample)
    {
        IBlobStore blob = registry.Get(sample.Image.Location);
        var key = new StorageKey(sample.Image.Location, sample.Image.RelativePath);
        return blob.OpenReadAsync(key).GetAwaiter().GetResult();
    }

    /// <summary>像素 xyxy → 归一化 YOLO 标签（cx cy w h），并夹到 [0,1]。</summary>
    private static IReadOnlyList<YoloLabel> ToLabels(DatasetSample sample)
    {
        if (sample.Boxes.Count == 0)
        {
            return [];
        }

        if (sample.Width <= 0 || sample.Height <= 0)
        {
            throw new InvalidDataException($"图片尺寸无效：{sample.Image.RelativePath} {sample.Width}x{sample.Height}");
        }

        var labels = new List<YoloLabel>(sample.Boxes.Count);

        foreach (BoxAnnotation box in sample.Boxes)
        {
            float width = Clamp01((box.X2 - box.X1) / sample.Width);
            float height = Clamp01((box.Y2 - box.Y1) / sample.Height);
            float centerX = Clamp01((box.X1 + box.X2) / 2f / sample.Width);
            float centerY = Clamp01((box.Y1 + box.Y2) / 2f / sample.Height);

            labels.Add(new YoloLabel(box.ClassId, YoloTask.Detect, [centerX, centerY, width, height]));
        }

        return labels;
    }

    private static float Clamp01(float value) => Math.Clamp(value, 0f, 1f);

    /// <summary>在验证集上算 mAP：解码 → NMS → 与真值比对。</summary>
    private (float Map50, float Map50_95) EvaluateMap(
        YoloDetectionModel model,
        IReadOnlyList<DatasetSample> validationSamples,
        YoloSampleLoader validationLoader,
        TrainingRunOptions options,
        int classCount,
        Device device,
        CancellationToken ct)
    {
        if (validationSamples.Count == 0)
        {
            return (0f, 0f);
        }

        var predictions = new List<IReadOnlyList<YoloDetection>>(validationSamples.Count);
        var groundTruths = new List<IReadOnlyList<YoloGroundTruth>>(validationSamples.Count);

        model.eval();

        try
        {
            using (torch.no_grad())
            {
                foreach (DatasetSample sample in validationSamples)
                {
                    ct.ThrowIfCancellationRequested();

                    YoloImageSample image;
                    using (Stream stream = OpenImage(sample))
                    {
                        image = validationLoader.Load(stream, sample.Image.RelativePath, ToLabels(sample));
                    }

                    Tensor input = TorchInterop.FromArray(
                        image.ToChw(),
                        [1, 3, options.ImageSize, options.ImageSize],
                        device);

                    predictions.Add(YoloPredictionDecoder.Decode(
                        model.forward(input),
                        options.ImageSize,
                        options.ImageSize,
                        classCount,
                        DetectHeadModule.DefaultRegressionMax,
                        confidenceThreshold: 0.001f,
                        iouThreshold: 0.7f));

                    groundTruths.Add(image.Boxes
                        .Select(box => new YoloGroundTruth(box.ClassId, box.X1, box.Y1, box.X2, box.Y2))
                        .ToArray());
                }
            }
        }
        finally
        {
            model.train();
        }

        DetectionMetricsResult result = DetectionMetrics.Evaluate(predictions, groundTruths, classCount);
        return (result.Map50, result.Map50_95);
    }

    private static Dictionary<string, string> BuildMetadata(
        EpochMetrics metrics,
        float lossValue,
        float map50,
        float map95,
        YoloDetectionModel model,
        int imageSize,
        string device)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["format"] = Checkpoint.FormatName,
            ["architecture"] = model.Architecture,
            ["epoch"] = metrics.Epoch.ToString(CultureInfo.InvariantCulture),
            ["step"] = metrics.Step.ToString(CultureInfo.InvariantCulture),
            ["loss"] = lossValue.ToString("G9", CultureInfo.InvariantCulture),
            ["map50"] = map50.ToString("G6", CultureInfo.InvariantCulture),
            ["map50-95"] = map95.ToString("G6", CultureInfo.InvariantCulture),
            ["backend"] = device,
            ["classes"] = model.ClassCount.ToString(CultureInfo.InvariantCulture),
            ["image_size"] = imageSize.ToString(CultureInfo.InvariantCulture),
        };
    }

    private static void WriteReport(
        string output,
        IDatasetStore store,
        TrainingRunOptions options,
        YoloDetectionModel model,
        IReadOnlyList<EpochMetrics> history,
        float bestMap50)
    {
        var report = new
        {
            dataset = store.Id,
            format = store.Format.ToId(),
            architecture = model.Architecture,
            backend = options.Device,
            classes = store.Classes.Names,
            epochs = options.Epochs,
            batchSize = options.BatchSize,
            imageSize = options.ImageSize,
            learningRate = options.LearningRate,
            bestMap50,
            history = history.Select(item => new
            {
                item.Epoch,
                item.Step,
                item.Loss,
                item.LearningRate,
            }).ToArray(),
        };

        File.WriteAllText(
            Path.Combine(output, "train.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static int[] Shuffle(int[] order, Rng rng)
    {
        for (int i = order.Length - 1; i > 0; i--)
        {
            int j = rng.NextInt(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        return order;
    }

    private static void Log(TrainingRunOptions options, string message)
    {
        options.Log?.Invoke(message);
    }
}
