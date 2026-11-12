using Sevencat.Core.Model;

namespace Sevencat.Core.AutoLabel;

/// <summary>YOLO 输出解码所需的几何参数（写给 <see cref="YoloOutputDecoder"/> 用）。</summary>
/// <param name="Confidence">置信度阈值。</param>
/// <param name="IouThreshold">NMS 的 IoU 阈值。</param>
/// <param name="Scale">原图 → letterbox 输入的缩放比。</param>
/// <param name="PadX">左侧填充像素。</param>
/// <param name="PadY">顶部填充像素。</param>
/// <param name="ImageWidth">原图宽。</param>
/// <param name="ImageHeight">原图高。</param>
public sealed record YoloDecodeOptions(
    double Confidence,
    double IouThreshold,
    double Scale,
    int PadX,
    int PadY,
    int ImageWidth,
    int ImageHeight);

/// <summary>
/// YOLOv8/v11 输出解码：<c>[1, 4+classes, N]</c>（channel-first）或 <c>[1, N, 4+classes]</c>。
/// 纯数组运算，不依赖 ONNX，便于单测。
/// </summary>
public static class YoloOutputDecoder
{
    public static IReadOnlyList<AutoLabelCandidate> Decode(
        ReadOnlySpan<float> data,
        int channels,
        int count,
        bool channelFirst,
        YoloDecodeOptions options,
        IReadOnlyList<string>? classNames = null,
        IReadOnlyDictionary<int, string>? classMap = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        int classCount = channels - 4;
        if (classCount <= 0 || count <= 0 || data.Length < (long)channels * count)
        {
            return [];
        }

        var candidates = new List<AutoLabelCandidate>();

        for (int index = 0; index < count; index++)
        {
            float centerX, centerY, width, height;

            if (channelFirst)
            {
                centerX = data[index];
                centerY = data[count + index];
                width = data[(2 * count) + index];
                height = data[(3 * count) + index];
            }
            else
            {
                int offset = index * channels;
                centerX = data[offset];
                centerY = data[offset + 1];
                width = data[offset + 2];
                height = data[offset + 3];
            }

            int bestClass = -1;
            double bestScore = 0;

            for (int classId = 0; classId < classCount; classId++)
            {
                float score = channelFirst
                    ? data[((4 + classId) * count) + index]
                    : data[(index * channels) + 4 + classId];

                if (score > bestScore)
                {
                    bestScore = score;
                    bestClass = classId;
                }
            }

            if (bestClass < 0 || bestScore < options.Confidence)
            {
                continue;
            }

            if (!TryMapBox(centerX, centerY, width, height, options, out RectD box))
            {
                continue;
            }

            candidates.Add(new AutoLabelCandidate(
                box,
                ResolveLabel(bestClass, classNames, classMap),
                bestClass,
                bestScore));
        }

        return NonMaximumSuppression(candidates, options.IouThreshold);
    }

    /// <summary>letterbox 坐标 → 原图坐标，并裁掉出界部分。</summary>
    private static bool TryMapBox(
        float centerX,
        float centerY,
        float width,
        float height,
        YoloDecodeOptions options,
        out RectD box)
    {
        box = default;

        if (options.Scale <= 0 || width <= 0 || height <= 0)
        {
            return false;
        }

        double left = ((centerX - (width / 2)) - options.PadX) / options.Scale;
        double top = ((centerY - (height / 2)) - options.PadY) / options.Scale;
        double right = ((centerX + (width / 2)) - options.PadX) / options.Scale;
        double bottom = ((centerY + (height / 2)) - options.PadY) / options.Scale;

        left = Math.Clamp(left, 0, options.ImageWidth);
        top = Math.Clamp(top, 0, options.ImageHeight);
        right = Math.Clamp(right, 0, options.ImageWidth);
        bottom = Math.Clamp(bottom, 0, options.ImageHeight);

        if (right - left < 1 || bottom - top < 1)
        {
            return false;
        }

        box = RectD.FromLtrb(left, top, right, bottom);
        return true;
    }

    private static string ResolveLabel(
        int classId,
        IReadOnlyList<string>? classNames,
        IReadOnlyDictionary<int, string>? classMap)
    {
        if (classMap is not null && classMap.TryGetValue(classId, out string? mapped) && !string.IsNullOrWhiteSpace(mapped))
        {
            return mapped.Trim();
        }

        if (classNames is not null && classId < classNames.Count && !string.IsNullOrWhiteSpace(classNames[classId]))
        {
            return classNames[classId].Trim();
        }

        return $"class_{classId}";
    }

    /// <summary>按类别分组的贪心 NMS。</summary>
    private static IReadOnlyList<AutoLabelCandidate> NonMaximumSuppression(
        List<AutoLabelCandidate> candidates,
        double iouThreshold)
    {
        if (candidates.Count <= 1)
        {
            return candidates;
        }

        candidates.Sort((left, right) => right.Score.CompareTo(left.Score));

        var kept = new List<AutoLabelCandidate>(candidates.Count);
        var suppressed = new bool[candidates.Count];

        for (int i = 0; i < candidates.Count; i++)
        {
            if (suppressed[i])
            {
                continue;
            }

            AutoLabelCandidate current = candidates[i];
            kept.Add(current);

            for (int j = i + 1; j < candidates.Count; j++)
            {
                if (suppressed[j] || candidates[j].ClassId != current.ClassId)
                {
                    continue;
                }

                if (IntersectionOverUnion(current.Box, candidates[j].Box) > iouThreshold)
                {
                    suppressed[j] = true;
                }
            }
        }

        return kept;
    }

    private static double IntersectionOverUnion(RectD a, RectD b)
    {
        double left = Math.Max(a.X, b.X);
        double top = Math.Max(a.Y, b.Y);
        double right = Math.Min(a.Right, b.Right);
        double bottom = Math.Min(a.Bottom, b.Bottom);

        double intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        if (intersection <= 0)
        {
            return 0;
        }

        double union = (a.Width * a.Height) + (b.Width * b.Height) - intersection;
        return union <= 0 ? 0 : intersection / union;
    }
}
