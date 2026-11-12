using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.RfDetr;

/// <summary>
/// 官方的 Conv+BN 对（`cv1` / `cv2` 里的结构）：<c>conv</c> + 只含 weight/bias 的 <c>bn</c>
/// （`track_running_stats=false`，与 checkpoint 里只有 <c>bn.weight</c>/<c>bn.bias</c> 一致）。
/// </summary>
public sealed class RfDetrConvBn : Module<Tensor, Tensor>
{
    private readonly Conv2d conv;
    private readonly BatchNorm2d bn;

    /// <summary>创建 Conv+BN。</summary>
    public RfDetrConvBn(
        int inChannels,
        int outChannels,
        int kernel,
        int stride = 1,
        int padding = 0,
        Device? device = null)
        : base("ConvBn")
    {
        conv = Conv2d(
            inChannels, outChannels, kernel, stride, padding,
            bias: false, device: device, dtype: ScalarType.Float32);

        bn = BatchNorm2d(
            outChannels, eps: 1e-5, momentum: 0.1, affine: true,
            track_running_stats: false, device: device, dtype: ScalarType.Float32);

        register_module("conv", conv);
        register_module("bn", bn);
    }

    /// <inheritdoc />
    public override Tensor forward(Tensor input) => bn.forward(conv.forward(input));
}

/// <summary>Bottleneck：两个 3×3 Conv+BN 加残差（官方 C2f 的内部块）。</summary>
public sealed class RfDetrBottleneck : Module<Tensor, Tensor>
{
    private readonly RfDetrConvBn cv1;
    private readonly RfDetrConvBn cv2;

    /// <summary>创建 Bottleneck。</summary>
    public RfDetrBottleneck(int channels, Device? device = null) : base("Bottleneck")
    {
        cv1 = new RfDetrConvBn(channels, channels, kernel: 3, padding: 1, device: device);
        cv2 = new RfDetrConvBn(channels, channels, kernel: 3, padding: 1, device: device);

        register_module("cv1", cv1);
        register_module("cv2", cv2);
    }

    /// <inheritdoc />
    public override Tensor forward(Tensor input) => input + cv2.forward(cv1.forward(input));
}

/// <summary>
/// 通道维 LayerNorm（ConvNeXt 风格）：permute 到 NHWC → <c>F.layer_norm</c> → permute 回来，
/// 键名只有 <c>weight</c>/<c>bias</c>，对应官方 <c>projector.stages.0.1</c>。
/// </summary>
public sealed class RfDetrChannelNorm : Module<Tensor, Tensor>
{
    private readonly Parameter weight;
    private readonly Parameter bias;
    private readonly long _channels;

    /// <summary>创建通道归一化。</summary>
    public RfDetrChannelNorm(int channels, double eps = 1e-6, Device? device = null) : base("ChannelNorm")
    {
        _channels = channels;
        weight = Parameter(torch.ones(new long[] { channels }, device: device));
        bias = Parameter(torch.zeros(new long[] { channels }, device: device));

        register_parameter("weight", weight);
        register_parameter("bias", bias);
    }

    /// <inheritdoc />
    public override Tensor forward(Tensor input)
    {
        Tensor channelsLast = input.permute(0, 2, 3, 1);

        Tensor normalised = torch.nn.functional.layer_norm(
            channelsLast, new long[] { _channels }, weight, bias, 1e-6);

        return normalised.permute(0, 3, 1, 2);
    }
}
