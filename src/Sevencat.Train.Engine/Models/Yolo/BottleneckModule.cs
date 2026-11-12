using TorchSharp;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.Yolo;

/// <summary>
/// ultralytics 的 <c>Bottleneck</c>：<c>Conv(c1, c_, 3) → Conv(c_, c2, 3)</c>，
/// <c>shortcut</c> 为真且通道一致时做残差相加。
/// </summary>
public sealed class BottleneckModule : Module<Tensor, Tensor>
{
    private readonly Module<Tensor, Tensor> cv1;
    private readonly Module<Tensor, Tensor> cv2;
    private readonly bool _shortcut;

    /// <summary>创建 Bottleneck；<paramref name="expansion"/> 对应 ultralytics 的 <c>e</c>（默认 0.5，C2f 内用 1.0）。</summary>
    public BottleneckModule(
        int inputChannels,
        int outputChannels,
        bool shortcut = true,
        double expansion = 0.5,
        Device? device = null)
        : base("Bottleneck")
    {
        if (inputChannels <= 0 || outputChannels <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputChannels), inputChannels, "通道数必须大于 0。");
        if (expansion is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(expansion), expansion, "expansion 必须在 (0,1]。");

        int hiddenChannels = (int)(outputChannels * expansion);
        _shortcut = shortcut && inputChannels == outputChannels;

        cv1 = new ConvModule(inputChannels, hiddenChannels, 3, 1, device);
        cv2 = new ConvModule(hiddenChannels, outputChannels, 3, 1, device);

        register_module("cv1", cv1);
        register_module("cv2", cv2);
    }

    /// <summary>第一个卷积（导出 ONNX 时按层展开）。</summary>
    public ConvModule Cv1 => (ConvModule)cv1;

    /// <summary>第二个卷积（导出 ONNX 时按层展开）。</summary>
    public ConvModule Cv2 => (ConvModule)cv2;

    /// <summary>是否做残差相加（通道一致且构造时 <c>shortcut</c> 为真）。</summary>
    public bool HasShortcut => _shortcut;

    /// <inheritdoc />
    public override Tensor forward(Tensor input)
    {
        Tensor output = cv2.forward(cv1.forward(input));
        return _shortcut ? input + output : output;
    }
}

