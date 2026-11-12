using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.Yolo;

/// <summary>
/// ultralytics 的 <c>C3k2</c>（YOLOv11 的主干 / 颈部基础块）：结构与 <see cref="C2fModule"/> 一致
/// （cv1 拆两半、后一半串 <c>m</c> 个块、全部拼接后 cv2 融合），差别在内层块：
/// <list type="bullet">
/// <item><c>c3k=false</c>（yolo11.yaml 的默认）→ <c>Bottleneck(c, c, shortcut, g)</c>，隐藏通道再折半；</item>
/// <item><c>c3k=true</c> → <see cref="C3kModule"/>（内含 2 个 Bottleneck）。</item>
/// </list>
/// </summary>
public sealed class C3k2Module : Module<Tensor, Tensor>
{
    private readonly Module<Tensor, Tensor> cv1;
    private readonly Module<Tensor, Tensor> cv2;
    private readonly ModuleList<Module<Tensor, Tensor>> m;
    private readonly Module<Tensor, Tensor>[] _blocks;

    /// <summary>创建 C3k2；<paramref name="repeats"/> 已按规模 depth 缩放。</summary>
    public C3k2Module(
        int inputChannels,
        int outputChannels,
        int repeats,
        bool c3k = false,
        double expansion = 0.5,
        bool shortcut = true,
        Device? device = null)
        : base("C3k2")
    {
        if (inputChannels <= 0 || outputChannels <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputChannels), inputChannels, "通道数必须大于 0。");
        if (repeats < 1)
            throw new ArgumentOutOfRangeException(nameof(repeats), repeats, "C3k2 至少需要 1 个块。");
        if (expansion is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(expansion), expansion, "expansion 必须在 (0,1]。");

        int hiddenChannels = (int)(outputChannels * expansion);
        HiddenChannels = hiddenChannels;
        UsesC3kBlocks = c3k;

        cv1 = new ConvModule(inputChannels, 2 * hiddenChannels, 1, 1, device);
        cv2 = new ConvModule((2 + repeats) * hiddenChannels, outputChannels, 1, 1, device);

        m = new ModuleList<Module<Tensor, Tensor>>();
        _blocks = new Module<Tensor, Tensor>[repeats];

        for (int index = 0; index < repeats; index++)
        {
            _blocks[index] = c3k
                ? new C3kModule(hiddenChannels, hiddenChannels, 2, shortcut, 0.5, device)
                : new BottleneckModule(hiddenChannels, hiddenChannels, shortcut, 0.5, device);

            m.Add(_blocks[index]);
        }

        register_module("cv1", cv1);
        register_module("cv2", cv2);
        register_module("m", m);
    }

    /// <summary>隐藏通道数（<c>c2 * expansion</c>，切分点就是它）。</summary>
    public int HiddenChannels { get; }

    /// <summary>内层块是否为 <see cref="C3kModule"/>（否则是单个 Bottleneck）。</summary>
    public bool UsesC3kBlocks { get; }

    /// <summary>输入卷积（导出 ONNX 时按层展开）。</summary>
    public ConvModule Cv1 => (ConvModule)cv1;

    /// <summary>输出卷积（导出 ONNX 时按层展开）。</summary>
    public ConvModule Cv2 => (ConvModule)cv2;

    /// <summary>内层块序列（导出时逐块展开）。</summary>
    public IReadOnlyList<Module<Tensor, Tensor>> Blocks => _blocks;

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
