using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.RfDetr;

/// <summary>DINOv2 ViT 的结构参数（默认对应 <c>dinov2-base</c>）。</summary>
public sealed record DinoV2Config(
    int ImageSize = 518,
    int PatchSize = 14,
    int InChannels = 3,
    int EmbeddingSize = 768,
    int Depth = 12,
    int Heads = 12,
    int MlpRatio = 4,
    int WindowSize = 0)
{
    /// <summary>
    /// 窗口注意力的窗口边长（patch 数）。<c>0</c> 表示全局注意力；
    /// 大于 0 时所有 block 都用窗口注意力，并按 block 序号交替做半窗口循环移位
    /// （RF-DETR 的 <c>dinov2_windowed_*</c> 用于高分辨率输入）。
    /// </summary>
    public int WindowSize { get; init; } = WindowSize;

    /// <summary>每个 patch 的边长（= PatchSize）。</summary>
    public int PatchCountPerSide => ImageSize / PatchSize;

    /// <summary>token 数（含 cls）。</summary>
    public int TokenCount => PatchCountPerSide * PatchCountPerSide + 1;
}

/// <summary>LayerScale：逐通道缩放，官方键名为 <c>blocks.{i}.ls1.gamma</c>。</summary>
public sealed class DinoV2LayerScale : Module<Tensor, Tensor>
{
    private readonly Parameter gamma;
    private readonly int _size;

    /// <summary>创建 LayerScale（DINOv2 初始值 1.0）。</summary>
    public DinoV2LayerScale(int size, Device? device = null) : base("LayerScale")
    {
        _size = size;
        gamma = Parameter(torch.ones(new long[] { size }, device: device));
        register_parameter("gamma", gamma);
    }

    /// <inheritdoc />
    public override Tensor forward(Tensor input) => input * gamma;
}

/// <summary>多头自注意力（DINOv2：qkv 合并为一个 Linear，含 bias）。</summary>
public sealed class DinoV2Attention : Module<Tensor, Tensor>
{
    private readonly Linear qkv;
    private readonly Linear proj;
    private readonly int _heads;
    private readonly int _headDim;
    private readonly int _windowSize;
    private readonly bool _shifted;

    /// <summary>创建注意力。</summary>
    /// <param name="embeddingSize">嵌入维度。</param>
    /// <param name="heads">头数。</param>
    /// <param name="windowSize">窗口边长；<c>0</c> 表示全局注意力。</param>
    /// <param name="shifted">窗口注意力是否做半窗口循环移位（交替使用）。</param>
    /// <param name="device">设备。</param>
    public DinoV2Attention(
        int embeddingSize,
        int heads,
        int windowSize = 0,
        bool shifted = false,
        Device? device = null)
        : base("Attention")
    {
        if (embeddingSize % heads != 0)
            throw new ArgumentException($"嵌入维度 {embeddingSize} 必须能被头数 {heads} 整除。", nameof(heads));
        if (windowSize < 0)
            throw new ArgumentOutOfRangeException(nameof(windowSize), windowSize, "窗口边长不能为负。");

        _heads = heads;
        _headDim = embeddingSize / heads;
        _windowSize = windowSize;
        _shifted = shifted;

        qkv = Linear(embeddingSize, embeddingSize * 3, device: device, dtype: ScalarType.Float32);
        proj = Linear(embeddingSize, embeddingSize, device: device, dtype: ScalarType.Float32);

        register_module("qkv", qkv);
        register_module("proj", proj);
    }

    /// <inheritdoc />
    public override Tensor forward(Tensor input)
    {
        ArgumentNullException.ThrowIfNull(input);

        long batch = input.shape[0];
        long length = input.shape[1];
        long patches = length - 1;
        int side = (int)Math.Sqrt(patches);

        // 全局注意力：未启用窗口，或 patch 网格放不进一个窗口。
        if (_windowSize <= 0 || (long)side * side != patches || side <= _windowSize)
            return Attend(input);

        if (side % _windowSize != 0)
        {
            throw new ArgumentException(
                $"patch 网格边长 {side} 必须是窗口边长 {_windowSize} 的整数倍。", nameof(input));
        }

        int embedding = _heads * _headDim;
        long windows = (side / _windowSize) * (side / _windowSize);
        int shift = _shifted ? _windowSize / 2 : 0;

        Tensor map = input.narrow(1, 1, patches).view(batch, side, side, embedding);

        if (shift > 0)
            map = torch.roll(map, new long[] { -shift, -shift }, new long[] { 1, 2 });

        Tensor windowed = map
            .view(batch, side / _windowSize, _windowSize, side / _windowSize, _windowSize, embedding)
            .permute(0, 1, 3, 2, 4, 5)
            .contiguous()
            .view(batch * windows, _windowSize * _windowSize, embedding);

        Tensor attended = Attend(windowed)
            .view(batch, side / _windowSize, side / _windowSize, _windowSize, _windowSize, embedding)
            .permute(0, 1, 3, 2, 4, 5)
            .contiguous()
            .view(batch, side, side, embedding);

        if (shift > 0)
            attended = torch.roll(attended, new long[] { shift, shift }, new long[] { 1, 2 });

        // cls token 仍需要全局信息：单独对全序列做一次注意力，只取它的输出。
        Tensor cls = Attend(input).narrow(1, 0, 1);
        Tensor flat = attended.contiguous().view(batch, patches, embedding);

        return torch.cat(new[] { cls, flat }, 1);
    }

