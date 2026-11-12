using TorchSharp;
using static TorchSharp.torch;

namespace Sevencat.Train.Engine.Training.Yolo;

/// <summary>TAL 分配结果：锚点级软标签 + 背景/前景标记。</summary>
/// <param name="TargetScores">每个锚点每个类别的目标分数 <c>[B,N,nc]</c>（背景为 0）。</param>
/// <param name="TargetBoxes">每个锚点被分配的 GT 框 <c>[B,N,4]</c>（xyxy，像素；背景为 0）。</param>
/// <param name="Foreground">前景锚点掩码 <c>[B,N]</c>。</param>
public sealed record TalResult(Tensor TargetScores, Tensor TargetBoxes, Tensor Foreground);

/// <summary>
/// Task-Aligned Assigner（TAL）：YOLOv8 / v11 的正样本分配。
/// <para>
/// 对每个 GT 取「分类分数<sup>α</sup> × IoU<sup>β</sup>」最高的前 <c>topK</c> 个锚点，
/// 且锚点中心必须落在 GT 内部；一个锚点被多个 GT 竞争时保留分数更高的那个。
/// 目标分会被归一化到该 GT 的最佳 IoU（与 ultralytics <c>get_targets</c> 一致）。
/// <para>
/// 与 ultralytics 的差别只有一点：分配阶段用 IoU 而不是 CIoU，避免负值参与幂运算。
/// </para>
/// </para>
/// <para>分配本身不产生梯度：内部包在 <c>no_grad</c> 里，只把结果当作软标签。</para>
/// </summary>
public sealed class TaskAlignedAssigner
{
    private readonly int _classCount;
    private readonly int _topK;
    private readonly float _alpha;
    private readonly float _beta;

    /// <summary>创建分配器；<paramref name="topK"/> 默认 13（ultralytics 默认值）。</summary>
    public TaskAlignedAssigner(int classCount, int topK = 13, float alpha = 0.5f, float beta = 6f)
    {
        if (classCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(classCount), classCount, "类别数必须大于 0。");
        if (topK <= 0)
            throw new ArgumentOutOfRangeException(nameof(topK), topK, "topK 必须大于 0。");
        if (alpha <= 0f || beta <= 0f)
            throw new ArgumentOutOfRangeException(nameof(alpha), alpha, "alpha / beta 必须大于 0。");

        _classCount = classCount;
        _topK = topK;
        _alpha = alpha;
        _beta = beta;
    }

    /// <summary>执行分配（调用方应传 <c>detach()</c> 后的预测，避免建图）。</summary>
    public TalResult Assign(
        Tensor predScores,
        Tensor predBoxes,
        Tensor anchorPoints,
        Tensor gtClasses,
        Tensor gtBoxes,
        Tensor gtMask)
    {
        ArgumentNullException.ThrowIfNull(predScores);
        ArgumentNullException.ThrowIfNull(predBoxes);
        ArgumentNullException.ThrowIfNull(anchorPoints);
        ArgumentNullException.ThrowIfNull(gtClasses);
        ArgumentNullException.ThrowIfNull(gtBoxes);
        ArgumentNullException.ThrowIfNull(gtMask);

        if (predScores.dim() != 3 || predScores.shape[2] != _classCount)
        {
            throw new ArgumentException(
                $"predScores 必须是 [B,N,{_classCount}]，实际是 [{string.Join(",", predScores.shape)}]。",
                nameof(predScores));
        }

        long batch = predScores.shape[0];
        long anchors = predScores.shape[1];
        long maxBoxes = gtBoxes.shape[1];
        Device device = predScores.device;

        var scoreList = new List<Tensor>((int)batch);
        var boxList = new List<Tensor>((int)batch);
        var foregroundList = new List<Tensor>((int)batch);

        using (torch.no_grad())
        {
            for (long imageIndex = 0; imageIndex < batch; imageIndex++)
            {
                Tensor scores = predScores[imageIndex];
                Tensor boxes = predBoxes[imageIndex];
                Tensor imageGtBoxes = gtBoxes[imageIndex];
                Tensor imageGtClasses = gtClasses[imageIndex];
                Tensor imageMask = gtMask[imageIndex];

                Tensor bestMetric = torch.zeros(new long[] { anchors }, dtype: ScalarType.Float32, device: device);
                Tensor bestGt = torch.full(new long[] { anchors }, -1L, dtype: ScalarType.Int64, device: device);

                long topK = Math.Min(_topK, anchors);

                for (long boxIndex = 0; boxIndex < maxBoxes; boxIndex++)
                {
                    Tensor gt = imageGtBoxes[boxIndex];

                    // 锚点中心必须落在 GT 内部（ultralytics 的 is_in_gts）。
                    Tensor inside = torch.logical_and(
                        torch.logical_and(
                            anchorPoints.select(-1, 0) > gt[0],
                            anchorPoints.select(-1, 0) < gt[2]),
                        torch.logical_and(
                            anchorPoints.select(-1, 1) > gt[1],
                            anchorPoints.select(-1, 1) < gt[3]));

                    Tensor classIndex = imageGtClasses[boxIndex].reshape(1, 1);
                    Tensor classScores = scores.gather(1, classIndex.expand(anchors, 1)).squeeze(1);

                    Tensor iou = BboxOps.IoU(boxes, gt.expand(anchors, 4)).clamp(min: 0);
                    Tensor metric = classScores.pow(_alpha) * iou.pow(_beta);

                    metric = metric
                        * inside.to(ScalarType.Float32)
                        * imageMask[boxIndex].to(ScalarType.Float32);

                    // 与 ultralytics 的 get_targets 一致：把每个 GT 的对齐度量归一化到
                    // 「该 GT 的最佳 IoU」。否则 iou^β（β=6）会把目标分压到 0.0x，
                    // 分类头只能学到同样低的置信度，推理时任何正常阈值都会把预测全过滤掉。
                    Tensor posAlign = metric.max();
                    Tensor posOverlap = iou.max();
                    metric = metric * posOverlap / (posAlign + 1e-9f);

                    (Tensor topValues, Tensor topIndices) = metric.topk((int)topK, dim: 0, largest: true, sorted: false);

                    Tensor candidate = torch.zeros(new long[] { anchors }, dtype: ScalarType.Float32, device: device);
                    candidate.scatter_(0, topIndices, topValues);

                    Tensor candidateGt = torch.full(
                        new long[] { anchors }, boxIndex, dtype: ScalarType.Int64, device: device);

                    Tensor better = candidate > bestMetric;
                    bestMetric = torch.where(better, candidate, bestMetric);
                    bestGt = torch.where(better, candidateGt, bestGt);
                }

                Tensor hasGt = bestGt >= 0;
                Tensor safeGt = bestGt.clamp(min: 0);
                Tensor maskFloat = hasGt.to(ScalarType.Float32).unsqueeze(1);

                Tensor imageBoxes = imageGtBoxes.index_select(0, safeGt) * maskFloat;

                Tensor classTargets = torch.zeros(
                        new long[] { anchors, _classCount }, dtype: ScalarType.Float32, device: device)
                    .scatter_(
                        1,
                        imageGtClasses.index_select(0, safeGt).unsqueeze(1),
                        (bestMetric * maskFloat.squeeze(1)).unsqueeze(1));

                scoreList.Add(classTargets);
                boxList.Add(imageBoxes);
                foregroundList.Add(hasGt);
            }

            return new TalResult(
                torch.stack(scoreList.ToArray(), 0),
                torch.stack(boxList.ToArray(), 0),
                torch.stack(foregroundList.ToArray(), 0));
        }
    }
}
