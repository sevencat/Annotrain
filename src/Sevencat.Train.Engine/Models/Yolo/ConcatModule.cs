using TorchSharp;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.Yolo;

/// <summary>
/// ultralytics 的 <c>Concat</c>：把多个来源张量沿通道维拼接。
/// <para>无参数；它存在的原因是颈部需要把上采样结果与更浅层的特征图融合。</para>
/// </summary>
public sealed class ConcatModule : Module<Tensor[], Tensor>
{
    private readonly int _dimension;

    /// <summary>创建拼接层（YOLOv8 一律沿通道维，<paramref name="dimension"/> = 1）。</summary>
    public ConcatModule(int dimension = 1)
        : base("Concat")
    {
        _dimension = dimension;
    }

    /// <summary>拼接维度（YOLOv8 一律为 1）。</summary>
    public int Dimension => _dimension;

    /// <inheritdoc />
    public override Tensor forward(Tensor[] input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length == 0)
            throw new ArgumentException("Concat 至少需要 1 个输入。", nameof(input));

        return torch.cat(input, _dimension);
    }
}
