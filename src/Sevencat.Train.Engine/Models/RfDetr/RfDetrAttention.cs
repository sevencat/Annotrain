using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.RfDetr;

/// <summary>
/// 多头自注意力，**键名与 <c>nn.MultiheadAttention</c> 完全一致**
/// （<c>in_proj_weight</c> / <c>in_proj_bias</c> / <c>out_proj.*</c>），
/// 但前向自己算，避免依赖 TorchSharp 中 batch_first 等参数的具体实现。
/// </summary>
public sealed class RfDetrAttention : Module<Tensor, Tensor>
{
    private readonly Parameter in_proj_weight;
    private readonly Parameter in_proj_bias;
    private readonly Linear out_proj;
    private readonly int _heads;
    private readonly int _headDim;

    /// <summary>创建自注意力。</summary>
    public RfDetrAttention(int embeddingSize, int heads, Device? device = null) : base("Attention")
    {
        if (embeddingSize % heads != 0)
            throw new ArgumentException($"嵌入维度 {embeddingSize} 必须能被头数 {heads} 整除。", nameof(heads));

        _heads = heads;
        _headDim = embeddingSize / heads;

        in_proj_weight = Parameter(torch.empty(
            new long[] { embeddingSize * 3, embeddingSize }, device: device, dtype: ScalarType.Float32));
        in_proj_bias = Parameter(torch.zeros(
            new long[] { embeddingSize * 3 }, device: device, dtype: ScalarType.Float32));
        out_proj = Linear(embeddingSize, embeddingSize, device: device, dtype: ScalarType.Float32);

        register_parameter("in_proj_weight", in_proj_weight);
        register_parameter("in_proj_bias", in_proj_bias);
        register_module("out_proj", out_proj);

        // 与 nn.MultiheadAttention._reset_parameters 一致：xavier_uniform + 0 偏置。
        using (torch.no_grad())
            torch.nn.init.xavier_uniform_(in_proj_weight);
    }

    /// <inheritdoc />
    public override Tensor forward(Tensor input)
    {
        ArgumentNullException.ThrowIfNull(input);

        long batch = input.shape[0];
        long tokens = input.shape[1];
        int embedding = _heads * _headDim;

        Tensor packed = input
            .matmul(in_proj_weight.transpose(0, 1))
            .add(in_proj_bias)
            .view(batch, tokens, 3, _heads, _headDim)
            .permute(2, 0, 3, 1, 4);

        Tensor weights = (packed[0].matmul(packed[1].transpose(-1, -2)) / Math.Sqrt(_headDim)).softmax(-1);
        Tensor merged = weights
            .matmul(packed[2])
            .transpose(1, 2)
            .contiguous()
            .view(batch, tokens, embedding);

        return out_proj.forward(merged);
    }
}
