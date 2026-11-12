using Sevencat.Train.Engine.Core;
using TorchSharp;
using static TorchSharp.torch;

namespace Sevencat.Train.Engine.Training.Yolo;

/// <summary>YOLO 检测损失的权重与分配参数（默认值取自 ultralytics 的 <c>hyp</c>）。</summary>
public sealed record YoloLossOptions
{
    /// <summary>box（CIoU）损失权重。</summary>
    public float BoxWeight { get; init; } = 7.5f;

    /// <summary>分类（BCE）损失权重。</summary>
    public float ClassWeight { get; init; } = 0.5f;

    /// <summary>DFL 损失权重。</summary>
    public float DflWeight { get; init; } = 1.5f;

    /// <summary>TAL 的 top-k 锚点数。</summary>
    public int TopK { get; init; } = 13;

    /// <summary>TAL 分类分数指数。</summary>
    public float Alpha { get; init; } = 0.5f;

    /// <summary>TAL IoU 指数。</summary>
    public float Beta { get; init; } = 6f;

    /// <summary>是否乘 batch size（与 ultralytics 一致；关掉可让损失数值更小）。</summary>
    public bool ScaleByBatchSize { get; init; } = true;
}

/// <summary>一次损失计算的三个分项（用于日志与调试）。</summary>
public sealed record YoloLossParts(float Box, float Class, float Dfl, float Total);

/// <summary>
/// YOLOv8 检测损失：TAL 分配 + box(CIoU) / cls(BCE) / dfl 三项。
/// <para>
/// 输入 <c>prediction</c> 是模型原始输出 <c>[B, 4*reg_max + nc, N]</c>，
/// <c>target</c> 是 <c>[B, M, 6]</c>（<c>valid, class, x1, y1, x2, y2</c>，像素坐标）。
/// 锚点顺序必须与检测头一致：P3 → P4 → P5，每层按行优先展平。
/// </para>
/// <para>
/// 三个分项都按「本次 batch 的目标分数总和」归一化；<see cref="YoloLossOptions.ScaleByBatchSize"/>
/// 打开时再乘 batch size，和 ultralytics 的梯度尺度一致。
/// </para>
/// </summary>
public sealed class YoloDetectionLoss : ILoss
{
    private readonly int _classCount;
    private readonly int _regressionMax;
    private int _imageWidth;
    private int _imageHeight;
    private readonly YoloLossOptions _options;
    private readonly TaskAlignedAssigner _assigner;

    private Device? _gridDevice;
    private Tensor? _anchorPoints;
    private Tensor? _anchorCells;
    private Tensor? _strides;
    private Tensor? _projection;
    private int _anchorCount;

