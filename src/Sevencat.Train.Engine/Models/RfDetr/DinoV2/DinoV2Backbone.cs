using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.RfDetr;

/// <summary>
/// DINOv2 ViT 主干：<c>patch_embed.proj</c> + <c>cls_token</c> + <c>pos_embed</c> +
/// <c>blocks.{i}</c> + <c>norm</c>，键名与 facebookresearch/dinov2 官方 <c>.pth</c> 一致。
/// <para>
/// 输出是完整 token 序列 <c>[B, 1 + H/p·W/p, D]</c>（含 cls token），供 RF-DETR 的多尺度投影使用。
/// 位置编码目前按「前 N 个 token」直接截取（不做双三次插值），因此输入尺寸应尽量等于训练尺寸。
/// </para>
/// </summary>
public sealed class DinoV2Backbone : Module<Tensor, Tensor>
{
    private readonly Sequential patch_embed;
    private readonly Parameter cls_token;
    private readonly Parameter pos_embed;
    private readonly ModuleList<DinoV2Block> blocks;
    private readonly LayerNorm norm;

    /// <summary>按配置创建主干（默认 dinov2-base）。</summary>
    public DinoV2Backbone(DinoV2Config? config = null, Device? device = null)
        : base("DinoV2")
    {
        Config = config ?? new DinoV2Config();

        // 用局部变量持有卷积，避免同名字段被额外注册成 `proj.*`（与官方键名会冲突）
        var projection = Conv2d(
            Config.InChannels,
            Config.EmbeddingSize,
            Config.PatchSize,
            Config.PatchSize,
            bias: true,
            device: device,
            dtype: ScalarType.Float32);

        patch_embed = Sequential(("proj", (Module<Tensor, Tensor>)projection));

        cls_token = Parameter(torch.zeros(new long[] { 1, 1, Config.EmbeddingSize }, device: device));
        pos_embed = Parameter(torch.zeros(
            new long[] { 1, Config.TokenCount, Config.EmbeddingSize }, device: device));

        blocks = new ModuleList<DinoV2Block>();
        for (int index = 0; index < Config.Depth; index++)
            blocks.Add(new DinoV2Block(Config, shifted: index % 2 == 1, device));

        norm = LayerNorm(Config.EmbeddingSize, eps: 1e-6, device: device, dtype: ScalarType.Float32);

        register_module("patch_embed", patch_embed);
        register_parameter("cls_token", cls_token);
        register_parameter("pos_embed", pos_embed);
        register_module("blocks", blocks);
        register_module("norm", norm);
    }

    /// <summary>结构参数。</summary>
    public DinoV2Config Config { get; }

    /// <summary>给定输入尺寸的 token 数（含 cls）。</summary>
    public int TokenCount(int height, int width)
    {
        if (height % Config.PatchSize != 0 || width % Config.PatchSize != 0)
        {
            throw new ArgumentException(
                $"输入高宽必须是 patch 尺寸 {Config.PatchSize} 的倍数，实际 {height}x{width}。");
        }

        return height / Config.PatchSize * (width / Config.PatchSize) + 1;
    }

    /// <inheritdoc />
    public override Tensor forward(Tensor input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.dim() != 4 || input.shape[1] != Config.InChannels)
        {
            throw new ArgumentException(
                $"DINOv2 输入必须是 [B,{Config.InChannels},H,W]，实际是 [{string.Join(",", input.shape)}]。",
                nameof(input));
        }

        int height = (int)input.shape[2];
        int width = (int)input.shape[3];
        int tokens = TokenCount(height, width);

        if (tokens > Config.TokenCount)
        {
            throw new ArgumentException(
                $"输入 {height}x{width} 需要 {tokens} 个 token，超过位置编码上限 {Config.TokenCount}。",
                nameof(input));
        }

        long batch = input.shape[0];

        Tensor patches = patch_embed.forward(input).flatten(2).transpose(1, 2);
        Tensor cls = cls_token.expand(batch, 1, Config.EmbeddingSize);
        Tensor hidden = torch.cat(new[] { cls, patches }, 1) + PositionFor(tokens, height, width);

