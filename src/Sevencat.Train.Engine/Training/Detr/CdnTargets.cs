using Sevencat.Train.Engine.Core;
using TorchSharp;
using static TorchSharp.torch;

namespace Sevencat.Train.Engine.Training.Detr;

/// <summary>一组 CDN（Contrastive DeNoising）目标：加噪框、标签、有效掩码。</summary>
public sealed record CdnBatch(
    float[] Boxes,
    int[] Labels,
    bool[] Mask,
    int Count,
    int Groups,
    int ClassCount)
{
    /// <summary>把加噪框转成张量 <c>[B,Count,4]</c>（归一化 cxcywh）。</summary>
    public Tensor ToBoxes(long batch, Device device) =>
        TorchInterop.FromArray(Boxes, new long[] { batch, Count, 4 }, device);
}

/// <summary>
/// CDN 目标构建（官方 RF-DETR / DETR 的去噪训练）：对每个 GT 复制 <c>Groups</c> 份，
/// 中心加随机扰动、宽高随机缩放，并按一定概率把类别翻成别的类（翻类样本在损失里忽略分类项）。
/// <para>
/// 这一步只负责「造目标」，模型侧需要把这些加噪框作为额外的 decoder query 输入；
/// 拼接与损失见 plan.md P2-2-4。
/// </para>
/// </summary>
public static class CdnTargets
{
    /// <summary>
    /// 从 YOLO 目标 <c>[B,M,6]</c>（valid, class, x1,y1,x2,y2 像素）构造 CDN 目标。
    /// </summary>
    public static CdnBatch Build(
        Tensor target,
        int imageWidth,
        int imageHeight,
        int classCount,
        int groups = 2,
        float noiseScale = 0.4f,
        float labelFlipProbability = 0.1f,
        Rng? rng = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.dim() != 3 || target.shape[2] != 6)
            throw new ArgumentException($"target 必须是 [B,M,6]，实际 [{string.Join(",", target.shape)}]。", nameof(target));
        if (groups <= 0)
            throw new ArgumentOutOfRangeException(nameof(groups), groups, "CDN 组数必须大于 0。");
        if (classCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(classCount), classCount, "类别数必须大于 0。");

        Rng random = rng ?? new Rng(17);

        int batch = (int)target.shape[0];
        int maxTargets = (int)target.shape[1];
        int count = maxTargets * groups;

        float[] raw = TorchInterop.ToArray(target);
        var boxes = new float[batch * count * 4];
        var labels = new int[batch * count];
        var mask = new bool[batch * count];

        for (int image = 0; image < batch; image++)
        {
            for (int targetIndex = 0; targetIndex < maxTargets; targetIndex++)
            {
                int offset = (image * maxTargets + targetIndex) * 6;
                bool valid = raw[offset] > 0.5f;
                int classId = (int)raw[offset + 1];

                float x1 = raw[offset + 2] / imageWidth;
                float y1 = raw[offset + 3] / imageHeight;
                float x2 = raw[offset + 4] / imageWidth;
                float y2 = raw[offset + 5] / imageHeight;

                float centreX = (x1 + x2) / 2f;
                float centreY = (y1 + y2) / 2f;
                float width = MathF.Max(1e-3f, x2 - x1);
                float height = MathF.Max(1e-3f, y2 - y1);

                for (int group = 0; group < groups; group++)
                {
                    int source = targetIndex * groups + group;
                    int index = image * count + source;

                    if (!valid)
                    {
                        mask[index] = false;
                        labels[index] = -1;
                        continue;
                    }

                    float noiseX = (random.NextFloat() * 2f - 1f) * noiseScale * width;
                    float noiseY = (random.NextFloat() * 2f - 1f) * noiseScale * height;
                    float scale = 1f + (random.NextFloat() * 2f - 1f) * noiseScale * 0.5f;

                    boxes[(index * 4)] = Math.Clamp(centreX + noiseX, 0f, 1f);
                    boxes[(index * 4) + 1] = Math.Clamp(centreY + noiseY, 0f, 1f);
                    boxes[(index * 4) + 2] = Math.Clamp(width * scale, 1e-3f, 1f);
                    boxes[(index * 4) + 3] = Math.Clamp(height * scale, 1e-3f, 1f);

                    mask[index] = true;

                    // 翻类样本：标签置 -1，损失里只回传框、不训练分类（与 CDN 的 attention mask 等价）。
                    labels[index] = random.NextFloat() < labelFlipProbability ? -1 : classId;
                }
            }
        }

        return new CdnBatch(boxes, labels, mask, count, groups, classCount);
    }
}
