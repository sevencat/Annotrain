using TorchSharp;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.Yolo;

/// <summary>
/// ultralytics 的 <c>PSABlock</c>：<c>x += Attention(x)</c> 再接 <c>x += FFN(x)</c>，
/// 其中 FFN 是 <c>Conv(c, 2c, 1, SiLU) → Conv(2c, c, 1, 无激活)</c>。
/// <para>子模块名 <c>attn</c> / <c>ffn</c> 与 ultralytics 一致，键形如 <c>model.10.m.0.ffn.0.conv.weight</c>。</para>
/// </summary>
public sealed class PsaBlockModule : Module<Tensor, Tensor>
{
    private readonly Module<Tensor, Tensor> attn;
    private readonly Module<Tensor, Tensor> ffn;
    private readonly bool _add;

    /// <summary>创建 PSABlock。</summary>
    public PsaBlockModule(int channels, bool shortcut = true, Device? device = null)
        : base("PSABlock")
    {
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "通道数必须大于 0。");

        _add = shortcut;
        attn = new YoloAttentionModule(channels, 0.5, null, device);

        ffn = new YoloSequential(
            "Sequential",
            ("0", new ConvModule(channels, channels * 2, 1, 1, device)),
            ("1", new ConvModule(channels * 2, channels, 1, 1, device, activation: false)));

        register_module("attn", attn);
        register_module("ffn", ffn);
    }

    /// <summary>
    /// 注意力层（导出时展开）。
    /// <para>
    /// 必须用转换属性而不是自动属性：TorchSharp 按<b>字段名</b>反射注册子模块，
    /// 自动属性会引入 <c>&lt;Attention&gt;k__BackingField</c> 这个备份字段，
    /// 于是 <c>state_dict</c> 里同一份权重出现两次（官方权重只认 <c>attn.*</c>，strict 加载会失败）。
    /// </para>
    /// </summary>
    public YoloAttentionModule Attention => (YoloAttentionModule)attn;

    /// <summary>FFN（导出时展开；两个 ConvModule）。</summary>
    public YoloSequential FeedForward => (YoloSequential)ffn;

    /// <summary>是否做残差相加。</summary>
    public bool HasShortcut => _add;

    /// <inheritdoc />
    public override Tensor forward(Tensor input)
    {
        Tensor output = attn.forward(input);
        if (_add)
            output = input + output;

        Tensor feedForward = ffn.forward(output);
        return _add ? output + feedForward : feedForward;
    }
}
