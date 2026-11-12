using Sevencat.Train.Engine.Core;
using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.RfDetr;

/// <summary>
/// RF-DETR 检测器（对齐官方结构）：DINOv2 ViT 主干 → 4 档投影（1/4、1/8、1/16、1/32）
/// → 多层可变形编码器 → 可学习 query + 逐层框细化的多层解码器 → 双 MLP 头。
/// <para>
/// 已对齐：官方 DINOv2 权重与键名、与 PyTorch 一致的多尺度可变形注意力、
/// 每档 <c>Conv1×1 + GroupNorm(32)</c>（最细一档接 <c>Conv3×3(stride 2)</c>）、
/// 编码器/解码器「自注意力 + 可变形注意力 + FFN」三段残差、解码器逐层参考点细化、
/// 与 <c>nn.MultiheadAttention</c> 相同的注意力键名。
/// </para>
/// <para>
/// 仍缺（见 plan.md P2-3）：两阶段提议（<c>enc_output</c> + top-k 选 query）、
/// 窗口注意力路径的 ONNX 导出、官方 RF-DETR 权重加载与 560/90 类精度对标。
/// </para>
/// </summary>
public sealed class RfDetrDetector : Module<Tensor, Tensor>, ILossExtrasProvider
{
    /// <summary>嵌入维度。</summary>
    public const int EmbeddingSize = 256;

    /// <summary>支持的最大多尺度档数。</summary>
    public const int MaxLevels = 4;

    private static readonly int[] Strides = { 4, 8, 16, 32 };

    /// <summary>官方 RF-DETR 要求输入分辨率是 56 的倍数（patch 14 × 下采样 4）。</summary>
    public const int OfficialInputMultiple = 56;

    private readonly DinoV2Backbone backbone;
    private readonly Conv2d? input_proj;
    private readonly RfDetrC2fProjector? c2f_projector;
    private readonly int[] _taps;
    private readonly ModuleList<RfDetrProjectionLevel>? projector;
    private readonly ModuleList<RfDetrEncoderLayer> encoder_layers;
    private readonly LayerNorm? encoder_norm;
    private readonly ModuleList<RfDetrDecoderLayer> decoder_layers;
    private readonly Parameter query_embed;
    private readonly Parameter refpoint_embed;
    private readonly RfDetrMlp? ref_point_head;
    private readonly int _refDims;
    private readonly ModuleList<Linear>? enc_output;
    private readonly ModuleList<LayerNorm>? enc_output_norm;
    private readonly ModuleList<RfDetrMlp>? enc_out_bbox_embed;
    private readonly ModuleList<Linear>? enc_out_class_embed;
    private readonly LayerNorm decoder_norm;
    private readonly RfDetrMlp bbox_embed;
    private readonly Linear class_embed;

    /// <summary>按类别数、query 数与（可选）DINOv2 配置创建。</summary>
    /// <param name="classCount">类别数。</param>
    /// <param name="queries">query 数（官方 300）。</param>
    /// <param name="backboneConfig">主干配置。</param>
    /// <param name="heads">编码器与自注意力头数（官方 sa 8）。</param>
    /// <param name="device">设备。</param>
    /// <param name="encoderLayers">编码器层数。</param>
    /// <param name="decoderLayers">解码器层数（官方 3）。</param>
    /// <param name="ffnSize">FFN 隐层宽度（官方 2048）。</param>
    /// <param name="points">可变形注意力每尺度采样点数（官方 2）。</param>
    /// <param name="levels">多尺度档数（官方 1）。</param>
    /// <param name="decoderHeads">解码器可变形注意力头数（官方 ca 16）；0 表示与 <paramref name="heads"/> 相同。</param>
    public RfDetrDetector(
        int classCount,
        int queries = 300,
        DinoV2Config? backboneConfig = null,
        int heads = 8,
        Device? device = null,
        int encoderLayers = 1,
        int decoderLayers = 2,
        int ffnSize = 1024,
        int points = 4,
        int levels = MaxLevels,
        int decoderHeads = 0,
        bool useRefPointHead = false,
        bool useTwoStage = false,
        int groupDetr = 1,
        bool useC2fProjector = false,
        int[]? taps = null)
        : base("RfDetr")
    {
        if (classCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(classCount), classCount, "类别数必须大于 0。");
        if (queries <= 0)
            throw new ArgumentOutOfRangeException(nameof(queries), queries, "query 数必须大于 0。");
        if (encoderLayers < 0 || decoderLayers <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(encoderLayers), encoderLayers, "编码器层数不能为负（官方取 0），解码器层数必须大于 0。");
        }
        if (levels <= 0 || levels > MaxLevels)
        {
            throw new ArgumentOutOfRangeException(
                nameof(levels), levels, $"多尺度档数必须在 1~{MaxLevels} 之间。");
        }

