using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.SimpleDetr;

/// <summary>
/// 简化的 DETR 风格检测模型（P2 的第一步）：CNN 主干 → 1×1 投影 → 位置编码 →
/// Transformer 编码器 → 可学习 query 的解码器 → 两个 MLP 头。
/// <para>
/// 与真正的 RF-DETR 的差异（见 plan.md P2 决策记录）：主干用 ResNet 风格 CNN 而不是 DINOv2，
/// 注意力是标准多头注意力而不是 deformable attention。这样可以在纯 TorchSharp / CPU 上
/// 训练与导出；代价是精度不如官方权重，目标先跑通「匹配 + 损失 + 导出」的整条链路。
/// <para>
/// 尺寸约定：主干固定 4 个 stage（stem 与 stage 0 各下采样 2 倍，stage 1~3 再各 2 倍），
/// 因此输入必须是 16 的倍数；<see cref="TokenCount"/> 依赖这个约定。
/// </para>
/// </para>
/// </summary>
public sealed class SimpleDetrModel : Module<Tensor, Tensor>
{
    /// <summary>嵌入维度（与 RF-DETR 的 256 一致）。</summary>
    public const int EmbeddingSize = 256;

    /// <summary>位置编码支持的最大 token 数（对应 1024×1024 输入 / 16 下采样）。</summary>
    public const int MaxTokens = 64 * 64;

    private readonly SimpleDetrBackbone backbone;
    private readonly Conv2d input_proj;
    private readonly Parameter pos_embed;
    private readonly ModuleList<SimpleDetrEncoderLayer> encoder;
    private readonly ModuleList<SimpleDetrDecoderLayer> decoder;
    private readonly Parameter query_embed;
    private readonly Linear denoising_box_proj;
    private readonly Parameter denoising_type_embed;
    private readonly Sequential box_head;
    private readonly Sequential class_head;

    /// <summary>创建模型；<paramref name="queries"/> 是每个样本的 query 数（默认 50）。</summary>
    public SimpleDetrModel(
        int classCount,
        int queries = 50,
        int encoderLayers = 2,
        int decoderLayers = 2,
        int heads = 8,
        int feedForwardSize = 1024,
        IReadOnlyList<int>? blocksPerStage = null,
        Device? device = null)
        : base("SimpleDetr")
    {
        if (classCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(classCount), classCount, "类别数必须大于 0。");
        if (queries <= 0)
            throw new ArgumentOutOfRangeException(nameof(queries), queries, "query 数必须大于 0。");

        ClassCount = classCount;
        Queries = queries;
        Architecture = new SimpleDetrArchitecture(
            classCount,
            queries,
            encoderLayers,
            decoderLayers,
            heads,
            feedForwardSize,
            (blocksPerStage ?? new[] { 2, 2, 2, 2 }).ToArray());

        backbone = new SimpleDetrBackbone(blocksPerStage ?? new[] { 2, 2, 2, 2 }, device);
        input_proj = Conv2d(
            backbone.OutputChannels,
            EmbeddingSize,
            1,
            1,
            0,
            bias: true,
            device: device,
            dtype: ScalarType.Float32);

        pos_embed = Parameter(torch.zeros(new long[] { 1, MaxTokens, EmbeddingSize }, device: device));

        encoder = new ModuleList<SimpleDetrEncoderLayer>();
        for (int index = 0; index < encoderLayers; index++)
            encoder.Add(new SimpleDetrEncoderLayer(EmbeddingSize, heads, feedForwardSize, device));

        decoder = new ModuleList<SimpleDetrDecoderLayer>();
        for (int index = 0; index < decoderLayers; index++)
            decoder.Add(new SimpleDetrDecoderLayer(EmbeddingSize, heads, feedForwardSize, device));

        query_embed = Parameter(torch.zeros(new long[] { queries, EmbeddingSize }, device: device));

        denoising_box_proj = Linear(4, EmbeddingSize, device: device, dtype: ScalarType.Float32);
        denoising_type_embed = Parameter(torch.zeros(new long[] { 1, EmbeddingSize }, device: device));

        box_head = Sequential(
            ("0", Linear(EmbeddingSize, EmbeddingSize, device: device, dtype: ScalarType.Float32)),
            ("1", ReLU()),
            ("2", Linear(EmbeddingSize, EmbeddingSize, device: device, dtype: ScalarType.Float32)),
            ("3", ReLU()),
            ("4", Linear(EmbeddingSize, 4, device: device, dtype: ScalarType.Float32)));

        class_head = Sequential(
            ("0", Linear(EmbeddingSize, EmbeddingSize, device: device, dtype: ScalarType.Float32)),
            ("1", ReLU()),
            ("2", Linear(EmbeddingSize, EmbeddingSize, device: device, dtype: ScalarType.Float32)),
            ("3", ReLU()),
            ("4", Linear(EmbeddingSize, classCount, device: device, dtype: ScalarType.Float32)));

        register_module("backbone", backbone);
        register_module("input_proj", input_proj);
        register_parameter("pos_embed", pos_embed);
        register_module("encoder", encoder);
        register_module("decoder", decoder);
        register_parameter("query_embed", query_embed);
        register_module("denoising_box_proj", denoising_box_proj);
        register_parameter("denoising_type_embed", denoising_type_embed);
        register_module("box_head", box_head);
        register_module("class_head", class_head);

        using (torch.no_grad())
        {
            // query / 位置编码用正态初始化：全零会让所有 query 输出完全相同。
            pos_embed.normal_(0, 0.02);
            query_embed.normal_(0, 0.02);
        }
    }