    /// <summary>按输入尺寸与类别数创建损失；<paramref name="regressionMax"/> 必须与模型一致。</summary>
    public YoloDetectionLoss(
        int imageWidth,
        int imageHeight,
        int classCount,
        int regressionMax = 16,
        YoloLossOptions? options = null)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageWidth), imageWidth, "图像尺寸必须大于 0。");
        if (classCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(classCount), classCount, "类别数必须大于 0。");
        if (regressionMax <= 0)
            throw new ArgumentOutOfRangeException(nameof(regressionMax), regressionMax, "reg_max 必须大于 0。");

        _imageWidth = imageWidth;
        _imageHeight = imageHeight;
        _classCount = classCount;
        _regressionMax = regressionMax;
        _options = options ?? new YoloLossOptions();
        _assigner = new TaskAlignedAssigner(classCount, _options.TopK, _options.Alpha, _options.Beta);
        ConfigureImageSize(imageWidth, imageHeight);
    }

    /// <summary>
    /// 切换输入尺寸（多尺度训练用）：尺寸必须是 32 的倍数，切换后锚点网格会在下一次计算时重建。
    /// </summary>
    public void ConfigureImageSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || width % 32 != 0 || height % 32 != 0)
        {
            throw new ArgumentException(
                $"图像尺寸必须是 32 的正整数倍，实际 {width}x{height}。",
                nameof(width));
        }

        if (width == _imageWidth && height == _imageHeight && _anchorPoints is not null)
            return;

        _imageWidth = width;
        _imageHeight = height;
        _gridDevice = null;
    }

    /// <inheritdoc />
    public string Name => "yolo-detect";

    /// <summary>每个锚点的输出宽度（<c>4*reg_max + nc</c>）。</summary>
    public int OutputChannels => 4 * _regressionMax + _classCount;

    /// <summary>锚点总数（P3+P4+P5）。</summary>
    public int AnchorCount => _anchorCount;

    /// <summary>是否在每一步读回三个分项（会同步设备；只建议在 CPU 或调试时打开）。</summary>
    public bool TrackComponents { get; set; }

    /// <summary>最近一次 <see cref="Compute"/> 的分项（<see cref="TrackComponents"/> 关闭时保持 0）。</summary>
    public YoloLossParts LastParts { get; private set; } = new(0f, 0f, 0f, 0f);

    /// <inheritdoc />
    public Tensor Compute(Tensor prediction, Tensor target)
    {
        (Tensor box, Tensor classes, Tensor dfl) = ComputeParts(prediction, target);

        Tensor total = box * _options.BoxWeight
            + classes * _options.ClassWeight
            + dfl * _options.DflWeight;

        if (_options.ScaleByBatchSize)
            total = total * prediction.shape[0];

        if (TrackComponents)
        {
            LastParts = new YoloLossParts(
                TorchInterop.ToScalar(box),
                TorchInterop.ToScalar(classes),
                TorchInterop.ToScalar(dfl),
                TorchInterop.ToScalar(total));
        }

        return total;
    }

    private (Tensor Box, Tensor Class, Tensor Dfl) ComputeParts(Tensor prediction, Tensor target)
    {
        ArgumentNullException.ThrowIfNull(prediction);
        ArgumentNullException.ThrowIfNull(target);

        if (prediction.dim() != 3 || prediction.shape[1] != OutputChannels)
        {
            throw new ArgumentException(
                $"prediction 必须是 [B,{OutputChannels},N]，实际是 [{string.Join(",", prediction.shape)}]。",
                nameof(prediction));
        }

        if (target.dim() != 3 || target.shape[2] != 6 || target.shape[0] != prediction.shape[0])
        {
            throw new ArgumentException(
                $"target 必须是 [B,M,6] 且 B 与 prediction 一致，实际是 [{string.Join(",", target.shape)}]。",
                nameof(target));
        }

        int batch = (int)prediction.shape[0];
        int anchors = (int)prediction.shape[2];

        EnsureGrid(prediction.device);

        if (anchors != _anchorCount)
        {
            throw new ArgumentException(
                $"prediction 的锚点数 {anchors} 与 {_imageWidth}x{_imageHeight} 的网格 {_anchorCount} 不一致。",
                nameof(prediction));
        }

        Tensor boxRaw = prediction.narrow(1, 0, 4 * _regressionMax).transpose(1, 2).contiguous();
        Tensor classRaw = prediction.narrow(1, 4 * _regressionMax, _classCount).transpose(1, 2).contiguous();

        Tensor distribution = boxRaw.view(batch, anchors, 4, _regressionMax).softmax(3);
        Tensor ltrb = (distribution * _projection!).sum(3);

        Tensor predictedStride = BboxOps.Dist2Bbox(ltrb, _anchorCells!);
        Tensor predictedPixels = predictedStride * _strides!;

        Tensor gtMask = target.select(2, 0) > 0.5f;
        Tensor gtClasses = target.select(2, 1).to(ScalarType.Int64);
        Tensor gtBoxes = target.narrow(2, 2, 4);

        TalResult assignment = _assigner.Assign(
            classRaw.sigmoid(),
            predictedPixels.detach(),
            _anchorPoints!,
            gtClasses,
            gtBoxes,
            gtMask);

        Tensor targetScoresSum = assignment.TargetScores.sum().clamp(min: 1f);

        Tensor classLoss = torch.nn.functional.binary_cross_entropy_with_logits(
            classRaw,
            assignment.TargetScores,
            reduction: torch.nn.Reduction.None).sum() / targetScoresSum;

        Tensor foreground = assignment.Foreground.view(-1);
        Tensor indices = foreground.nonzero().squeeze(1);

        Tensor boxLoss;
        Tensor dflLoss;

        if (indices.shape[0] == 0)
        {
            boxLoss = torch.zeros(Array.Empty<long>(), dtype: ScalarType.Float32, device: prediction.device);
            dflLoss = torch.zeros(Array.Empty<long>(), dtype: ScalarType.Float32, device: prediction.device);
        }
        else
        {
            Tensor weights = assignment.TargetScores
                .view(-1, _classCount)
                .index_select(0, indices)
                .sum(1)
                .unsqueeze(1);

            // foreground / indices 是 [B*N] 展平后的位置，而 _strides / _anchorCells 只有单图 N 项，
            // 取模把批内偏移折回锚点序号（同一锚点在各图上共享 stride 与 cell 坐标）。
            Tensor anchorIndex = indices.remainder(_anchorCount);

            Tensor predictedBoxes = predictedStride.view(-1, 4).index_select(0, indices);
            Tensor targetBoxes = assignment.TargetBoxes.view(-1, 4).index_select(0, indices);
            Tensor strideValues = _strides!.view(-1, 1).index_select(0, anchorIndex);
            Tensor anchorValues = _anchorCells!.view(-1, 2).index_select(0, anchorIndex);

            // CIoU / DFL 都在 stride 单位下计算（与 ultralytics 一致），避免不同层尺度互相干扰。
            Tensor normalizedTargets = targetBoxes / strideValues;

            Tensor iou = BboxOps.Ciou(predictedBoxes, normalizedTargets);
            boxLoss = ((1f - iou).unsqueeze(1) * weights).sum() / targetScoresSum;

            Tensor predictionDist = boxRaw.view(-1, 4 * _regressionMax).index_select(0, indices);
            Tensor targetDist = BboxOps.Bbox2Dist(anchorValues, normalizedTargets, _regressionMax - 1);

            Tensor dflRows = DflLoss(predictionDist, targetDist, indices.shape[0]);
            dflLoss = (dflRows.mean(new long[] { 1 }, true) * weights).sum() / targetScoresSum;
        }

        return (boxLoss, classLoss, dflLoss);
    }

    /// <summary>DFL 的交叉熵：目标距离按「左右两个整数格 + 权重」拆成软标签。</summary>
    private Tensor DflLoss(Tensor predictionDist, Tensor targetDist, long rows)
    {
        Tensor prediction = predictionDist.view(rows * 4, _regressionMax);
        Tensor target = targetDist.view(rows * 4);

        Tensor lower = target.to(ScalarType.Int64).clamp(min: 0, max: _regressionMax - 1);
        Tensor upper = (lower + 1).clamp(max: _regressionMax - 1);

        Tensor lowerWeight = upper.to(ScalarType.Float32) - target;
        Tensor upperWeight = 1f - lowerWeight;

        Tensor lowerLoss = torch.nn.functional.cross_entropy(prediction, lower, reduction: torch.nn.Reduction.None);
        Tensor upperLoss = torch.nn.functional.cross_entropy(prediction, upper, reduction: torch.nn.Reduction.None);

        return (lowerLoss * lowerWeight + upperLoss * upperWeight).view(rows, 4);
    }

    /// <summary>按输入尺寸构造 P3/P4/P5 的锚点网格（像素坐标 + 格点坐标 + 每锚点 stride）。</summary>
    private void EnsureGrid(Device device)
    {
        if (_gridDevice is not null
            && _anchorPoints is not null
            && _gridDevice.type == device.type
            && _gridDevice.index == device.index)
        {
            return;
        }

        var anchorValues = new List<float>();
        var cellValues = new List<float>();
        var strideValues = new List<float>();

        YoloAnchor[] anchors = YoloAnchorGrid.Create(_imageWidth, _imageHeight);
        foreach (YoloAnchor anchor in anchors)
        {
            cellValues.Add(anchor.CellX);
            cellValues.Add(anchor.CellY);
            anchorValues.Add(anchor.PixelX);
            anchorValues.Add(anchor.PixelY);
            strideValues.Add(anchor.Stride);
        }

        _anchorCount = anchors.Length;
        _anchorPoints = torch.tensor(anchorValues.ToArray(), new long[] { _anchorCount, 2 }, device: device);
        _anchorCells = torch.tensor(cellValues.ToArray(), new long[] { 1, _anchorCount, 2 }, device: device);
        _strides = torch.tensor(strideValues.ToArray(), new long[] { 1, _anchorCount, 1 }, device: device);
        _projection = torch.arange(_regressionMax, dtype: ScalarType.Float32, device: device)
            .view(1, 1, 1, _regressionMax);
        _gridDevice = device;
    }
}

