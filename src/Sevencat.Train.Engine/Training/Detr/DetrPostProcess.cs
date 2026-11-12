using Sevencat.Train.Engine.Core;
using Sevencat.Train.Engine.Training.Yolo;
using TorchSharp;
using static TorchSharp.torch;

namespace Sevencat.Train.Engine.Training.Detr;

/// <summary>
/// RF-DETR 的推理后处理：把 <c>[B,Q,4+nc]</c>（归一化 cxcywh + logits）变成
/// 每张图的检测框列表（网络输入像素 xyxy），供 <see cref="DetectionMetrics"/> 使用。
/// <para>
/// DETR 的每个 query 只对应一个类别（逐 query argmax），因此**不需要 NMS**：
/// 集合预测本身已经抑制了重复框。这一点与 YOLO 的 densely-anchored 解码不同。
/// </para>
/// </summary>
public static class DetrPostProcess
{
    /// <summary>解码整个批次；返回的列表与 batch 一一对应。</summary>
    public static IReadOnlyList<IReadOnlyList<YoloDetection>> Decode(
        Tensor prediction,
        int imageWidth,
        int imageHeight,
        int classCount,
        float confidenceThreshold = 0.5f,
        int maxDetections = 300)
    {
        ArgumentNullException.ThrowIfNull(prediction);

        if (imageWidth <= 0 || imageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageWidth), imageWidth, "图像尺寸必须大于 0。");
        if (classCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(classCount), classCount, "类别数必须大于 0。");
        if (maxDetections <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDetections), maxDetections, "上限必须大于 0。");

        if (prediction.dim() != 3 || prediction.shape[2] != 4 + classCount)
        {
            throw new ArgumentException(
                $"prediction 必须是 [B,Q,{4 + classCount}]，实际是 [{string.Join(",", prediction.shape)}]。",
                nameof(prediction));
        }

        int batch = (int)prediction.shape[0];
        int queries = (int)prediction.shape[1];
        float[] values = TorchInterop.ToArray(prediction);

        var results = new List<IReadOnlyList<YoloDetection>>(batch);

        for (int image = 0; image < batch; image++)
        {
            var detections = new List<YoloDetection>(queries);

            for (int query = 0; query < queries; query++)
            {
                int offset = (image * queries + query) * (4 + classCount);

                float centreX = values[offset];
                float centreY = values[offset + 1];
                float width = values[offset + 2];
                float height = values[offset + 3];

                int bestClass = -1;
                float bestScore = 0f;
                float bestLogit = float.NegativeInfinity;

                for (int classId = 0; classId < classCount; classId++)
                {
                    float logit = values[offset + 4 + classId];
                    if (logit > bestLogit)
                    {
                        bestLogit = logit;
                        bestClass = classId;
                    }
                }

                bestScore = 1f / (1f + MathF.Exp(-bestLogit));

                if (bestClass < 0 || bestScore < confidenceThreshold)
                    continue;

                detections.Add(new YoloDetection(
                    bestClass,
                    bestScore,
                    Math.Clamp(centreX - width / 2f, 0f, 1f) * imageWidth,
                    Math.Clamp(centreY - height / 2f, 0f, 1f) * imageHeight,
                    Math.Clamp(centreX + width / 2f, 0f, 1f) * imageWidth,
                    Math.Clamp(centreY + height / 2f, 0f, 1f) * imageHeight));
            }

            results.Add(detections
                .OrderByDescending(item => item.Score)
                .Take(maxDetections)
                .ToArray());
        }

        return results;
    }
}
