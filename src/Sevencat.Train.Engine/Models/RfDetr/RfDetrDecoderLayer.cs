using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.RfDetr;

/// <summary>
/// 解码器层（RF-DETR 风格）：query 自注意力 + norm → 可变形注意力 + norm → FFN + norm。
/// 参考点由调用方逐层细化后传入，因此本层不含框回归参数。
/// </summary>
public sealed class RfDetrDecoderLayer : Module<Tensor[], Tensor>
{
    private readonly RfDetrAttention self_attn;
    private readonly LayerNorm norm1;
    private readonly MultiScaleDeformableAttention deform_attn;
    private readonly LayerNorm norm2;
    private readonly LayerNorm norm3;
    private readonly Linear ffn0;
    private readonly Linear ffn1;

    /// <summary>创建解码器层。</summary>
    /// <param name="embeddingSize">嵌入维度。</param>
    /// <param name="heads">自注意力头数（官方 sa 8）。</param>
    /// <param name="levels">多尺度档数。</param>
    /// <param name="points">每尺度采样点数。</param>
    /// <param name="ffnSize">FFN 隐层宽度。</param>
    /// <param name="device">设备。</param>
    /// <param name="deformHeads">可变形注意力头数（官方 ca 16）；0 表示与 <paramref name="heads"/> 相同。</param>
    public RfDetrDecoderLayer(
        int embeddingSize,
        int heads,
        int levels,
        int points,
        int ffnSize,
        Device? device = null,
        int deformHeads = 0)
        : base("DecoderLayer")
    {
        int crossHeads = deformHeads > 0 ? deformHeads : heads;

        self_attn = new RfDetrAttention(embeddingSize, heads, device);
        norm1 = LayerNorm(embeddingSize, device: device, dtype: ScalarType.Float32);
        deform_attn = new MultiScaleDeformableAttention(embeddingSize, crossHeads, levels, points, device);
        norm2 = LayerNorm(embeddingSize, device: device, dtype: ScalarType.Float32);
        norm3 = LayerNorm(embeddingSize, device: device, dtype: ScalarType.Float32);
        ffn0 = Linear(embeddingSize, ffnSize, device: device, dtype: ScalarType.Float32);
        ffn1 = Linear(ffnSize, embeddingSize, device: device, dtype: ScalarType.Float32);

        register_module("self_attn", self_attn);
        register_module("norm1", norm1);
        register_module("deform_attn", deform_attn);
        register_module("norm2", norm2);
        register_module("norm3", norm3);
        register_module("ffn0", ffn0);
        register_module("ffn1", ffn1);
    }

    /// <summary>前向：<c>queries [B,Q,D]</c> + 参考点 <c>[B,Q,levels,2]</c> + 各尺度特征。</summary>
    public Tensor Forward(Tensor queries, Tensor referencePoints, IReadOnlyList<Tensor> levelFeatures)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(referencePoints);
        ArgumentNullException.ThrowIfNull(levelFeatures);

        Tensor hidden = queries + self_attn.forward(norm1.forward(queries));
        hidden = hidden + deform_attn.Forward(norm2.forward(hidden), referencePoints, levelFeatures);
        hidden = hidden + ffn1.forward(ffn0.forward(norm3.forward(hidden)).relu());
        return hidden;
    }

    /// <inheritdoc />
    public override Tensor forward(Tensor[] input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length < 3)
            throw new ArgumentException("输入应为 [queries, referencePoints, level0, …]。", nameof(input));

        return Forward(input[0], input[1], input.Skip(2).ToArray());
    }
}
