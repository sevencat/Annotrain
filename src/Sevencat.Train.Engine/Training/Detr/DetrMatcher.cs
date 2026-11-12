using Sevencat.Train.Engine.Core;
using TorchSharp;
using static TorchSharp.torch;

namespace Sevencat.Train.Engine.Training.Detr;

/// <summary>集合损失与匹配的权重。</summary>
public sealed record DetrLossOptions
{
    /// <summary>分类（sigmoid BCE）权重。</summary>
    public float ClassWeight { get; init; } = 1f;

    /// <summary>L1 框回归权重（DETR 常用 5）。</summary>
    public float L1Weight { get; init; } = 5f;

    /// <summary>GIoU 权重（DETR 常用 2）。</summary>
    public float GiouWeight { get; init; } = 2f;

    /// <summary>
    /// focal loss 的 γ（&gt;0 时启用；0 表示普通 BCE）。
    /// <para>
    /// 实测：30 epoch 的合成数据上 focal（α=0.25、γ=2）mAP 0.002，普通 BCE 为 0.091，
    /// 因此**默认关闭**；focal 作为长跑 / 类别不平衡时的可选项保留。
    /// </para>
    /// </summary>
    public float FocalGamma { get; init; }

    /// <summary>focal loss 的 α（正样本权重）。</summary>
    public float FocalAlpha { get; init; } = 0.25f;

    /// <summary>
    /// IA-BCE 的 γ（&gt;0 时分类目标 = <c>IoU^γ</c> 而不是 1）。
    /// 这是官方 RF-DETR / LW-DETR 的 IoU-Aware BCE：让分类分数与定位质量对齐。
    /// </summary>
    public float IouAwareClassGamma { get; init; }
}

/// <summary>匈牙利算法（e-maxx 版，O(n³)）：<c>cost[i,j]</c> 为第 i 行匹配第 j 列的成本。</summary>
public static class HungarianSolver
{
    /// <summary>返回每个行对应的列号（要求 rows ≤ cols，调用方自行补零）。</summary>
    public static int[] Solve(double[,] cost)
    {
        int rows = cost.GetLength(0);
        int columns = cost.GetLength(1);

        if (rows > columns)
            throw new ArgumentException($"匈牙利算法要求 rows ≤ cols，实际 {rows} > {columns}。", nameof(cost));

        var u = new double[rows + 1];
        var v = new double[columns + 1];
        var p = new int[columns + 1];
        var way = new int[columns + 1];

        for (int row = 1; row <= rows; row++)
        {
            p[0] = row;
            int column0 = 0;
            var minv = Enumerable.Repeat(double.PositiveInfinity, columns + 1).ToArray();
            var used = new bool[columns + 1];

            do
            {
                used[column0] = true;
                int row0 = p[column0];
                int column1 = 0;
                double delta = double.PositiveInfinity;

                for (int column = 1; column <= columns; column++)
                {
                    if (used[column])
                        continue;

                    double current = cost[row0 - 1, column - 1] - u[row0] - v[column];
                    if (current < minv[column])
                    {
                        minv[column] = current;
                        way[column] = column0;
                    }

                    if (minv[column] < delta)
                    {
                        delta = minv[column];
                        column1 = column;
                    }
                }

                for (int column = 0; column <= columns; column++)
                {
                    if (used[column])
                    {
                        u[p[column]] += delta;
                        v[column] -= delta;
                    }
                    else
                    {
                        minv[column] -= delta;
                    }
                }

                column0 = column1;
            }
            while (p[column0] != 0);

            do
            {
                int previous = way[column0];
                p[column0] = p[previous];
                column0 = previous;
            }
            while (column0 != 0);
        }

        var assignment = new int[rows];
        Array.Fill(assignment, -1);

        for (int column = 1; column <= columns; column++)
        {
            if (p[column] > 0)
                assignment[p[column] - 1] = column - 1;
        }

        return assignment;
    }
}

/// <summary>一张图的匹配结果：<c>Pairs</c> 每项是 (query, gt)，未匹配的 query 不在列表里。</summary>
public sealed record SimpleDetrImageMatch(IReadOnlyList<(int Query, int Target)> Pairs);

/// <summary>
/// DETR 风格的匈牙利匹配：代价 = <c>-score[gtClass]·wCls + L1·wL1 + (1-GIoU)·wGiou</c>，
/// 全部在**归一化 cxcywh** 空间计算（与损失一致）。
/// </summary>
public static class DetrMatcher
{
    /// <summary>对每张图匹配一次；<paramref name="gtMask"/> 为 false 的行视为填充。</summary>
    public static IReadOnlyList<SimpleDetrImageMatch> Match(
        Tensor boxes,
        Tensor logits,
        Tensor gtBoxes,
        Tensor gtClasses,
        Tensor gtMask,
        DetrLossOptions? options = null,
        int groups = 1)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        ArgumentNullException.ThrowIfNull(logits);
        ArgumentNullException.ThrowIfNull(gtBoxes);
        ArgumentNullException.ThrowIfNull(gtClasses);
        ArgumentNullException.ThrowIfNull(gtMask);

        DetrLossOptions settings = options ?? new DetrLossOptions();

