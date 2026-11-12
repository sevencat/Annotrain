using TorchSharp;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.Yolo;

/// <summary>
/// ultralytics 的 <c>Attention</c>（YOLOv11 的 C2PSA 内部）：一个 1×1 卷积同时产出 q / k / v，
/// 再对展平成 token 序列的特征做单头组内自注意力。
/// <para>
/// 形状流：<c>[B,C,H,W] → qkv: [B, C+2·heads·key, H, W] → reshape [B, heads, 2·key+head_dim, H·W]</c>，
/// 沿通道维切成 q / k / v；<c>attn = softmax((q·scale)ᵀ · k)</c>，输出 <c>v · attnᵀ</c> 还原成特征图，
/// 再叠加一个深度可分离位置编码 <c>pe</c>，最后由 <c>proj</c> 投影。
/// </para>
/// <para>子模块名 <c>qkv</c> / <c>proj</c> / <c>pe</c> 与 ultralytics 一致，键形如 <c>model.10.m.0.attn.qkv.conv.weight</c>。</para>
/// </summary>
public sealed class YoloAttentionModule : Module<Tensor, Tensor>
{
    private readonly Module<Tensor, Tensor> qkv;
    private readonly Module<Tensor, Tensor> proj;
    private readonly Module<Tensor, Tensor> pe;
    private readonly float _scale;

    /// <summary>创建注意力层；<paramref name="heads"/> 缺省时按 <c>max(C / 64, 1)</c>（与 ultralytics 一致）。</summary>
    public YoloAttentionModule(
        int channels,
        double attentionRatio = 0.5,
        int? heads = null,
        Device? device = null)
        : base("Attention")
    {
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "通道数必须大于 0。");

        int headCount = heads ?? Math.Max(channels / 64, 1);
        if (headCount <= 0 || channels % headCount != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(heads), headCount, $"head 数必须整除通道数 {channels}。");
        }

        int headChannels = channels / headCount;
        int keyChannels = (int)(headChannels * attentionRatio);
        if (keyChannels <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attentionRatio), attentionRatio, $"key 通道数必须大于 0（head 通道 {headChannels}）。");
        }

        HeadCount = headCount;
        HeadChannels = headChannels;
        KeyChannels = keyChannels;
        _scale = (float)Math.Pow(keyChannels, -0.5);

        int hidden = channels + keyChannels * headCount * 2;

        qkv = new ConvModule(channels, hidden, 1, 1, device, activation: false);
        proj = new ConvModule(channels, channels, 1, 1, device, activation: false);
        pe = new ConvModule(channels, channels, 3, 1, device, groups: channels, activation: false);

        register_module("qkv", qkv);
        register_module("proj", proj);
        register_module("pe", pe);
    }

    /// <summary>注意力头数。</summary>
    public int HeadCount { get; }

    /// <summary>每个头的 value 通道数（<c>C / heads</c>）。</summary>
    public int HeadChannels { get; }

    /// <summary>每个头的 key / query 通道数（<c>head_dim * attention_ratio</c>）。</summary>
    public int KeyChannels { get; }

    /// <summary>qkv 卷积的输出通道数（<c>C + 2·heads·key</c>）。</summary>
    public int QkvChannels => HeadChannels + 2 * KeyChannels * HeadCount;

    /// <summary>qkv 卷积（导出时展开；无激活）。</summary>
    public ConvModule Qkv => (ConvModule)qkv;

    /// <summary>输出投影（导出时展开；无激活）。</summary>
    public ConvModule Projection => (ConvModule)proj;

    /// <summary>深度可分离位置编码（导出时展开；无激活）。</summary>
    public ConvModule PositionalEncoding => (ConvModule)pe;

    /// <inheritdoc />
    public override Tensor forward(Tensor input)
    {
        ArgumentNullException.ThrowIfNull(input);

        long batch = input.shape[0];
        long channels = input.shape[1];
        long height = input.shape[2];
        long width = input.shape[3];
        long tokens = height * width;

        Tensor packed = qkv.forward(input).reshape(
            new long[] { batch, HeadCount, 2L * KeyChannels + HeadChannels, tokens });

        Tensor q = packed.narrow(2, 0, KeyChannels) * _scale;
        Tensor k = packed.narrow(2, KeyChannels, KeyChannels);
        Tensor v = packed.narrow(2, 2L * KeyChannels, HeadChannels);

        Tensor attention = torch.matmul(q.transpose(2, 3), k).softmax(3);
        Tensor attended = torch.matmul(v, attention.transpose(2, 3))
            .reshape(new long[] { batch, channels, height, width });

        Tensor positioned = attended + pe.forward(v.reshape(new long[] { batch, channels, height, width }));
        return proj.forward(positioned);
    }
}