    /// <summary>标准多头注意力；窗口路径把每个窗口当成一个批次项复用这里。</summary>
    private Tensor Attend(Tensor input)
    {
        long batch = input.shape[0];
        long tokens = input.shape[1];

        Tensor packed = qkv.forward(input).view(batch, tokens, 3, _heads, _headDim).permute(2, 0, 3, 1, 4);
        Tensor weights = (packed[0].matmul(packed[1].transpose(-1, -2)) / Math.Sqrt(_headDim)).softmax(-1);
        Tensor merged = weights
            .matmul(packed[2])
            .transpose(1, 2)
            .contiguous()
            .view(batch, tokens, _heads * _headDim);

        return proj.forward(merged);
    }
}

/// <summary>DINOv2 的 MLP（字段名 <c>fc1</c>/<c>fc2</c> 与官方 <c>state_dict</c> 一致）。</summary>
public sealed class DinoV2Mlp : Module<Tensor, Tensor>
{
    private readonly Linear fc1;
    private readonly Linear fc2;

    /// <summary>创建 MLP（GELU 激活）。</summary>
    public DinoV2Mlp(int embeddingSize, int hiddenSize, Device? device = null) : base("Mlp")
    {
        fc1 = Linear(embeddingSize, hiddenSize, device: device, dtype: ScalarType.Float32);
        fc2 = Linear(hiddenSize, embeddingSize, device: device, dtype: ScalarType.Float32);

        register_module("fc1", fc1);
        register_module("fc2", fc2);
    }

    /// <inheritdoc />
    public override Tensor forward(Tensor input) => fc2.forward(fc1.forward(input).gelu());
}

/// <summary>DINOv2 的 Transformer block：Pre-LN + 注意力 + LayerScale + MLP + LayerScale。</summary>
public sealed class DinoV2Block : Module<Tensor, Tensor>
{
    private readonly LayerNorm norm1;
    private readonly DinoV2Attention attn;
    private readonly DinoV2LayerScale ls1;
    private readonly LayerNorm norm2;
    private readonly DinoV2Mlp mlp;
    private readonly DinoV2LayerScale ls2;

    /// <summary>创建一个 block。</summary>
    /// <param name="config">结构参数。</param>
    /// <param name="shifted">窗口注意力是否做半窗口移位（按 block 序号交替）。</param>
    /// <param name="device">设备。</param>
    public DinoV2Block(DinoV2Config config, bool shifted = false, Device? device = null) : base("Block")
    {
        ArgumentNullException.ThrowIfNull(config);

        int hidden = config.EmbeddingSize;
        int mlpHidden = hidden * config.MlpRatio;

        norm1 = LayerNorm(hidden, eps: 1e-6, device: device, dtype: ScalarType.Float32);
        attn = new DinoV2Attention(hidden, config.Heads, config.WindowSize, shifted, device);
        ls1 = new DinoV2LayerScale(hidden, device);
        norm2 = LayerNorm(hidden, eps: 1e-6, device: device, dtype: ScalarType.Float32);
        mlp = new DinoV2Mlp(hidden, mlpHidden, device);
        ls2 = new DinoV2LayerScale(hidden, device);

        register_module("norm1", norm1);
        register_module("attn", attn);
        register_module("ls1", ls1);
        register_module("norm2", norm2);
        register_module("mlp", mlp);
        register_module("ls2", ls2);
    }

    /// <inheritdoc />
    public override Tensor forward(Tensor input)
    {
        Tensor hidden = input + ls1.forward(attn.forward(norm1.forward(input)));
        return hidden + ls2.forward(mlp.forward(norm2.forward(hidden)));
    }
}