        int batch = (int)boxes.shape[0];
        int queries = (int)boxes.shape[1];
        int maxTargets = (int)gtBoxes.shape[1];
        int classCount = (int)logits.shape[2];

        if (groups <= 0 || queries % groups != 0)
        {
            throw new ArgumentException(
                $"groups 必须能整除 query 数（queries={queries}, groups={groups}）。", nameof(groups));
        }

        // Group-DETR：把 query 均分成 groups 组，**每组独立做一次一对一匹配**，
        // 于是同一批 GT 会被 G 组各匹配一次，正样本数 ×G（官方用这个技巧加速收敛）。
        int groupSize = queries / groups;

        float[] allBoxes = TorchInterop.ToArray(boxes);
        float[] allScores = TorchInterop.ToArray(logits.sigmoid());
        float[] allTargets = TorchInterop.ToArray(gtBoxes);
        float[] classValues = TorchInterop.ToArray(gtClasses.to(ScalarType.Float32));
        float[] maskValues = TorchInterop.ToArray(gtMask.to(ScalarType.Float32));

        var matches = new List<SimpleDetrImageMatch>(batch);

        for (int image = 0; image < batch; image++)
        {
            var valid = new List<int>();
            for (int target = 0; target < maxTargets; target++)
            {
                if (maskValues[image * maxTargets + target] > 0.5f)
                    valid.Add(target);
            }

            if (valid.Count == 0)
            {
                matches.Add(new SimpleDetrImageMatch(Array.Empty<(int, int)>()));
                continue;
            }

            var pairs = new List<(int, int)>(valid.Count * groups);

            for (int group = 0; group < groups; group++)
            {
                int start = group * groupSize;
                int size = Math.Max(groupSize, valid.Count);
                var cost = new double[size, size];

                for (int local = 0; local < groupSize; local++)
                {
                    int query = start + local;

                    var prediction = (
                        allBoxes[(image * queries + query) * 4],
                        allBoxes[(image * queries + query) * 4 + 1],
                        allBoxes[(image * queries + query) * 4 + 2],
                        allBoxes[(image * queries + query) * 4 + 3]);

                    for (int column = 0; column < valid.Count; column++)
                    {
                        int target = valid[column];
                        int offset = (image * maxTargets + target) * 4;

                        var truth = (
                            allTargets[offset],
                            allTargets[offset + 1],
                            allTargets[offset + 2],
                            allTargets[offset + 3]);

                        int classId = Math.Clamp((int)classValues[image * maxTargets + target], 0, classCount - 1);
                        float score = allScores[(image * queries + query) * classCount + classId];

                        cost[local, column] =
                            -settings.ClassWeight * score
                            + settings.L1Weight * Distance(prediction, truth)
                            + settings.GiouWeight * (1.0 - Giou(prediction, truth));
                    }

                    // 补齐列（GT 少于组内 query）用大成本，避免抢真实 GT。
                    for (int column = valid.Count; column < size; column++)
                        cost[local, column] = 1e6;
                }

                for (int row = groupSize; row < size; row++)
                {
                    for (int column = 0; column < size; column++)
                        cost[row, column] = 1e6;
                }

                int[] assignment = HungarianSolver.Solve(cost);

                for (int local = 0; local < groupSize; local++)
                {
                    int column = assignment[local];
                    if (column >= 0 && column < valid.Count)
                        pairs.Add((start + local, valid[column]));
                }
            }

            matches.Add(new SimpleDetrImageMatch(pairs));
        }

        return matches;
    }

    private static double Distance((float X, float Y, float W, float H) a, (float X, float Y, float W, float H) b) =>
        Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) + Math.Abs(a.W - b.W) + Math.Abs(a.H - b.H);

    /// <summary>归一化 cxcywh 的 GIoU（匹配阶段不需要梯度）。</summary>
    private static double Giou((float X, float Y, float W, float H) a, (float X, float Y, float W, float H) b)
    {
        double ax1 = a.X - a.W / 2.0, ay1 = a.Y - a.H / 2.0;
        double ax2 = a.X + a.W / 2.0, ay2 = a.Y + a.H / 2.0;
        double bx1 = b.X - b.W / 2.0, by1 = b.Y - b.H / 2.0;
        double bx2 = b.X + b.W / 2.0, by2 = b.Y + b.H / 2.0;

        double interWidth = Math.Max(0.0, Math.Min(ax2, bx2) - Math.Max(ax1, bx1));
        double interHeight = Math.Max(0.0, Math.Min(ay2, by2) - Math.Max(ay1, by1));
        double intersection = interWidth * interHeight;

        double areaA = Math.Max(0.0, ax2 - ax1) * Math.Max(0.0, ay2 - ay1);
        double areaB = Math.Max(0.0, bx2 - bx1) * Math.Max(0.0, by2 - by1);
        double union = areaA + areaB - intersection + 1e-9;

        double encloseWidth = Math.Max(ax2, bx2) - Math.Min(ax1, bx1);
        double encloseHeight = Math.Max(ay2, by2) - Math.Min(ay1, by1);
        double enclose = encloseWidth * encloseHeight + 1e-9;

        return intersection / union - (enclose - union) / enclose;
    }
}

