using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.RfDetr;

/// <summary>
/// 多尺度可变形注意力（MSDeformAttn 的最小实现）：每个 query 在每个尺度上采 <c>Points</c> 个点，
/// 采样位置 = 参考点 + 预测偏移，采样用 <c>grid_sample</c>（双线性、<c>align_corners=false</c>、边界外补 0），
/// 因此**可微**，训练与 ONNX（<c>GridSample</c>）都能走同一条路径。
/// <para>
/// 与官方 Deformable DETR 一致：偏移先按该尺度 <c>(W,H)</c> 归一化；权重在 <c>Levels × Points</c> 维上 softmax；
/// 各尺度贡献求和后由 <c>output_proj</c> 融合。
/// </para>
/// </summary>
public sealed class MultiScaleDeformableAttention : Module<Tensor[], Tensor>
{
    private readonly Linear sampling_offsets;
    private readonly Linear attention_weights;
    private readonly Linear value_proj;
    private readonly Linear output_proj;
    private readonly int _embedding;
    private readonly int _heads;
    private readonly int _levels;
    private readonly int _points;

    /// <summary>按嵌入维度、头数、尺度数与采样点数创建。</summary>
    public MultiScaleDeformableAttention(
        int embeddingSize,
        int heads,
        int levels,
        int points = 4,
        Device? device = null)
        : base("MSDeformAttn")
    {
        if (embeddingSize % heads != 0)
            throw new ArgumentException($"嵌入维度 {embeddingSize} 必须能被头数 {heads} 整除。", nameof(heads));
        if (levels <= 0 || points <= 0)
            throw new ArgumentOutOfRangeException(nameof(levels), levels, "尺度数与采样点数必须大于 0。");

        _embedding = embeddingSize;
        _heads = heads;
        _levels = levels;
        _points = points;
        HeadDim = embeddingSize / heads;

        sampling_offsets = Linear(
            embeddingSize, heads * levels * points * 2, device: device, dtype: ScalarType.Float32);
        attention_weights = Linear(
            embeddingSize, heads * levels * points, device: device, dtype: ScalarType.Float32);
        value_proj = Linear(embeddingSize, embeddingSize, device: device, dtype: ScalarType.Float32);
        output_proj = Linear(embeddingSize, embeddingSize, device: device, dtype: ScalarType.Float32);

        register_module("sampling_offsets", sampling_offsets);
        register_module("attention_weights", attention_weights);
        register_module("value_proj", value_proj);
        register_module("output_proj", output_proj);
    }

    /// <summary>每个头的维度。</summary>
    public int HeadDim { get; }

    /// <summary>尺度数。</summary>
    public int Levels => _levels;

    /// <summary>每尺度采样点数。</summary>
    public int Points => _points;

    /// <summary>
    /// 前向。<paramref name="query"/> 为 <c>[B,Lq,D]</c>，<paramref name="referencePoints"/> 为
    /// <c>[B,Lq,Levels,2]</c>（归一化 0~1），<paramref name="levelFeatures"/> 每项 <c>[B,D,H,W]</c>。
    /// </summary>
    public Tensor Forward(
        Tensor query,
        Tensor referencePoints,
        IReadOnlyList<Tensor> levelFeatures)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(referencePoints);
        ArgumentNullException.ThrowIfNull(levelFeatures);

        if (levelFeatures.Count != _levels)
        {
            throw new ArgumentException(
                $"需要 {_levels} 个尺度的特征，实际 {levelFeatures.Count} 个。",
                nameof(levelFeatures));
        }

        long batch = query.shape[0];
        long queries = query.shape[1];

        if (referencePoints.shape[1] != queries || referencePoints.shape[2] != _levels)
        {
            throw new ArgumentException(
                $"referencePoints 必须是 [B,{queries},{_levels},2]，"
                + $"实际是 [{string.Join(",", referencePoints.shape)}]。",
                nameof(referencePoints));
        }

        Tensor offsets = sampling_offsets
            .forward(query)
            .view(batch, queries, _heads, _levels, _points, 2);

        Tensor weights = attention_weights
            .forward(query)
            .view(batch, queries, _heads, _levels * _points)
            .softmax(-1)
            .view(batch, queries, _heads, _levels, _points);

        Tensor accumulated = torch.zeros(
            new long[] { batch, _heads, HeadDim, queries },
            dtype: ScalarType.Float32,
            device: query.device);

        for (int level = 0; level < _levels; level++)
        {
            Tensor feature = levelFeatures[level];
            if (feature.dim() != 4 || feature.shape[1] != _embedding)
            {
                throw new ArgumentException(
                    $"第 {level} 个尺度的特征必须是 [B,{_embedding},H,W]，"
                    + $"实际是 [{string.Join(",", feature.shape)}]。",
                    nameof(levelFeatures));
            }

            long height = feature.shape[2];
            long width = feature.shape[3];

            Tensor value = value_proj
                .forward(feature.flatten(2).transpose(1, 2))
                .transpose(1, 2)
                .reshape(batch, _embedding, height, width);

            Tensor normalizer = torch.tensor(
                new[] { (float)width, (float)height },
                new long[] { 1, 1, 1, 1, 1, 2 },
                device: query.device);

            Tensor locations = referencePoints.narrow(2, level, 1).unsqueeze(2).unsqueeze(4)
                + offsets.narrow(3, level, 1) / normalizer;

            Tensor grid = (locations * 2f - 1f)
                .squeeze(3)
                .permute(0, 2, 1, 3, 4);

            Tensor expandedValue = value
                .view(batch, _heads, HeadDim, height, width)
                .reshape(batch * _heads, HeadDim, height, width);

            Tensor sampled = torch.nn.functional
                .grid_sample(expandedValue, grid.reshape(batch * _heads, queries, _points, 2))
                .reshape(batch, _heads, HeadDim, queries, _points);

            Tensor levelWeights = weights
                .narrow(3, level, 1)
                .squeeze(3)
                .permute(0, 2, 1, 3)
                .unsqueeze(2);

            accumulated += (sampled * levelWeights).sum(new long[] { 4 });
        }

        Tensor merged = accumulated.transpose(1, 3).contiguous().view(batch, queries, _embedding);
        return output_proj.forward(merged);
    }

    /// <summary>
    /// <c>[query, referencePoints, level0, level1, …]</c> 形式的入口（便于放进 <c>ModuleList</c> 或统一调度）。
    /// </summary>
    public override Tensor forward(Tensor[] input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length != 2 + _levels)
        {
            throw new ArgumentException(
                $"输入应为 [query, referencePoints] + {_levels} 个尺度特征，实际 {input.Length} 项。",
                nameof(input));
        }

        return Forward(input[0], input[1], input.Skip(2).ToArray());
    }
}
