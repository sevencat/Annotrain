using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.SimpleDetr;

/// <summary>RF-DETR 风格模型的输出：归一化 <c>cxcywh</c> 框与分类 logits。</summary>
public sealed record SimpleDetrOutput(Tensor Boxes, Tensor Logits);

/// <summary>导出一致性所需的结构参数（ONNX 导出器按它重建同名子图）。</summary>
public sealed record SimpleDetrArchitecture(
    int ClassCount,
    int Queries,
    int EncoderLayers,
    int DecoderLayers,
    int Heads,
    int FeedForwardSize,
    IReadOnlyList<int> BlocksPerStage)
{
    /// <summary>可写入检查点元数据的字符串，如 <c>detr-q20-e2-d2-h8</c>。</summary>
    public string ToMetadata() =>
        $"detr-q{Queries}-e{EncoderLayers}-d{DecoderLayers}-h{Heads}";
}

/// <summary>
/// 多头自注意力 / 交叉注意力的最小实现（<c>batch_first</c> 语义，输入输出都是 <c>[B,L,D]</c>）。
/// <para>
/// 不用 <c>nn.MultiheadAttention</c> 的原因：它把 q/k/v 合成一个 <c>in_proj_weight</c>，
/// 键名与我们要对齐的命名不一致，也不便于导出 ONNX 时逐项展开。
/// </para>
/// </summary>
public sealed class SimpleDetrAttention : Module<Tensor, Tensor>
{
    private readonly Linear q_proj;
    private readonly Linear k_proj;
    private readonly Linear v_proj;
    private readonly Linear out_proj;
    private readonly int _heads;
    private readonly int _headDim;
    private readonly double _scale;

    /// <summary>按嵌入维度与头数创建注意力。</summary>
    public SimpleDetrAttention(int embeddingSize, int heads, Device? device = null)
        : base("Attention")
    {
        if (embeddingSize <= 0 || heads <= 0 || embeddingSize % heads != 0)
            throw new ArgumentException($"嵌入维度 {embeddingSize} 必须能被头数 {heads} 整除。", nameof(heads));

        _heads = heads;
        _headDim = embeddingSize / heads;
        _scale = 1.0 / Math.Sqrt(_headDim);

        q_proj = Linear(embeddingSize, embeddingSize, device: device, dtype: ScalarType.Float32);
        k_proj = Linear(embeddingSize, embeddingSize, device: device, dtype: ScalarType.Float32);
        v_proj = Linear(embeddingSize, embeddingSize, device: device, dtype: ScalarType.Float32);
        out_proj = Linear(embeddingSize, embeddingSize, device: device, dtype: ScalarType.Float32);

        register_module("q_proj", q_proj);
        register_module("k_proj", k_proj);
        register_module("v_proj", v_proj);
        register_module("out_proj", out_proj);
    }

    /// <summary>自注意力（q = k = v）。</summary>
    public override Tensor forward(Tensor input) => Attend(input, input, input);

    /// <summary>交叉注意力：query 来自解码器，key/value 来自编码器记忆。</summary>
    public Tensor Attend(Tensor query, Tensor key, Tensor value)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        long batch = query.shape[0];
        long queryLength = query.shape[1];
        long keyLength = key.shape[1];

        Tensor q = q_proj.forward(query).view(batch, queryLength, _heads, _headDim).transpose(1, 2);
        Tensor k = k_proj.forward(key).view(batch, keyLength, _heads, _headDim).transpose(1, 2);
        Tensor v = v_proj.forward(value).view(batch, keyLength, _heads, _headDim).transpose(1, 2);

        Tensor scores = q.matmul(k.transpose(-1, -2)) * _scale;
        Tensor weights = scores.softmax(-1);

        return out_proj.forward(
            weights.matmul(v).transpose(1, 2).contiguous().view(batch, queryLength, _heads * _headDim));
    }
}

/// <summary>Pre-LN 的 Transformer 编码层：自注意力 + FFN，各自带残差。</summary>
public sealed class SimpleDetrEncoderLayer : Module<Tensor, Tensor>
{
    private readonly SimpleDetrAttention self_attn;
    private readonly LayerNorm norm1;
    private readonly Sequential ffn;
    private readonly LayerNorm norm2;