        ClassCount = classCount;
        Queries = queries;
        LevelCount = levels;
        DecoderHeads = decoderHeads > 0 ? decoderHeads : heads;
        UsesRefPointHead = useRefPointHead;
        UsesTwoStage = useTwoStage;
        _refDims = useRefPointHead ? 4 : 2;

        if (groupDetr <= 0)
            throw new ArgumentOutOfRangeException(nameof(groupDetr), groupDetr, "Group-DETR 组数必须大于 0。");

        GroupDetr = groupDetr;

        backbone = new DinoV2Backbone(backboneConfig, device);

        UsesC2fProjector = useC2fProjector;

        if (useC2fProjector)
        {
            if (levels != 1)
                throw new ArgumentException("C2f 融合投影器只产生单档特征，levels 必须为 1。", nameof(levels));

            _taps = taps ?? new[] { 2, 5, 8, 11 };
            c2f_projector = new RfDetrC2fProjector(
                backbone.Config.EmbeddingSize * _taps.Length, EmbeddingSize, bottlenecks: 3, device);
        }
        else
        {
            _taps = Array.Empty<int>();

            input_proj = Conv2d(
                backbone.Config.EmbeddingSize, EmbeddingSize, 1,
                bias: true, device: device, dtype: ScalarType.Float32);

            projector = new ModuleList<RfDetrProjectionLevel>();

            for (int level = 0; level < LevelCount; level++)
                projector.Add(new RfDetrProjectionLevel(EmbeddingSize, EmbeddingSize, level == 0, device));
        }

        encoder_layers = new ModuleList<RfDetrEncoderLayer>();

        for (int index = 0; index < encoderLayers; index++)
            encoder_layers.Add(new RfDetrEncoderLayer(EmbeddingSize, heads, LevelCount, points, ffnSize, device));

        LayerNorm? encoderNorm = null;

        if (encoderLayers > 0)
        {
            encoderNorm = LayerNorm(EmbeddingSize, device: device, dtype: ScalarType.Float32);
            register_module("encoder_norm", encoderNorm);
        }

        encoder_norm = encoderNorm;

        decoder_layers = new ModuleList<RfDetrDecoderLayer>();

        for (int index = 0; index < decoderLayers; index++)
        {
            decoder_layers.Add(new RfDetrDecoderLayer(
                EmbeddingSize, heads, LevelCount, points, ffnSize, device, deformHeads: DecoderHeads));
        }

        decoder_norm = LayerNorm(EmbeddingSize, device: device, dtype: ScalarType.Float32);

        query_embed = Parameter(torch.zeros(
            new long[] { (long)queries * groupDetr, EmbeddingSize }, device: device));

        refpoint_embed = Parameter(torch.zeros(
            new long[] { (long)queries * groupDetr, _refDims }, device: device));

        if (useRefPointHead)
        {
            // 官方 ref_point_head：4 维坐标的 sin/cos 编码（2×256 = 512）→ 256 → 256
            ref_point_head = new RfDetrMlp(
                EmbeddingSize * 2, EmbeddingSize, EmbeddingSize, layerCount: 2, device);

            register_module("ref_point_head", ref_point_head);
        }

        if (useTwoStage)
        {
            // 官方按 group 建 ModuleList：enc_output / enc_output_norm / 两个 enc_out 辅助头
            enc_output = new ModuleList<Linear>();
            enc_output_norm = new ModuleList<LayerNorm>();
            enc_out_bbox_embed = new ModuleList<RfDetrMlp>();
            enc_out_class_embed = new ModuleList<Linear>();

            for (int group = 0; group < groupDetr; group++)
            {
                enc_output.Add(Linear(EmbeddingSize, EmbeddingSize, device: device, dtype: ScalarType.Float32));
                enc_output_norm.Add(LayerNorm(EmbeddingSize, device: device, dtype: ScalarType.Float32));

                // 与官方一致：3 个 Linear，ReLU 在 forward 里函数式串联
                enc_out_bbox_embed.Add(new RfDetrMlp(EmbeddingSize, EmbeddingSize, 4, layerCount: 3, device));
                enc_out_class_embed.Add(
                    Linear(EmbeddingSize, classCount, device: device, dtype: ScalarType.Float32));
            }

            register_module("enc_output", enc_output);
            register_module("enc_output_norm", enc_output_norm);
            register_module("enc_out_bbox_embed", enc_out_bbox_embed);
            register_module("enc_out_class_embed", enc_out_class_embed);
        }

