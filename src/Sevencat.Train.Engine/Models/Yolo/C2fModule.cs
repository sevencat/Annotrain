using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.Yolo;

/// <summary>
/// ultralytics 的 <c>C2f</c>：省掉一个卷积的 CSP 瓶颈堆叠（YOLOv8 / v11 的主干与颈部基础块）。
/// <para>
/// 形状流：<c>cv1</c> 把输入卷积成 <c>2c</c> 后沿通道切成两半，
/// 后一半连续经过 <c>m</c> 里的 Bottleneck，再把所有中间结果拼起来用 <c>cv2</c> 融合。
/// 子模块名 <c>cv1</c> / <c>cv2</c> / <c>m</c> 与 ultralytics 一致，
/// 因此键名是 <c>model.2.cv1.conv.weight</c>、<c>model.2.m.0.cv1.conv.weight</c>。
/// </para>
/// </summary>
public sealed class C2fModule : Module<Tensor, Tensor>
{
    private readonly Module<Tensor, Tensor> cv1;
    private readonly Module<Tensor, Tensor> cv2;
    private readonly ModuleList<Module<Tensor, Tensor>> m;
    private readonly BottleneckModule[] _blocks;

    /// <summary>创建 C2f：<paramref name="repeats"/> 已按规模 depth 缩放。</summary>
    public C2fModule(
        int inputChannels,
        int outputChannels,
        int repeats,
        bool shortcut = false,
        double expansion = 0.5,
        Device? device = null)
        : base("C2f")
    {
        if (inputChannels <= 0 || outputChannels <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputChannels), inputChannels, "通道数必须大于 0。");
        if (repeats < 1)
            throw new ArgumentOutOfRangeException(nameof(repeats), repeats, "C2f 至少需要 1 个 Bottleneck。");
        if (expansion is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(expansion), expansion, "expansion 必须在 (0,1]。");

        int hiddenChannels = (int)(outputChannels * expansion);
        HiddenChannels = hiddenChannels;

        cv1 = new ConvModule(inputChannels, 2 * hiddenChannels, 1, 1, device);
        cv2 = new ConvModule((2 + repeats) * hiddenChannels, outputChannels, 1, 1, device);

        m = new ModuleList<Module<Tensor, Tensor>>();
        _blocks = new BottleneckModule[repeats];
        for (int index = 0; index < repeats; index++)
        {
            _blocks[index] = new BottleneckModule(hiddenChannels, hiddenChannels, shortcut, 1.0, device);
            m.Add(_blocks[index]);
        }

        register_module("cv1", cv1);
        register_module("cv2", cv2);
        register_module("m", m);
    }

    /// <summary>隐藏通道数（<c>c = c2 * expansion</c>，切分点就是它）。</summary>
    public int HiddenChannels { get; }

    /// <summary>输入卷积（导出 ONNX 时按层展开）。</summary>
    public ConvModule Cv1 => (ConvModule)cv1;

    /// <summary>输出卷积（导出 ONNX 时按层展开）。</summary>
    public ConvModule Cv2 => (ConvModule)cv2;

    /// <summary>Bottleneck 序列（导出 ONNX 时逐块展开）。</summary>
    public IReadOnlyList<BottleneckModule> Blocks => _blocks;

    /// <inheritdoc />
    public override Tensor forward(Tensor input)
    {
        Tensor[] heads = cv1.forward(input).chunk(2, 1);
        var outputs = new List<Tensor>(heads.Length + m.Count) { heads[0], heads[1] };

        for (int index = 0; index < m.Count; index++)
            outputs.Add(m[index].forward(outputs[^1]));

        return cv2.forward(torch.cat(outputs.ToArray(), 1));
    }
}
