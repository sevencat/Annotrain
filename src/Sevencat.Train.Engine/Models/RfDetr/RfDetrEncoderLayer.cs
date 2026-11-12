using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.RfDetr;

/// <summary>
/// 编码器层（RF-DETR 风格）：自注意力 + norm → 可变形注意力 + norm → FFN + norm，
/// 三处都是**残差加**，norm 作用在残差加之前（与官方一致）。
/// </summary>
public sealed class RfDetrEncoderLayer : Module<Tensor[], Tensor>
{
    private readonly RfDetrAttention self_attn;
    private readonly LayerNorm norm1;
    private readonly MultiScaleDeformableAttention deform_attn;
    private readonly LayerNorm norm2;
    private readonly LayerNorm norm3;
    private readonly Linear ffn0;
    private readonly Linear ffn1;

    /// <summary>创建编码器层。</summary>
    public RfDetrEncoderLayer(
        int embeddingSize,
        int heads,
        int levels,
        int points,
        int ffnSize,
        Device? device = null)
        : base("EncoderLayer")
    {
        self_attn = new RfDetrAttention(embeddingSize, heads, device);
        norm1 = LayerNorm(embeddingSize, device: device, dtype: ScalarType.Float32);
        deform_attn = new MultiScaleDeformableAttention(embeddingSize, heads, levels, points, device);
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

    /// <summary>前向：<c>tokens [B,L,D]</c> + 参考点 <c>[B,L,levels,2]</c> + 各尺度特征。</summary>
    public Tensor Forward(Tensor tokens, Tensor referencePoints, IReadOnlyList<Tensor> levelFeatures)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(referencePoints);
        ArgumentNullException.ThrowIfNull(levelFeatures);

        Tensor hidden = tokens + self_attn.forward(norm1.forward(tokens));
        hidden = hidden + deform_attn.Forward(norm2.forward(hidden), referencePoints, levelFeatures);
        hidden = hidden + ffn1.forward(ffn0.forward(norm3.forward(hidden)).relu());
        return hidden;
    }

    /// <inheritdoc />
    public override Tensor forward(Tensor[] input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length < 3)
            throw new ArgumentException("输入应为 [tokens, referencePoints, level0, …]。", nameof(input));

        return Forward(input[0], input[1], input.Skip(2).ToArray());
    }
}
