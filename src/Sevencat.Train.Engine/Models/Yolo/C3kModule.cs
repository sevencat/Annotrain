using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.Yolo;

/// <summary>
/// ultralytics 的 <c>C3</c> / <c>C3k</c>：两条 1×1 卷积分支，其中一条经过 <paramref name="repeats"/> 个 Bottleneck，
/// 拼接后由 <c>cv3</c> 融合。YOLOv11 只在 <c>C3k2(c3k=True)</c> 时用它（默认 yaml 用 False）。
/// <para>子模块名 <c>cv1</c> / <c>cv2</c> / <c>cv3</c> / <c>m</c> 与 ultralytics 一致。</para>
/// </summary>
public sealed class C3kModule : Module<Tensor, Tensor>
{
    private readonly Module<Tensor, Tensor> cv1;
    private readonly Module<Tensor, Tensor> cv2;
    private readonly Module<Tensor, Tensor> cv3;
    private readonly ModuleList<Module<Tensor, Tensor>> m;
    private readonly BottleneckModule[] _blocks;

    /// <summary>创建 C3 / C3k；<paramref name="repeats"/> 已按规模 depth 缩放。</summary>
    public C3kModule(
        int inputChannels,
        int outputChannels,
        int repeats = 2,
        bool shortcut = true,
        double expansion = 0.5,
        Device? device = null)
        : base("C3k")
    {
        if (inputChannels <= 0 || outputChannels <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputChannels), inputChannels, "通道数必须大于 0。");
        if (repeats < 1)
            throw new ArgumentOutOfRangeException(nameof(repeats), repeats, "C3k 至少需要 1 个 Bottleneck。");
        if (expansion is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(expansion), expansion, "expansion 必须在 (0,1]。");

        int hiddenChannels = (int)(outputChannels * expansion);
        HiddenChannels = hiddenChannels;

        cv1 = new ConvModule(inputChannels, hiddenChannels, 1, 1, device);
        cv2 = new ConvModule(inputChannels, hiddenChannels, 1, 1, device);
        cv3 = new ConvModule(2 * hiddenChannels, outputChannels, 1, 1, device);

        m = new ModuleList<Module<Tensor, Tensor>>();
        _blocks = new BottleneckModule[repeats];
        for (int index = 0; index < repeats; index++)
        {
            // ultralytics: Bottleneck(c_, c_, shortcut, g, k=(k, k), e=1.0)
            _blocks[index] = new BottleneckModule(hiddenChannels, hiddenChannels, shortcut, 1.0, device);
            m.Add(_blocks[index]);
        }

        register_module("cv1", cv1);
        register_module("cv2", cv2);
        register_module("cv3", cv3);
        register_module("m", m);
    }

    /// <summary>隐藏通道数（<c>c2 * expansion</c>）。</summary>
    public int HiddenChannels { get; }

    /// <summary>第一条 1×1 卷积（导出时展开）。</summary>
    public ConvModule Cv1 => (ConvModule)cv1;

    /// <summary>第二条 1×1 卷积（不经过 Bottleneck，导出时展开）。</summary>
    public ConvModule Cv2 => (ConvModule)cv2;

    /// <summary>融合卷积（导出时展开）。</summary>
    public ConvModule Cv3 => (ConvModule)cv3;

    /// <summary>Bottleneck 序列（导出时逐块展开）。</summary>
    public IReadOnlyList<BottleneckModule> Blocks => _blocks;

    /// <inheritdoc />
    public override Tensor forward(Tensor input)
    {
        // ultralytics: cv3(cat((m(cv1(x)), cv2(x)), 1)) —— 只拼两支，
        // 中间 Bottleneck 的逐级输出不参与拼接（这一点与 C2f / C3k2 不同）。
        Tensor left = cv1.forward(input);
        for (int index = 0; index < m.Count; index++)
            left = m[index].forward(left);

        Tensor right = cv2.forward(input);
        return cv3.forward(torch.cat(new[] { left, right }, 1));
    }
}
