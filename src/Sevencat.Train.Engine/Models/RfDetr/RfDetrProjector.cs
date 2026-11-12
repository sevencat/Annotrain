using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.RfDetr;

/// <summary>
/// 多尺度投影的**单档**分支：<c>Conv1×1 → GroupNorm(32)</c>，最细一档额外接
/// <c>Conv3×3(stride 2)</c>，对齐官方 <c>Projector</c> 的每档结构。
/// </summary>
public sealed class RfDetrProjectionLevel : Module<Tensor, Tensor>
{
    /// <summary>GroupNorm 的分组数（官方固定 32）。</summary>
    public const int Groups = 32;

    private readonly Conv2d conv;
    private readonly GroupNorm norm;
    private readonly Conv2d? down;

    /// <summary>创建一档投影。</summary>
    /// <param name="inChannels">输入通道数（主干嵌入维度）。</param>
    /// <param name="outChannels">输出通道数（256）。</param>
    /// <param name="downsample">是否为最细一档（需要再下采样 2 倍）。</param>
    /// <param name="device">设备。</param>
    public RfDetrProjectionLevel(
        int inChannels,
        int outChannels,
        bool downsample,
        Device? device = null)
        : base("ProjectionLevel")
    {
        if (outChannels % Groups != 0)
        {
            throw new ArgumentException(
                $"输出通道 {outChannels} 必须是 GroupNorm 分组数 {Groups} 的倍数。", nameof(outChannels));
        }

        conv = Conv2d(inChannels, outChannels, 1, bias: true, device: device, dtype: ScalarType.Float32);
        norm = GroupNorm(Groups, outChannels, eps: 1e-5, device: device, dtype: ScalarType.Float32);

        register_module("conv", conv);
        register_module("norm", norm);

        if (downsample)
        {
            down = Conv2d(
                outChannels, outChannels, 3, stride: 2, padding: 1,
                bias: true, device: device, dtype: ScalarType.Float32);

            register_module("down", down);
        }
    }

    /// <summary>是否带额外下采样。</summary>
    public bool HasDownsample => down is not null;

    /// <inheritdoc />
    public override Tensor forward(Tensor input)
    {
        Tensor output = norm.forward(conv.forward(input));
        return down is null ? output : down.forward(output);
    }
}
