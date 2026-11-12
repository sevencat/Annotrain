using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.RfDetr;

/// <summary>
/// RF-DETR 的 C2f 融合投影器，键名严格对齐官方 <c>backbone.0.projector.*</c>：
/// <code>
/// stages.0.0.cv1.conv (1536→256,1×1) + cv1.bn
/// stages.0.0.cv2.conv ( 640→256,1×1) + cv2.bn
/// stages.0.0.m.{0,1,2}.cv1/cv2 (128→128,3×3) + bn
/// stages.0.1 = 通道 LayerNorm(256)
/// </code>
/// 输入是主干 4 个深度层拼接后的特征图（4×384 = 1536 通道），输出**单张** 256 维特征图
/// —— 这正是官方 <c>num_feature_levels = 1</c> 的原因。
/// </summary>
public sealed class RfDetrC2fProjector : Module<Tensor, Tensor>
{
    private readonly ModuleList<Sequential> stages;

    /// <summary>按输入通道数与输出通道数创建。</summary>
    /// <param name="inChannels">主干拼接后的通道数（官方 1536）。</param>
    /// <param name="outChannels">输出通道数（官方 256）。</param>
    /// <param name="bottlenecks">Bottleneck 个数（官方 3）。</param>
    /// <param name="device">设备。</param>
    public RfDetrC2fProjector(
        int inChannels,
        int outChannels = 256,
        int bottlenecks = 3,
        Device? device = null)
        : base("Projector")
    {
        if (outChannels % 2 != 0)
            throw new ArgumentException($"输出通道 {outChannels} 必须是偶数（C2f 要对半切分）。", nameof(outChannels));

        // 用局部变量持有 C2f 主体：同名字段会被额外注册成 projector.block.*（与官方键名冲突）
        var block = new RfDetrC2fBlock(inChannels, outChannels, bottlenecks, device);

        var stage = Sequential(
            ("0", (Module<Tensor, Tensor>)block),
            ("1", (Module<Tensor, Tensor>)new RfDetrChannelNorm(outChannels, device: device)));

        stages = new ModuleList<Sequential>();
        stages.Add(stage);

        register_module("stages", stages);
    }

    /// <inheritdoc />
    public override Tensor forward(Tensor input) => stages[0].forward(input);
}

/// <summary>C2f 主体：<c>cv1 → split → 3×Bottleneck → concat → cv2</c>（键名 <c>cv1</c>/<c>cv2</c>/<c>m</c>）。</summary>
public sealed class RfDetrC2fBlock : Module<Tensor, Tensor>
{
    private readonly RfDetrConvBn cv1;
    private readonly RfDetrConvBn cv2;
    private readonly ModuleList<RfDetrBottleneck> m;

    /// <summary>创建 C2f。</summary>
    public RfDetrC2fBlock(
        int inChannels,
        int outChannels,
        int bottlenecks,
        Device? device = null)
        : base("C2f")
    {
        int hidden = outChannels / 2;

        // cv2 的输入 = 2 段 split + bottlenecks 段（官方 checkpoint 里是 640 = 2×128 + 3×128）
        int cv2In = outChannels + hidden * bottlenecks;

        cv1 = new RfDetrConvBn(inChannels, outChannels, kernel: 1, device: device);
        cv2 = new RfDetrConvBn(cv2In, outChannels, kernel: 1, device: device);

        m = new ModuleList<RfDetrBottleneck>();

        for (int index = 0; index < bottlenecks; index++)
            m.Add(new RfDetrBottleneck(hidden, device));

        register_module("cv1", cv1);
        register_module("cv2", cv2);
        register_module("m", m);
    }

    /// <inheritdoc />
    public override Tensor forward(Tensor input)
    {
        Tensor hidden = cv1.forward(input);

        Tensor[] halves = hidden.chunk(2, 1);
        var outputs = new List<Tensor>(halves.Length + m.Count) { halves[0], halves[1] };

        Tensor current = halves[1];

        for (int index = 0; index < m.Count; index++)
        {
            current = m[index].forward(current);
            outputs.Add(current);
        }

        return cv2.forward(torch.cat(outputs.ToArray(), 1));
    }
}
