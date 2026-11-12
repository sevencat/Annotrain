namespace Sevencat.Train.Engine.Training.Yolo;

/// <summary>mAP 评估结果；<see cref="AveragePrecision50"/> 按类别索引，未出现的类别为 0。</summary>
public sealed record DetectionMetricsResult(
    float Map50,
    float Map50_95,
    IReadOnlyList<float> AveragePrecision50);

/// <summary>
/// 检测指标：mAP50 与 mAP50-95（IoU 0.50 → 0.95，步长 0.05）。
/// <para>
/// 实现是 VOC 风格的全点插值（precision 包络 + recall 增量积分），
/// 与 COCO 的 101 点插值可能有千分位差异，但对「是否学到了目标」这个判断足够；
/// 每个类别独立匹配，未在 GT 中出现的类别不参与平均。
/// </para>
/// </summary>
public static class DetectionMetrics
{
    private const int IouThresholdCount = 10;

    /// <summary>按图像列表计算指标；<paramref name="predictions"/> 与 GT 必须一一对应。</summary>
    public static DetectionMetricsResult Evaluate(
        IReadOnlyList<IReadOnlyList<YoloDetection>> predictions,
        IReadOnlyList<IReadOnlyList<YoloGroundTruth>> groundTruths,
        int classCount,
        int maxDetectionsPerImage = 100)
    {
        ArgumentNullException.ThrowIfNull(predictions);
        ArgumentNullException.ThrowIfNull(groundTruths);

        if (predictions.Count != groundTruths.Count)
        {
            throw new ArgumentException(
                $"预测 {predictions.Count} 张图与真值 {groundTruths.Count} 张图数量不一致。",
                nameof(predictions));
        }

        if (classCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(classCount), classCount, "类别数必须大于 0。");
        if (maxDetectionsPerImage <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDetectionsPerImage), maxDetectionsPerImage, "上限必须大于 0。");

        var groundTruthByImage = new IReadOnlyList<YoloGroundTruth>?[groundTruths.Count];
        for (int image = 0; image < groundTruths.Count; image++)
            groundTruthByImage[image] = groundTruths[image] ?? Array.Empty<YoloGroundTruth>();

        var ap50 = new float[classCount];
        var classesWithGt = new List<int>(classCount);

        for (int classId = 0; classId < classCount; classId++)
        {
            if (!groundTruthByImage.Any(gt => gt!.Any(item => item.ClassId == classId)))
                continue;

            classesWithGt.Add(classId);
            ap50[classId] = AveragePrecision(
                predictions,
                groundTruthByImage!,
                classId,
                0.5f,
                maxDetectionsPerImage);
        }

        if (classesWithGt.Count == 0)
            return new DetectionMetricsResult(0f, 0f, ap50);

        double map50 = 0;
        double mapAll = 0;

        foreach (int classId in classesWithGt)
        {
            for (int index = 0; index < IouThresholdCount; index++)
            {
                float threshold = 0.5f + index * 0.05f;
                float ap = index == 0
                    ? ap50[classId]
                    : AveragePrecision(predictions, groundTruthByImage!, classId, threshold, maxDetectionsPerImage);

                mapAll += ap;
            }

            map50 += ap50[classId];
        }

        return new DetectionMetricsResult(
            (float)(map50 / classesWithGt.Count),
            (float)(mapAll / (classesWithGt.Count * IouThresholdCount)),
            ap50);
    }

    private static float AveragePrecision(
        IReadOnlyList<IReadOnlyList<YoloDetection>> predictions,
        IReadOnlyList<YoloGroundTruth>?[] groundTruthByImage,
        int classId,
        float iouThreshold,
        int maxDetectionsPerImage)
    {
        var detections = new List<(float Score, int Image, YoloDetection Box)>();
        var matched = new bool[groundTruthByImage.Length][];
        int groundTruthCount = 0;

        for (int image = 0; image < groundTruthByImage.Length; image++)
        {
            IReadOnlyList<YoloGroundTruth> imageGroundTruth = groundTruthByImage[image]!;
            int classCount = 0;

            foreach (YoloGroundTruth item in imageGroundTruth)
            {
                if (item.ClassId == classId)
                    classCount++;
            }

            matched[image] = new bool[classCount];
            groundTruthCount += classCount;

            foreach (YoloDetection detection in predictions[image]
                         .Where(item => item.ClassId == classId)
                         .OrderByDescending(item => item.Score)
                         .Take(maxDetectionsPerImage))
            {
                detections.Add((detection.Score, image, detection));
            }
        }

        if (groundTruthCount == 0)
            return float.NaN;

        detections.Sort((left, right) => right.Score.CompareTo(left.Score));

        var truePositives = new bool[detections.Count];

        for (int index = 0; index < detections.Count; index++)
        {
            (_, int image, YoloDetection box) = detections[index];
            IReadOnlyList<YoloGroundTruth> imageGroundTruth = groundTruthByImage[image]!;

            int bestIndex = -1;
            float bestIou = iouThreshold;
            int groundTruthIndex = 0;

            foreach (YoloGroundTruth gt in imageGroundTruth)
            {
                if (gt.ClassId != classId)
                    continue;

                if (!matched[image][groundTruthIndex])
                {
                    float iou = Iou(box, gt);
                    if (iou >= bestIou)
                    {
                        bestIou = iou;
                        bestIndex = groundTruthIndex;
                    }
                }

                groundTruthIndex++;
            }

            if (bestIndex >= 0)
            {
                matched[image][bestIndex] = true;
                truePositives[index] = true;
            }
        }

        double truePositive = 0;
        double falsePositive = 0;
        var precisions = new double[detections.Count];
        var recalls = new double[detections.Count];

        for (int index = 0; index < detections.Count; index++)
        {
            if (truePositives[index])
                truePositive++;
            else
                falsePositive++;

            precisions[index] = truePositive / (truePositive + falsePositive);
            recalls[index] = truePositive / groundTruthCount;
        }

        for (int index = precisions.Length - 2; index >= 0; index--)
            precisions[index] = Math.Max(precisions[index], precisions[index + 1]);

        double averagePrecision = 0;
        double previousRecall = 0;

        for (int index = 0; index < detections.Count; index++)
        {
            averagePrecision += (recalls[index] - previousRecall) * precisions[index];
            previousRecall = recalls[index];
        }

        return (float)averagePrecision;
    }

    private static float Iou(YoloDetection detection, YoloGroundTruth groundTruth)
    {
        float width = MathF.Min(detection.X2, groundTruth.X2) - MathF.Max(detection.X1, groundTruth.X1);
        float height = MathF.Min(detection.Y2, groundTruth.Y2) - MathF.Max(detection.Y1, groundTruth.Y1);

        if (width <= 0f || height <= 0f)
            return 0f;

        float intersection = width * height;
        float union = (detection.X2 - detection.X1) * (detection.Y2 - detection.Y1)
            + (groundTruth.X2 - groundTruth.X1) * (groundTruth.Y2 - groundTruth.Y1)
            - intersection;

        return union <= 0f ? 0f : intersection / union;
    }
}

