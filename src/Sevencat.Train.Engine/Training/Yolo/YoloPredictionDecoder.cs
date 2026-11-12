using Sevencat.Train.Engine.Core;
using TorchSharp;
using static TorchSharp.torch;

namespace Sevencat.Train.Engine.Training.Yolo;

/// <summary>解码后的一个检测框（xyxy，网络输入像素）。</summary>
public readonly record struct YoloDetection(int ClassId, float Score, float X1, float Y1, float X2, float Y2)
{
    public float Width => X2 - X1;

    public float Height => Y2 - Y1;
}

/// <summary>评估用的真值框（xyxy，网络输入像素）。</summary>
public readonly record struct YoloGroundTruth(int ClassId, float X1, float Y1, float X2, float Y2);

/// <summary>
/// 把模型原始输出 <c>[1, 4*reg_max + nc, N]</c> 解码成检测框：
/// DFL 积分 → dist2bbox → sigmoid 分类 → 置信度过滤 → NMS。
/// <para>
/// 与训练侧共用 <see cref="YoloAnchorGrid"/> 的锚点顺序；导出 ONNX 时同一套公式用图节点表达。
/// </para>
/// </summary>
public static class YoloPredictionDecoder
{
    /// <summary>解码单张图的预测。</summary>
    public static IReadOnlyList<YoloDetection> Decode(
        Tensor prediction,
        int imageWidth,
        int imageHeight,
        int classCount,
        int regressionMax = 16,
        float confidenceThreshold = 0.25f,
        float iouThreshold = 0.45f,
        int maxDetections = 300)
    {
        ArgumentNullException.ThrowIfNull(prediction);

        if (classCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(classCount), classCount, "类别数必须大于 0。");

        if (prediction.dim() == 3)
        {
            if (prediction.shape[0] != 1)
            {
                throw new ArgumentException(
                    $"解码一次只处理一张图，prediction 的 batch 必须是 1，实际是 {prediction.shape[0]}。",
                    nameof(prediction));
            }

            prediction = prediction[0];
        }

        int outputChannels = 4 * regressionMax + classCount;
        if (prediction.dim() != 2 || prediction.shape[0] != outputChannels)
        {
            throw new ArgumentException(
                $"prediction 必须是 [{outputChannels},N]，实际是 [{string.Join(",", prediction.shape)}]。",
                nameof(prediction));
        }

        YoloAnchor[] anchors = YoloAnchorGrid.Create(imageWidth, imageHeight);
        int anchorCount = anchors.Length;

        if (prediction.shape[1] != anchorCount)
        {
            throw new ArgumentException(
                $"prediction 的锚点数 {prediction.shape[1]} 与 {imageWidth}x{imageHeight} 的网格 {anchorCount} 不一致。",
                nameof(prediction));
        }

        // 解码走向量化路径（与 ONNX 导出同一套公式），逐锚点只做一次 argmax，
        // 比原来的「每锚点 4×reg_max 次 exp + 归一化」快一个量级。
        Tensor decoded = DecodeOutput0(prediction.unsqueeze(0), imageWidth, imageHeight, classCount, regressionMax);
        float[] values = TorchInterop.ToArray(decoded[0]);
        var detections = new List<YoloDetection>();

        for (int anchorIndex = 0; anchorIndex < anchorCount; anchorIndex++)
        {
            int bestClass = -1;
            float bestScore = 0f;

            for (int classId = 0; classId < classCount; classId++)
            {
                float score = values[(4 + classId) * anchorCount + anchorIndex];
                if (score > bestScore)
                {
                    bestScore = score;
                    bestClass = classId;
                }
            }

            if (bestClass < 0 || bestScore < confidenceThreshold)
                continue;

            detections.Add(new YoloDetection(
                bestClass,
                bestScore,
                values[anchorIndex],
                values[anchorCount + anchorIndex],
                values[2 * anchorCount + anchorIndex],
                values[3 * anchorCount + anchorIndex]));
        }

        return YoloNms.Apply(detections, iouThreshold, maxDetections);
    }

    /// <summary>
    /// 把原始预测 <c>[B, 4*reg_max+nc, N]</c> 向量化解码成 ultralytics 的 <c>output0</c>
    /// （<c>[B, 4+nc, N]</c>，xyxy 像素坐标 + sigmoid 分类分数），不做置信度过滤与 NMS。
    /// <para>
    /// 与 ONNX 导出使用同一套公式：DFL 期望值 → 乘 stride → 相对锚点中心偏移。
    /// </para>
    /// </summary>
    public static Tensor DecodeOutput0(
        Tensor prediction,
        int imageWidth,
        int imageHeight,
        int classCount,
        int regressionMax = 16)
    {
        ArgumentNullException.ThrowIfNull(prediction);

        if (classCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(classCount), classCount, "类别数必须大于 0。");

        if (prediction.dim() != 3 || prediction.shape[1] != 4L * regressionMax + classCount)
        {
            throw new ArgumentException(
                $"prediction 必须是 [B,{4 * regressionMax + classCount},N]，"
                + $"实际是 [{string.Join(",", prediction.shape)}]。",
                nameof(prediction));
        }

        YoloAnchor[] anchors = YoloAnchorGrid.Create(imageWidth, imageHeight);
        if (prediction.shape[2] != anchors.Length)
        {
            throw new ArgumentException(
                $"prediction 的锚点数 {prediction.shape[2]} 与 {imageWidth}x{imageHeight} 的网格 {anchors.Length} 不一致。",
                nameof(prediction));
        }

        long batch = prediction.shape[0];
        var levels = new List<Tensor>(YoloAnchorGrid.Strides.Length);

        using (torch.no_grad())
        {
            Tensor weights = torch.arange(
                regressionMax,
                dtype: ScalarType.Float32,
                device: prediction.device).view(1, regressionMax, 1, 1);

            int offset = 0;

            foreach (int stride in YoloAnchorGrid.Strides)
            {
                int count = imageHeight / stride * (imageWidth / stride);

                Tensor boxLogits = prediction
                    .narrow(1, 0, 4 * regressionMax)
                    .narrow(2, offset, count);

                Tensor classLogits = prediction
                    .narrow(1, 4 * regressionMax, classCount)
                    .narrow(2, offset, count);

                Tensor distances = boxLogits
                    .view(batch, 4, regressionMax, count)
                    .transpose(1, 2)
                    .softmax(1)
                    .mul(weights)
                    .sum(1)
                    .view(batch, 4, count)
                    .mul(stride); // DFL 期望值的单位是 stride，换成像素距离

                var centerX = new float[count];
                var centerY = new float[count];
                for (int index = 0; index < count; index++)
                {
                    centerX[index] = anchors[offset + index].PixelX;
                    centerY[index] = anchors[offset + index].PixelY;
                }

                Tensor anchorX = TorchInterop.FromArray(
                    centerX, new long[] { 1, 1, count }, prediction.device);
                Tensor anchorY = TorchInterop.FromArray(
                    centerY, new long[] { 1, 1, count }, prediction.device);

                Tensor boxes = torch.cat(
                    new[]
                    {
                        anchorX - distances.narrow(1, 0, 1),
                        anchorY - distances.narrow(1, 1, 1),
                        anchorX + distances.narrow(1, 2, 1),
                        anchorY + distances.narrow(1, 3, 1),
                    },
                    1);

                levels.Add(torch.cat(new[] { boxes, classLogits.sigmoid() }, 1));
                offset += count;
            }
        }

        return torch.cat(levels.ToArray(), 2);
    }
}