    /// <summary>创建编码层（FFN 隐藏维度由调用方给出，通常为 4 倍）。</summary>
    public SimpleDetrEncoderLayer(int embeddingSize, int heads, int feedForwardSize, Device? device = null)
        : base("EncoderLayer")
    {
        self_attn = new SimpleDetrAttention(embeddingSize, heads, device);
        norm1 = LayerNorm(embeddingSize, device: device, dtype: ScalarType.Float32);
        ffn = Sequential(
            ("0", (Module<Tensor, Tensor>)Linear(embeddingSize, feedForwardSize, device: device, dtype: ScalarType.Float32)),
            ("1", ReLU()),
            ("2", Linear(feedForwardSize, embeddingSize, device: device, dtype: ScalarType.Float32)));
        norm2 = LayerNorm(embeddingSize, device: device, dtype: ScalarType.Float32);

        register_module("self_attn", self_attn);
        register_module("norm1", norm1);
        register_module("ffn", ffn);
        register_module("norm2", norm2);
    }

    /// <inheritdoc />
    public override Tensor forward(Tensor input)
    {
        Tensor hidden = input + self_attn.forward(norm1.forward(input));
        return hidden + ffn.forward(norm2.forward(hidden));
    }
}

/// <summary>Pre-LN 的解码层：自注意力（query 之间）+ 交叉注意力（到编码器记忆）+ FFN。</summary>
public sealed class SimpleDetrDecoderLayer : Module<Tensor, Tensor>
{
    private readonly SimpleDetrAttention self_attn;
    private readonly LayerNorm norm1;
    private readonly SimpleDetrAttention cross_attn;
    private readonly LayerNorm norm2;
    private readonly Sequential ffn;
    private readonly LayerNorm norm3;

    /// <summary>创建解码层。</summary>
    public SimpleDetrDecoderLayer(int embeddingSize, int heads, int feedForwardSize, Device? device = null)
        : base("DecoderLayer")
    {
        self_attn = new SimpleDetrAttention(embeddingSize, heads, device);
        norm1 = LayerNorm(embeddingSize, device: device, dtype: ScalarType.Float32);
        cross_attn = new SimpleDetrAttention(embeddingSize, heads, device);
        norm2 = LayerNorm(embeddingSize, device: device, dtype: ScalarType.Float32);
        ffn = Sequential(
            ("0", Linear(embeddingSize, feedForwardSize, device: device, dtype: ScalarType.Float32)),
            ("1", ReLU()),
            ("2", Linear(feedForwardSize, embeddingSize, device: device, dtype: ScalarType.Float32)));
        norm3 = LayerNorm(embeddingSize, device: device, dtype: ScalarType.Float32);

        register_module("self_attn", self_attn);
        register_module("norm1", norm1);
        register_module("cross_attn", cross_attn);
        register_module("norm2", norm2);
        register_module("ffn", ffn);
        register_module("norm3", norm3);
    }

    /// <summary>query 与编码器记忆同时传入。</summary>
    public Tensor ForwardWithMemory(Tensor query, Tensor memory)
    {
        Tensor hidden = query + self_attn.forward(norm1.forward(query));
        hidden += cross_attn.Attend(norm2.forward(hidden), memory, memory);
        return hidden + ffn.forward(norm3.forward(hidden));
    }

    /// <inheritdoc />
    public override Tensor forward(Tensor input) => ForwardWithMemory(input, input);
}

/// <summary>
/// 简化 CNN 主干：<c>stem</c>（3×3/2）+ 最多 4 个 stage，每 stage 由一个带下采样的
/// BasicBlock 与若干恒等 BasicBlock 组成，输出 <c>[B,512,H/16,W/16]</c>。
/// <para>
/// 真正的 RF-DETR 用 DINOv2（ViT）主干；这里换成可训练、可导出、可测试的最小替代，
/// 差异与后续替换计划见 plan.md 的 P2 决策记录。
/// </para>
/// </summary>
public sealed class SimpleDetrBackbone : Module<Tensor, Tensor>
{
    /// <summary>各 stage 的通道宽度。</summary>
    public static readonly int[] Widths = { 64, 128, 256, 512 };