    /// <summary>类别数。</summary>
    public int ClassCount { get; }

    /// <summary>query 数（每张图最多输出多少个候选框）。</summary>
    public int Queries { get; }

    /// <summary>结构参数（ONNX 导出按它重建子图）。</summary>
    public SimpleDetrArchitecture Architecture { get; }

    /// <summary>单张图进入编码器的 token 数（由输入尺寸决定）。</summary>
    public int TokenCount(int inputHeight, int inputWidth)
    {
        if (inputHeight % 16 != 0 || inputWidth % 16 != 0)
        {
            throw new ArgumentException(
                $"输入高宽必须是 16 的倍数（主干下采样 16 倍），实际 {inputHeight}x{inputWidth}。");
        }

        return inputHeight / 16 * (inputWidth / 16);
    }

    /// <summary>前向：输出 <c>[B,Q,4+nc]</c>（前 4 列是 sigmoid 后的 cxcywh，其余是分类 logits）。</summary>
    public override Tensor forward(Tensor input)
    {
        SimpleDetrOutput output = ForwardDetections(input);
        return torch.cat(new[] { output.Boxes, output.Logits }, -1);
    }

    /// <summary>前向并拆成框 / 分类两部分（评估与导出用）。</summary>
    public SimpleDetrOutput ForwardDetections(Tensor input) => ForwardCore(input, null);

    /// <summary>带 CDN 去噪 query 的前向：输出 <c>[B,Q+D,4+nc]</c>（前 Q 个是匹配 query）。</summary>
    public SimpleDetrOutput ForwardWithDenoising(Tensor input, Tensor denoisedBoxes)
    {
        ArgumentNullException.ThrowIfNull(denoisedBoxes);
        return ForwardCore(input, BuildDenoisingQueries(denoisedBoxes));
    }

    /// <summary>把加噪框投影成 decoder query（<c>[B,D,4] → [B,D,EmbeddingSize]</c>，再加类型嵌入）。</summary>
    public Tensor BuildDenoisingQueries(Tensor boxes)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        if (boxes.dim() != 3 || boxes.shape[2] != 4)
            throw new ArgumentException($"boxes 必须是 [B,D,4]，实际 [{string.Join(",", boxes.shape)}]。", nameof(boxes));

        long batch = boxes.shape[0];
        long count = boxes.shape[1];

        Tensor projected = denoising_box_proj
            .forward(boxes.reshape(-1, 4))
            .view(batch, count, EmbeddingSize);

        return projected + denoising_type_embed.unsqueeze(0);
    }

    private SimpleDetrOutput ForwardCore(Tensor input, Tensor? extraQueries)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.dim() != 4 || input.shape[1] != 3)
        {
            throw new ArgumentException(
                $"SimpleDetr 输入必须是 [B,3,H,W]，实际是 [{string.Join(",", input.shape)}]。",
                nameof(input));
        }

        int tokens = TokenCount((int)input.shape[2], (int)input.shape[3]);
        if (tokens > MaxTokens)
        {
            throw new ArgumentException(
                $"输入 {input.shape[2]}x{input.shape[3]} 需要 {tokens} 个 token，"
                + $"超过位置编码上限 {MaxTokens}。",
                nameof(input));
        }

        long batch = input.shape[0];

        Tensor features = backbone.forward(input);
        Tensor memory = input_proj.forward(features).flatten(2).transpose(1, 2);
        memory = memory + pos_embed.narrow(1, 0, tokens);

        for (int index = 0; index < encoder.Count; index++)
            memory = encoder[index].forward(memory);

        Tensor queries = query_embed.unsqueeze(0).expand(batch, Queries, EmbeddingSize);

        if (extraQueries is not null)
            queries = torch.cat(new[] { queries, extraQueries }, 1);

        for (int index = 0; index < decoder.Count; index++)
            queries = decoder[index].ForwardWithMemory(queries, memory);

        return new SimpleDetrOutput(
            box_head.forward(queries).sigmoid(),
            class_head.forward(queries));
    }
}