        for (int index = 0; index < blocks.Count; index++)
            hidden = blocks[index].forward(hidden);

        return norm.forward(hidden);
    }

    /// <summary>
    /// 按指定 block 索引收集中间层输出，并沿通道拼接成 <c>[B, n·D, hp, wp]</c>。
    /// 这是官方 RF-DETR 投影器的输入（4 层 × 384 = 1536 通道）。
    /// </summary>
    public Tensor ForwardTapped(Tensor input, IReadOnlyList<int> blockIndexes)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(blockIndexes);

        if (input.dim() != 4 || input.shape[1] != Config.InChannels)
        {
            throw new ArgumentException(
                $"DINOv2 输入必须是 [B,{Config.InChannels},H,W]，实际是 [{string.Join(",", input.shape)}]。",
                nameof(input));
        }

        int height = (int)input.shape[2];
        int width = (int)input.shape[3];
        int tokens = TokenCount(height, width);
        long batch = input.shape[0];

        Tensor patches = patch_embed.forward(input).flatten(2).transpose(1, 2);
        Tensor cls = cls_token.expand(batch, 1, Config.EmbeddingSize);
        Tensor hidden = torch.cat(new[] { cls, patches }, 1) + PositionFor(tokens, height, width);

        var collected = new List<Tensor>(blockIndexes.Count);

        for (int index = 0; index < blocks.Count; index++)
        {
            hidden = blocks[index].forward(hidden);

            if (blockIndexes.Contains(index))
            {
                // timm 的 get_intermediate_layers(..., norm=True) 会对每个 tap 再走一次最终 norm
                collected.Add(PatchesToMap(norm.forward(hidden)));
            }
        }

        if (collected.Count == 0)
            throw new ArgumentException("blockIndexes 至少要包含一个有效的 block 索引。", nameof(blockIndexes));

        return torch.cat(collected.ToArray(), 1);
    }

    /// <summary>把 patch token（去掉 cls）还原成特征图 <c>[B,D,hp,wp]</c>，供多尺度投影使用。</summary>
    public Tensor PatchesToMap(Tensor tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        if (tokens.dim() != 3 || tokens.shape[2] != Config.EmbeddingSize)
        {
            throw new ArgumentException(
                $"tokens 必须是 [B,L,{Config.EmbeddingSize}]，实际是 [{string.Join(",", tokens.shape)}]。",
                nameof(tokens));
        }

        long batch = tokens.shape[0];
        long patches = tokens.shape[1] - 1;
        int side = (int)Math.Sqrt(patches);

        if ((long)side * side != patches)
        {
            throw new ArgumentException(
                $"patch 数 {patches} 不是完全平方数，无法还原成正方形特征图（当前只支持正方形输入）。",
                nameof(tokens));
        }

        return tokens
            .narrow(1, 1, patches)
            .transpose(1, 2)
            .contiguous()
            .view(batch, Config.EmbeddingSize, side, side);
    }

    /// <summary>
    /// 取位置编码：token 数与训练尺寸一致时直接截取；否则按官方做法做**双三次插值**。
    /// </summary>
    private Tensor PositionFor(int tokens, int height, int width)
    {
        if (tokens == Config.TokenCount)
            return pos_embed;

        int sourceSide = Config.PatchCountPerSide;
        int targetHeight = height / Config.PatchSize;
        int targetWidth = width / Config.PatchSize;

        Tensor patchEmbed = pos_embed
            .narrow(1, 1, sourceSide * sourceSide)
            .view(1, sourceSide, sourceSide, Config.EmbeddingSize)
            .permute(0, 3, 1, 2);

        Tensor resized = torch.nn.functional.interpolate(
            patchEmbed,
            new long[] { targetHeight, targetWidth },
            mode: InterpolationMode.Bicubic,
            align_corners: false);

        Tensor resizedPatches = resized
            .permute(0, 2, 3, 1)
            .contiguous()
            .view(1, targetHeight * targetWidth, Config.EmbeddingSize);

        return torch.cat(new[] { pos_embed.narrow(1, 0, 1), resizedPatches }, 1);
    }
}
