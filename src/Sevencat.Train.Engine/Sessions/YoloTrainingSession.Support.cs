using System.Globalization;
using System.Text.Json;
using Sevencat.Train.Engine.Core;
using Sevencat.Train.Engine.Data.Images;
using Sevencat.Train.Engine.Data.Yolo;
using Sevencat.Train.Engine.Models.Yolo;
using Sevencat.Train.Engine.Training;
using Sevencat.Train.Engine.Training.Yolo;
using TorchSharp;
using static TorchSharp.torch;

namespace Sevencat.Train.Engine.Sessions;

/// <summary>训练辅助：验证集评估、检查点元数据、报告落盘。</summary>
public sealed partial class YoloTrainingSession
{
    /// <summary>在验证集上算 mAP：解码 → NMS → 与真值比对（与 CLI 的评估口径一致）。</summary>
    private static (float Map50, float Map50_95) EvaluateMap(
        YoloDetectionModel model,
        YoloDataset? validationSet,
        YoloSampleLoader validationLoader,
        YoloTrainingOptions options,
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

                predictions.Add(YoloPredictionDecoder.Decode(
                    model.forward(input),
                    options.ImageSize,
                    options.ImageSize,
                    classCount,
                    DetectHeadModule.DefaultRegressionMax,
                    options.Confidence,
                    options.IouThreshold));

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

    /// <summary>检查点元数据：字段名与 CLI 保持一致，续训/导出的读取逻辑才通用。</summary>
    private static Dictionary<string, string> BuildMetadata(
        EpochMetrics metrics,
        float lossValue,
        float map50,
        float map95,
        YoloDetectionModel model,
        int imageSize,
        ComputeDevice device)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["format"] = "omnits-checkpoint/1",
            // 必须是 YoloArchitectures 能解析的格式（yolov8n-detect-3 / yolo11n-detect-3），
            // 否则推理与导出在重建模型时会拒绝加载。
            ["architecture"] = model.Architecture,
            ["epoch"] = metrics.Epoch.ToString(CultureInfo.InvariantCulture),
            ["step"] = metrics.Step.ToString(CultureInfo.InvariantCulture),
            ["loss"] = lossValue.ToString("G9", CultureInfo.InvariantCulture),
            ["map50"] = map50.ToString("G6", CultureInfo.InvariantCulture),
            ["map50-95"] = map95.ToString("G6", CultureInfo.InvariantCulture),
            ["backend"] = DeviceFactory.Describe(device),
            ["classes"] = model.ClassCount.ToString(CultureInfo.InvariantCulture),
            ["image_size"] = imageSize.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>写 train.json：字段与 CLI 的报告同名，界面和脚本都能直接读。</summary>
    private static void WriteReport(
        string output,
        YoloTrainingOptions options,
        YoloDetectionModel model,
        List<TrainProgress> reports,
        float bestMap50)
    {
        var report = new
        {
            architecture = model.Architecture,
            backend = DeviceFactory.Describe(options.Device),
            data = options.Descriptor,
            classes = model.ClassCount,
            epochs = options.Epochs,
            batchSize = options.BatchSize,
            imageSize = options.ImageSize,
            learningRate = options.LearningRate,
            bestMap50,
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
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}

