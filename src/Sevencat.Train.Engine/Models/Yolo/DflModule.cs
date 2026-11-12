using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.Yolo;

/// <summary>
/// ultralytics 的 <c>DFL</c>（Distribution Focal Loss 的积分层）：
/// 把每个方向上的 16 个离散概率与 <c>0..15</c> 权重做加权和，得到连续的距离值。
/// <para>
/// 权重固定为 <c>arange(reg_max)</c>（形状 <c>[1, reg_max, 1, 1]</c>、不参与训练），
/// 用 1×1 卷积实现，和 ultralytics 一样保留 <c>dfl.conv.weight</c> 这个键，
/// 便于与官方权重互相加载。
/// </para>
/// </summary>
public sealed class DflModule : Module<Tensor, Tensor>
{
    private readonly Conv2d conv;
    private readonly int _regressionMax;

    /// <summary>创建 DFL 层。</summary>
    public DflModule(int regressionMax = 16, Device? device = null)
        : base("DFL")
    {
        if (regressionMax <= 0)
            throw new ArgumentOutOfRangeException(nameof(regressionMax), regressionMax, "reg_max 必须大于 0。");

        _regressionMax = regressionMax;
        conv = nn.Conv2d(
            regressionMax,
            1,
            1,
            1,
            0,
            bias: false,
            device: device,
            dtype: ScalarType.Float32);

        using (torch.no_grad())
        {
            Tensor weights = torch.arange(
                regressionMax,
                dtype: ScalarType.Float32,
                device: device).view(1, regressionMax, 1, 1);
            conv.weight.copy_(weights);
            conv.weight.requires_grad_(false);
        }

        register_module("conv", conv);
    }

    /// <summary>reg_max（每个方向上的离散区间数）。</summary>
    public int RegressionMax => _regressionMax;

    /// <summary>输入 <c>[B, 4*reg_max, N]</c>，输出 <c>[B, 4, N]</c> 的连续距离。</summary>
    public override Tensor forward(Tensor input)
    {
        ArgumentNullException.ThrowIfNull(input);

        long batch = input.shape[0];
        long anchors = input.shape[2];
        long expected = 4L * _regressionMax;
        if (input.dim() != 3 || input.shape[1] != expected)
        {
            throw new ArgumentException(
                $"DFL 输入形状必须为 [B,{expected},N]，实际是 [{string.Join(",", input.shape)}]。",
                nameof(input));
        }

        Tensor softmax = input
            .view(batch, 4, _regressionMax, anchors)
            .transpose(1, 2)
            .softmax(1);

        return conv.forward(softmax).view(batch, 4, anchors);
    }
}
