namespace Sevencat.Train.Engine.Training.Yolo;

/// <summary>
/// 非极大值抑制：按类别分组，分数从高到低贪心保留，IoU 超过阈值的候选被抑制。
/// <para>评估与推理共用；输入输出都是网络输入像素坐标。</para>
/// </summary>
public static class YoloNms
{
    /// <summary>执行 NMS，返回按分数降序的前 <paramref name="maxDetections"/> 个框。</summary>
    public static IReadOnlyList<YoloDetection> Apply(
        IReadOnlyList<YoloDetection> detections,
        float iouThreshold = 0.45f,
        int maxDetections = 300)
    {
        ArgumentNullException.ThrowIfNull(detections);
        if (iouThreshold is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(iouThreshold), iouThreshold, "IoU 阈值必须在 [0,1]。");
        if (maxDetections <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDetections), maxDetections, "maxDetections 必须大于 0。");

        var kept = new List<YoloDetection>();

        foreach (IGrouping<int, YoloDetection> group in detections.GroupBy(detection => detection.ClassId))
        {
            var candidates = group.OrderByDescending(detection => detection.Score).ToArray();
            var selected = new List<YoloDetection>();

            foreach (YoloDetection candidate in candidates)
            {
                bool suppressed = false;
                foreach (YoloDetection chosen in selected)
                {
                    if (Iou(chosen, candidate) > iouThreshold)
                    {
                        suppressed = true;
                        break;
                    }
                }

                if (!suppressed)
                    selected.Add(candidate);
            }

            kept.AddRange(selected);
        }

        return kept
            .OrderByDescending(detection => detection.Score)
            .Take(maxDetections)
            .ToArray();
    }

    /// <summary>两个 xyxy 框的 IoU。</summary>
    public static float Iou(YoloDetection left, YoloDetection right)
    {
        float width = MathF.Min(left.X2, right.X2) - MathF.Max(left.X1, right.X1);
        float height = MathF.Min(left.Y2, right.Y2) - MathF.Max(left.Y1, right.Y1);

        if (width <= 0f || height <= 0f)
            return 0f;

        float intersection = width * height;
        float union = left.Width * left.Height + right.Width * right.Height - intersection;
        return union <= 0f ? 0f : intersection / union;
    }
}