        bbox_embed = new RfDetrMlp(EmbeddingSize, EmbeddingSize, 4, layerCount: 3, device);
        class_embed = Linear(EmbeddingSize, classCount, device: device, dtype: ScalarType.Float32);

        register_module("backbone", backbone);

        if (input_proj is not null)
            register_module("input_proj", input_proj);

        if (projector is not null)
            register_module("projector", projector);

        if (c2f_projector is not null)
            register_module("projector", c2f_projector);
        register_module("encoder_layers", encoder_layers);
        register_module("decoder_layers", decoder_layers);
        register_module("decoder_norm", decoder_norm);
        register_parameter("query_embed", query_embed);
        register_parameter("refpoint_embed", refpoint_embed);
        register_module("bbox_embed", bbox_embed);
        register_module("class_embed", class_embed);

        using (torch.no_grad())
        {
            query_embed.normal_(0, 0.02);
            refpoint_embed.fill_(0.5);
        }
    }

    /// <summary>类别数。</summary>
    public int ClassCount { get; }

    /// <summary>多尺度档数（官方 1，最多 4 档：1/4、1/8、1/16、1/32）。</summary>
    public int LevelCount { get; }

    /// <summary>解码器可变形注意力头数（官方 16）。</summary>
    public int DecoderHeads { get; }

    /// <summary>是否使用官方的 4 维参考点（cxcywh）+ <c>ref_point_head</c>。</summary>
    public bool UsesRefPointHead { get; }

    /// <summary>是否启用两阶段提议（`enc_output` + top-k 选 query）。</summary>
    public bool UsesTwoStage { get; }

    /// <summary>
    /// 最近一次 <see cref="forward"/> 里**两阶段辅助头的原始输出**（逐 group 的最后一组）：
    /// <c>ClassLogits [B, L, nc]</c> 与 <c>Boxes [B, L, 4]</c>（已 sigmoid）。
    /// <para>
    /// 用途：官方 RF-DETR 会对这些提议做"深监督"辅助损失（`enc_out_class_embed` / `enc_out_bbox_embed`），
    /// 损失层需要拿到它们。这里只做**只读暴露**，不改变任何 <see cref="Module{Tensor, Tensor}"/> 接口。
    /// </para>
    /// <para>
    /// 注意：内部已 <c>detach()</c>，避免长期持有计算图；因此它只用于前向后的诊断/损失计算，
    /// 不能反向传播。未启用两阶段时为 <c>(null, null)</c>。
    /// </para>
    /// </summary>
    public (Tensor? ClassLogits, Tensor? Boxes) LastProposal { get; private set; }

    /// <summary>
    /// 键名与 <c>DetrLoss.ProposalBoxesKey</c> / <c>ProposalLogitsKey</c> 一致
    /// （<c>Models</c> 不能引用 <c>Training</c>，故此处用字面量，并由 <c>RfDetrStructureTests</c> 断言两侧一致）。
    /// </summary>
    public const string ProposalBoxesKey = "proposal_boxes";

    /// <summary>见 <see cref="ProposalBoxesKey"/>。</summary>
    public const string ProposalLogitsKey = "proposal_logits";

    /// <summary>
    /// 实现 <see cref="ILossExtrasProvider"/>：把两阶段提议交给损失做深监督。
    /// 未启用两阶段时返回 <c>null</c>，训练器自动走普通损失（零开销）。
    /// </summary>
    public IReadOnlyDictionary<string, Tensor>? GetLossExtras()
    {
        if (LastProposal.Boxes is null || LastProposal.ClassLogits is null)
            return null;

        return new Dictionary<string, Tensor>(2, StringComparer.Ordinal)
        {
            [ProposalBoxesKey] = LastProposal.Boxes,
            [ProposalLogitsKey] = LastProposal.ClassLogits,
        };
    }

    /// <summary>Group-DETR 组数（官方 13）；推理只输出第 0 组。</summary>
    public int GroupDetr { get; }

    /// <summary>是否使用官方的 C2f 融合投影器（单档特征，输入为 4 个 ViT 层的拼接）。</summary>
    public bool UsesC2fProjector { get; }
    /// <summary>query 数。</summary>
    public int Queries { get; }

    /// <summary>主干结构参数。</summary>
    public DinoV2Config BackboneConfig => backbone.Config;

    /// <summary>编码器层数。</summary>
    public int EncoderLayerCount => (int)encoder_layers.Count;

    /// <summary>解码器层数。</summary>
    public int DecoderLayerCount => (int)decoder_layers.Count;

    /// <summary>检查输入尺寸是否满足官方要求（不一致时给出明确提示）。</summary>
    public static void ValidateOfficialInputSize(int height, int width)
    {
        if (height <= 0 || width <= 0)
            throw new ArgumentException($"输入高宽必须为正，实际 {height}x{width}。");
        if (height % OfficialInputMultiple != 0 || width % OfficialInputMultiple != 0)
        {
            throw new ArgumentException(
                $"官方 RF-DETR 要求输入高宽都是 {OfficialInputMultiple} 的倍数，实际 {height}x{width}。");
        }
    }

    /// <inheritdoc />
    public override Tensor forward(Tensor input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.dim() != 4 || input.shape[1] != backbone.Config.InChannels)
        {
            throw new ArgumentException(
                $"检测器输入必须是 [B,{backbone.Config.InChannels},H,W]，"
                + $"实际是 [{string.Join(",", input.shape)}]。",
                nameof(input));
        }

        long batch = input.shape[0];
        int height = (int)input.shape[2];
        int width = (int)input.shape[3];

        Tensor[] levels;

        if (c2f_projector is not null)
        {
            // 官方路径：主干 4 个深度层拼接 → C2f 融合 → 单张 256 维特征图
            levels = new[] { c2f_projector.forward(backbone.ForwardTapped(input, _taps)) };
        }
        else
        {
            Tensor tokens = backbone.forward(input);
            Tensor map = backbone.PatchesToMap(tokens);
            Tensor projected = input_proj!.forward(map);

            levels = BuildLevels(projected, height, width);
        }
        Tensor memory = levels[0].flatten(2).transpose(1, 2);
        Tensor reference = ReferenceGrid(
            (int)levels[0].shape[2], (int)levels[0].shape[3], batch, input.device);

        for (int index = 0; index < encoder_layers.Count; index++)
            memory = encoder_layers[index].Forward(memory, reference, levels);

        memory = encoder_norm is null ? memory : encoder_norm.forward(memory);

        long totalQueries = (long)Queries * GroupDetr;

        Tensor queries = query_embed.unsqueeze(0).expand(batch, totalQueries, EmbeddingSize);
        Tensor references = refpoint_embed.unsqueeze(0).expand(batch, totalQueries, _refDims).contiguous();

        if (enc_output is not null)
        {
            // 两阶段：每组用自己的 enc_output + 辅助头预测类别/框，各组独立 top-k 选出 Queries 个提议
            var queryGroups = new List<Tensor>(GroupDetr);
            var referenceGroups = new List<Tensor>(GroupDetr);

            for (int group = 0; group < GroupDetr; group++)
            {
                Tensor proposal = enc_output_norm![group].forward(enc_output[group].forward(memory));
                Tensor proposalReference = enc_out_bbox_embed![group].forward(proposal).sigmoid();
                Tensor proposalLogits = enc_out_class_embed![group].forward(proposal);

                Tensor indices = proposalLogits.max(-1).values.topk(Queries, dim: 1).indexes;
                Tensor gatherIndex = indices.unsqueeze(-1);

                queryGroups.Add(proposal.gather(1, gatherIndex.expand(batch, Queries, EmbeddingSize)));
                referenceGroups.Add(proposalReference.gather(
                    1, gatherIndex.expand(batch, Queries, proposalReference.shape[^1])));

                // 供两阶段辅助损失使用；**不 detach**，否则辅助损失只能"算"不能"学"。
                // 生命周期：保留到下一次 forward，训练器在同一个 step 内取用，不会无限累积。
                LastProposal = (proposalLogits, proposalReference);
            }

            queries = queries + torch.cat(queryGroups.ToArray(), 1);
            references = torch.cat(referenceGroups.ToArray(), 1);
        }

        Tensor centers = references.narrow(2, 0, 2).contiguous();
        Tensor points = ExpandReference(centers);

        for (int index = 0; index < decoder_layers.Count; index++)
        {
            Tensor layerInput = queries;

            if (ref_point_head is not null)
            {
                // 官方做法：参考点做 sin/cos 位置编码后过 MLP，再与 query 相加
                layerInput = queries + ref_point_head.forward(SineEmbed(references));
            }

            queries = decoder_layers[index].Forward(layerInput, points, levels);

            // 逐层框细化：预测中心点增量，夹到 0~1 作为下一层参考点（宽高当前保持不变）。
            // 官方 lite_refpoint_refine：复用 bbox_embed 的前两维作为中心点增量，不额外加参数
            centers = centers.add(bbox_embed.forward(queries).narrow(-1, 0, 2)).sigmoid();
            points = ExpandReference(centers);

            if (_refDims == 4)
                references = torch.cat(new[] { centers, references.narrow(2, 2, 2) }, -1);
        }

        queries = decoder_norm.forward(queries.narrow(1, 0, Queries));

        Tensor boxes = bbox_embed.forward(queries).sigmoid();
        Tensor logits = class_embed.forward(queries);
        return torch.cat(new[] { boxes, logits }, -1);
    }

    /// <summary>把参考点 <c>[B,Q,2]</c> 复制到每个尺度，得到 <c>[B,Q,Levels,2]</c>。</summary>
    private Tensor ExpandReference(Tensor centers)
    {
        long batch = centers.shape[0];
        long queries = centers.shape[1];

        return centers
            .view(batch, queries, 1, 2)
            .expand(batch, queries, LevelCount, 2)
            .contiguous();
    }

    /// <summary>
    /// 把参考点（官方 4 维 cxcywh，0~1）编成正弦位置编码 <c>[B,Q,4·2·feats]</c>；
    /// <c>feats = D/4</c>，因此 4 个坐标共 <c>2D</c> 维，正好对上官方 <c>ref_point_head</c> 的 512 输入。
    /// </summary>
    private static Tensor SineEmbed(Tensor position)
    {
        int dimensions = (int)position.shape[2];
        int feats = EmbeddingSize / 4;

        Tensor exponent = torch.arange(feats, dtype: ScalarType.Float32, device: position.device) / feats;
        Tensor scales = torch.pow(
            torch.tensor(10000.0, dtype: ScalarType.Float32, device: position.device), exponent);

        Tensor angles = position.unsqueeze(3) * (2.0 * Math.PI) / scales.view(1, 1, 1, feats);
        Tensor embedded = torch.cat(new[] { angles.sin(), angles.cos() }, -1);

        return embedded.view(position.shape[0], position.shape[1], dimensions * 2L * feats);
    }

    /// <summary>
    /// 把主干特征图投影成 4 档：1/4、1/8、1/16、1/32。最细一档先插值到 1/2，
    /// 再由分支内的 stride-2 卷积降到 1/4（与官方 C2 分支的两步结构一致）。
    /// </summary>
    private Tensor[] BuildLevels(Tensor projected, int imageHeight, int imageWidth)
    {
        var levels = new Tensor[LevelCount];

        for (int level = 0; level < LevelCount; level++)
        {
            int stride = level == 0 ? Strides[level] / 2 : Strides[level];
            long height = Math.Max(1, imageHeight / stride);
            long width = Math.Max(1, imageWidth / stride);

            Tensor resized = torch.nn.functional.interpolate(
                projected,
                new long[] { height, width },
                mode: InterpolationMode.Bilinear,
                align_corners: false);

            levels[level] = projector[level].forward(resized);
        }

        return levels;
    }

    /// <summary>归一化参考点网格（0~1），顺序与展平后的特征图一致。</summary>
    private Tensor ReferenceGrid(int height, int width, long batch, Device device)
    {
        int count = height * width;
        var values = new float[count * LevelCount * 2];

        for (int index = 0; index < count; index++)
        {
            int row = index / width;
            int column = index % width;
            float x = (column + 0.5f) / width;
            float y = (row + 0.5f) / height;

            for (int level = 0; level < LevelCount; level++)
            {
                values[(index * LevelCount + level) * 2] = x;
                values[(index * LevelCount + level) * 2 + 1] = y;
            }
        }

        Tensor grid = TorchInterop.FromArray(values, new long[] { 1, count, LevelCount, 2 }, device);
        return grid.expand(batch, count, LevelCount, 2).contiguous();
    }
}
