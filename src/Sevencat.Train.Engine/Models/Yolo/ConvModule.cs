using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.Yolo;

/// <summary>
/// ultralytics 的 <c>Conv</c> 模块：<c>Conv2d(bias=false) → BatchNorm2d → SiLU</c>。
/// <para>
/// 字段名直接使用 ultralytics 的层名（<c>conv</c> / <c>bn</c> / <c>act</c>），而不是
/// <c>_camelCase</c>：TorchSharp 0.107 通过反射按<b>字段名</b>注册子模块，且没有
/// <c>ComponentNameAttribute</c>；只有字段名等于目标键名，导出的 <c>state_dict</c>
/// 才是 <c>model.0.conv.weight</c> 这种 ultralytics 可直接加载的形式。
/// 理由与取舍见 plan.md「决策记录」。
/// </para>
/// </summary>
public sealed class ConvModule : Module<Tensor, Tensor>
{
    private readonly Module<Tensor, Tensor> conv;
    private readonly Module<Tensor, Tensor> bn;
    private readonly Module<Tensor, Tensor> act;

    /// <summary>
    /// ultralytics <c>Conv</c> 里 BatchNorm 的 epsilon。
    /// <para>
    /// ultralytics 的 <c>BatchNorm2d</c> 子类把默认值改成了 <c>eps=1e-3</c>（PyTorch 默认是 1e-5），
    /// 这里必须跟着改：否则用官方权重做前向会系统性偏离（实测最大误差可达 9.4），
    /// 自己从零训练则看不出来（训练会把 running stats 学成适配值）。
    /// </para>
    /// </summary>
    public const double BatchNormEpsilon = 1e-3;

    /// <summary>ultralytics 的 BN momentum（0.03，同样不是 PyTorch 默认的 0.1）。</summary>
    public const double BatchNormMomentum = 0.03;

    /// <summary>按 ultralytics <c>Conv(c1, c2, k, s, g, act)</c> 语义创建模块（padding = k / 2）。</summary>
    /// <param name="inputChannels">输入通道。</param>
    /// <param name="outputChannels">输出通道。</param>
    /// <param name="kernelSize">卷积核尺寸（padding 取 k / 2）。</param>
    /// <param name="stride">步长。</param>
    /// <param name="device">计算设备。</param>
    /// <param name="groups">分组数；YOLOv11 的 <c>DWConv</c> 用 <c>groups = gcd(c1, c2)</c>。</param>
    /// <param name="activation">是否接 SiLU；<c>act=False</c> 的层（Attention 的 qkv/proj/pe、PSA 的 ffn 尾层）用 <c>Identity</c>。</param>
    public ConvModule(
        int inputChannels,
        int outputChannels,
        int kernelSize = 1,
        int stride = 1,
        Device? device = null,
        int groups = 1,
        bool activation = true)
        : base("Conv")
    {
        if (inputChannels <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputChannels), inputChannels, "输入通道必须大于 0。");
        if (outputChannels <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputChannels), outputChannels, "输出通道必须大于 0。");
        if (kernelSize <= 0 || stride <= 0)
            throw new ArgumentOutOfRangeException(nameof(kernelSize), kernelSize, "卷积核与步长必须大于 0。");
        if (groups <= 0 || inputChannels % groups != 0 || outputChannels % groups != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(groups), groups, $"分组数必须整除输入通道 {inputChannels} 与输出通道 {outputChannels}。");
        }

        OutputChannels = outputChannels;
        KernelSize = kernelSize;
        Stride = stride;
        Groups = groups;
        HasActivation = activation;

        conv = nn.Conv2d(
            inputChannels,
            outputChannels,
            kernelSize,
            stride,
            kernelSize / 2,
            groups: groups,
            bias: false,
            device: device,
            dtype: ScalarType.Float32);
        bn = nn.BatchNorm2d(
            outputChannels,
            eps: BatchNormEpsilon,
            momentum: BatchNormMomentum,
            device: device,
            dtype: ScalarType.Float32);
        act = activation ? nn.SiLU() : nn.Identity();

        register_module("conv", conv);
        register_module("bn", bn);
        register_module("act", act);
    }

    /// <summary>输出通道数。</summary>
    public int OutputChannels { get; }

    /// <summary>分组数（导出 ONNX 时写成 Conv 的 <c>group</c> 属性）。</summary>
    public int Groups { get; }

    /// <summary>是否接 SiLU 激活（<c>act=False</c> 的层导出时跳过 Sigmoid/Mul）。</summary>
    public bool HasActivation { get; }

    /// <summary>卷积核尺寸。</summary>
    public int KernelSize { get; }

    /// <summary>步长。</summary>
    public int Stride { get; }

    /// <summary>padding（= <c>KernelSize / 2</c>，与 ultralytics 一致）。</summary>
    public int Padding => KernelSize / 2;

    /// <summary>卷积层（导出 ONNX 时读取权重）。</summary>
    public Conv2d Convolution => (Conv2d)conv;

    /// <summary>批归一化层（导出 ONNX 时折叠进卷积）。</summary>
    public BatchNorm2d Normalization => (BatchNorm2d)bn;

    /// <inheritdoc />
    public override Tensor forward(Tensor input) => act.forward(bn.forward(conv.forward(input)));
}
