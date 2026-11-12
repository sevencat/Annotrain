using Sevencat.Train.Engine.Core;
using Sevencat.Train.Engine.Data.Images;
using Sevencat.Train.Engine.Data.Yolo;
using Sevencat.Train.Engine.Models.RfDetr;
using Sevencat.Train.Engine.Models.SimpleDetr;
using Sevencat.Train.Engine.Training;
using Sevencat.Train.Engine.Training.Detr;
using Sevencat.Train.Engine.Training.Yolo;
using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Sessions;

/// <summary>训练辅助：模型装配与验证集评估。</summary>
public sealed partial class DetrTrainingSession
{
    /// <summary>按参数装配 RF-DETR 或简化 DETR。</summary>
    private static Module<Tensor, Tensor> BuildModel(
        DetrTrainingOptions options, int classCount, Device device, Action<string>? log)
    {
        if (!options.UseRfDetr)
            return new SimpleDetrModel(classCount, options.Queries, options.EncoderLayers, options.DecoderLayers, device: device);

        // pos_embed 用 518 以匹配官方权重形状；实际输入更小时走双三次插值。
        var dinoConfig = new DinoV2Config(
            ImageSize: 518,
            EmbeddingSize: options.DinoEmbedding,
            Depth: options.DinoDepth,
            Heads: options.DinoHeads);

        var detector = new RfDetrDetector(classCount, options.Queries, dinoConfig, options.ModelHeads, device);

        if (!string.IsNullOrWhiteSpace(options.DinoWeights))
        {
            string weightsPath = Path.GetFullPath(options.DinoWeights);

            if (!File.Exists(weightsPath))
                throw new FileNotFoundException($"DINOv2 权重不存在：{weightsPath}", weightsPath);

            using Checkpoint dino = Checkpoint.Open(weightsPath);
            var prefixed = new Dictionary<string, Tensor>(StringComparer.Ordinal);

            foreach ((string key, Tensor tensor) in dino.LoadState(device))
                prefixed["backbone." + key] = tensor;

            detector.load_state_dict(prefixed, strict: false);
            log?.Invoke($"dino  : {weightsPath}（{prefixed.Count} 个张量载入 backbone）");
        }

        return detector;
    }

    /// <summary>验证集上算 mAP：解码 → 与真值比对（与 CLI 的 DETR 评估口径一致）。</summary>
    private static (float Map50, float Map50_95) EvaluateMap(
        Module<Tensor, Tensor> model,
        YoloDataset? validationSet,
        YoloSampleLoader validationLoader,
        DetrTrainingOptions options,
        int classCount,
        Device device)
    {
        if (validationSet is null || options.EvalEvery <= 0)
            return (0f, 0f);

        IEnumerable<YoloSample> samples = validationSet.Samples;
        if (options.EvaluationLimit > 0)
            samples = samples.Take(options.EvaluationLimit);

        var predictions = new List<IReadOnlyList<YoloDetection>>();
        var groundTruths = new List<IReadOnlyList<YoloGroundTruth>>();

        model.eval();

        using (torch.no_grad())
        {
            foreach (YoloSample sample in samples)
            {
                YoloImageSample image = validationLoader.Load(sample);
                Tensor input = TorchInterop.FromArray(
                    image.ToChw(),
                    new long[] { 1, 3, options.ImageSize, options.ImageSize },
                    device);

                predictions.Add(DetrPostProcess.Decode(
                    model.forward(input), options.ImageSize, options.ImageSize, classCount, options.Confidence)[0]);

                groundTruths.Add(image.Boxes
                    .Select(box => new YoloGroundTruth(box.ClassId, box.X1, box.Y1, box.X2, box.Y2))
                    .ToArray());
            }
        }

        model.train();

        if (predictions.Count == 0)
            return (0f, 0f);

        DetectionMetricsResult result = DetectionMetrics.Evaluate(predictions, groundTruths, classCount);
        return (result.Map50, result.Map50_95);
    }

    /// <summary>检查点元数据：与 CLI 的 detr 检查点同名字段，导出器可直接复用。</summary>
    private static Dictionary<string, string> BuildMetadata(
        DetrTrainingOptions options, int classCount, EpochMetrics metrics)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["format"] = "omnits-checkpoint/1",
            ["architecture"] = (options.UseRfDetr ? "rfdetr" : "simple-detr") + $"-detect-{classCount}",
            ["epoch"] = metrics.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["step"] = metrics.Step.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["loss"] = metrics.Loss.ToString("G9", System.Globalization.CultureInfo.InvariantCulture),
            ["queries"] = options.Queries.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["encoder_layers"] = options.EncoderLayers.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["decoder_layers"] = options.DecoderLayers.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["heads"] = options.ModelHeads.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["dino_embed"] = options.DinoEmbedding.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["dino_depth"] = options.DinoDepth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["dino_heads"] = options.DinoHeads.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["image_size"] = options.ImageSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["blocks"] = "2,2,2,2",
            ["classes"] = classCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["backend"] = DeviceFactory.Describe(options.Device),
        };
    }

    /// <summary>写 train.json（字段与 CLI 报告同名，界面与脚本都能直接读）。</summary>
    private static void WriteReport(
        string output,
        DetrTrainingOptions options,
        int classCount,
        long parameters,
        List<TrainProgress> reports,
        float bestMap50,
        float lastMap95)
    {
        var report = new
        {
            architecture = (options.UseRfDetr ? "rfdetr" : "simple-detr") + $"-detect-{classCount}",
            backend = DeviceFactory.Describe(options.Device),
            data = options.Descriptor,
            classes = classCount,
            queries = options.Queries,
            epochs = options.Epochs,
            batchSize = options.BatchSize,
            imageSize = options.ImageSize,
            learningRate = options.LearningRate,
            parameters,
            bestMap50,
            bestMap50_95 = lastMap95,
            history = reports.Select(item => new
            {
                item.Epoch,
                item.Step,
                item.Loss,
                Map50 = item.Map50,
                Map50_95 = item.Map50_95,
            }).ToArray(),
        };

        File.WriteAllText(
            Path.Combine(output, "train.json"),
            System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}