    private readonly Sequential stem;
    private readonly ModuleList<Module<Tensor, Tensor>> stages;
    private readonly int _outputChannels;

    /// <summary>按「每个 stage 的 block 数」创建主干。</summary>
    public SimpleDetrBackbone(IReadOnlyList<int> blocksPerStage, Device? device = null)
        : base("Backbone")
    {
        ArgumentNullException.ThrowIfNull(blocksPerStage);
        if (blocksPerStage.Count == 0)
            throw new ArgumentException("主干至少需要 1 个 stage。", nameof(blocksPerStage));

        stem = Sequential(
            ("0", Conv2d(3, 64, 3, 2, 1, bias: false, device: device, dtype: ScalarType.Float32)),
            ("1", BatchNorm2d(64, device: device, dtype: ScalarType.Float32)),
            ("2", ReLU()));

        stages = new ModuleList<Module<Tensor, Tensor>>();
        int stageCount = Math.Min(blocksPerStage.Count, Widths.Length);
        _outputChannels = Widths[Math.Max(stageCount, 1) - 1];

        for (int stage = 0; stage < stageCount; stage++)
        {
            int inputChannels = stage == 0 ? 64 : Widths[stage - 1];
            int outputChannels = Widths[stage];
            int stride = stage == 0 ? 1 : 2;

            stages.Add(new SimpleDetrBasicBlock(inputChannels, outputChannels, stride, device));

            for (int index = 1; index < blocksPerStage[stage]; index++)
                stages.Add(new SimpleDetrBasicBlock(outputChannels, outputChannels, 1, device));
        }

        register_module("stem", stem);
        register_module("stages", stages);
    }

    /// <summary>输出通道数（= 最后一个实际 stage 的宽度）。</summary>
    public int OutputChannels => _outputChannels;

    /// <inheritdoc />
    public override Tensor forward(Tensor input)
    {
        Tensor current = stem.forward(input);

        for (int index = 0; index < stages.Count; index++)
            current = stages[index].forward(current);

        return current;
    }
}

/// <summary>ResNet 风格 BasicBlock：两个 3×3 卷积 + 残差。</summary>
public sealed class SimpleDetrBasicBlock : Module<Tensor, Tensor>
{
    private readonly Sequential conv1;
    private readonly Sequential conv2;
    private readonly Sequential? downsample;

    /// <summary>创建 BasicBlock；通道或步长变化时用 1×1 卷积对齐残差。</summary>
    public SimpleDetrBasicBlock(int inputChannels, int outputChannels, int stride = 1, Device? device = null)
        : base("BasicBlock")
    {
        conv1 = Sequential(
            ("0", Conv2d(inputChannels, outputChannels, 3, stride, 1, bias: false, device: device, dtype: ScalarType.Float32)),
            ("1", BatchNorm2d(outputChannels, device: device, dtype: ScalarType.Float32)),
            ("2", ReLU()));

        conv2 = Sequential(
            ("0", Conv2d(outputChannels, outputChannels, 3, 1, 1, bias: false, device: device, dtype: ScalarType.Float32)),
            ("1", BatchNorm2d(outputChannels, device: device, dtype: ScalarType.Float32)));

        register_module("conv1", conv1);
        register_module("conv2", conv2);

        if (inputChannels != outputChannels || stride != 1)
        {
            downsample = Sequential(
                ("0", Conv2d(inputChannels, outputChannels, 1, stride, 0, bias: false, device: device, dtype: ScalarType.Float32)),
                ("1", BatchNorm2d(outputChannels, device: device, dtype: ScalarType.Float32)));
            register_module("downsample", downsample);
        }
    }

    /// <inheritdoc />
    public override Tensor forward(Tensor input)
    {
        Tensor hidden = conv2.forward(conv1.forward(input));
        Tensor residual = downsample is null ? input : downsample.forward(input);
        return (hidden + residual).relu();
    }
}
