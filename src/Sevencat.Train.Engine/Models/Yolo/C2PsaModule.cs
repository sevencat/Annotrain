using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.Yolo;

/// <summary>
/// ultralytics 的 <c>C2PSA</c>（YOLOv11 主干最后一个块）：cv1 把通道拆成 <c>2c</c>，
/// 后一半过 <c>n</c> 个 <see cref="PsaBlockModule"/>（位置敏感注意力 + FFN），
/// 再与前一半拼接后由 cv2 融合。
/// <para>子模块名 <c>cv1</c> / <c>cv2</c> / <c>m</c> 与 ultralytics 一致。</para>
/// </summary>
public sealed class C2PsaModule : Module<Tensor, Tensor>
{
    private readonly Module<Tensor, Tensor> cv1;
    private readonly Module<Tensor, Tensor> cv2;
    private readonly ModuleList<Module<Tensor, Tensor>> m;
    private readonly PsaBlockModule[] _blocks;

    /// <summary>创建 C2PSA；输入输出通道必须相同（ultralytics 的 assert c1 == c2）。</summary>
    public C2PsaModule(
        int inputChannels,
        int outputChannels,
        int repeats,
        double expansion = 0.5,
        Device? device = null)
        : base("C2PSA")
    {
        if (inputChannels <= 0 || outputChannels <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputChannels), inputChannels, "通道数必须大于 0。");
        if (inputChannels != outputChannels)
        {
            throw new ArgumentException(
                $"C2PSA 要求输入输出通道相同，实际 {inputChannels} → {outputChannels}。", nameof(outputChannels));
        }
        if (repeats < 1)
            throw new ArgumentOutOfRangeException(nameof(repeats), repeats, "C2PSA 至少需要 1 个 PSABlock。");
        if (expansion is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(expansion), expansion, "expansion 必须在 (0,1]。");

        int hiddenChannels = (int)(inputChannels * expansion);
        HiddenChannels = hiddenChannels;

        cv1 = new ConvModule(inputChannels, 2 * hiddenChannels, 1, 1, device);
        cv2 = new ConvModule(2 * hiddenChannels, outputChannels, 1, 1, device);

        m = new ModuleList<Module<Tensor, Tensor>>();
        _blocks = new PsaBlockModule[repeats];

        for (int index = 0; index < repeats; index++)
        {
            _blocks[index] = new PsaBlockModule(hiddenChannels, shortcut: true, device);
            m.Add(_blocks[index]);
        }

        register_module("cv1", cv1);
        register_module("cv2", cv2);
        register_module("m", m);
    }

    /// <summary>隐藏通道数（<c>c1 * expansion</c>）。</summary>
    public int HiddenChannels { get; }

    /// <summary>输入卷积（导出时展开）。</summary>
    public ConvModule Cv1 => (ConvModule)cv1;

    /// <summary>输出卷积（导出时展开）。</summary>
    public ConvModule Cv2 => (ConvModule)cv2;

    /// <summary>PSABlock 序列（导出时逐块展开）。</summary>
    public IReadOnlyList<PsaBlockModule> Blocks => _blocks;

    /// <inheritdoc />
    public override Tensor forward(Tensor input)
    {
        Tensor[] halves = cv1.forward(input).chunk(2, 1);
        Tensor current = halves[1];

        for (int index = 0; index < m.Count; index++)
            current = m[index].forward(current);

        return cv2.forward(torch.cat(new[] { halves[0], current }, 1));
    }
}
