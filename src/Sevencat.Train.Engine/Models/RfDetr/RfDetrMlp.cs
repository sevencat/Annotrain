using System.Globalization;
using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.RfDetr;

/// <summary>
/// RF-DETR 的 MLP 头（官方 <c>MLP(in, hidden, out, num_layers)</c>）：
/// 线性层与 ReLU 交错，键名为 <c>layers.0</c> / <c>layers.2</c> / <c>layers.4</c>，
/// 与官方 <c>bbox_embed.layers.*</c> 完全一致。
/// </summary>
public sealed class RfDetrMlp : Module<Tensor, Tensor>
{
    private readonly Sequential layers;
    private readonly List<Linear> _linearLayers;
    private readonly int _layerCount;

    /// <summary>创建 MLP 头。</summary>
    /// <param name="inputSize">输入维度。</param>
    /// <param name="hiddenSize">隐层维度。</param>
    /// <param name="outputSize">输出维度。</param>
    /// <param name="layerCount">线性层个数（官方 3）。</param>
    /// <param name="device">设备。</param>
    public RfDetrMlp(
        int inputSize,
        int hiddenSize,
        int outputSize,
        int layerCount = 3,
        Device? device = null)
        : base("Mlp")
    {
        if (layerCount < 1)
            throw new ArgumentOutOfRangeException(nameof(layerCount), layerCount, "线性层个数必须大于 0。");

        var modules = new List<(string, Module<Tensor, Tensor>)>(layerCount);
        var linears = new List<Linear>(layerCount);
        int current = inputSize;

        for (int index = 0; index < layerCount; index++)
        {
            int next = index == layerCount - 1 ? outputSize : hiddenSize;

            Linear linear = Linear(current, next, device: device, dtype: ScalarType.Float32);
            linears.Add(linear);

            modules.Add((index.ToString(CultureInfo.InvariantCulture), linear));

            current = next;
        }

        _layerCount = layerCount;
        _linearLayers = linears;
        layers = Sequential(modules.ToArray());

        register_module("layers", layers);
    }

    /// <inheritdoc />
    public override Tensor forward(Tensor input)
    {
        // 官方 MLP 用 F.relu 逐个串联（relu 不是独立子模块），因此 layers 里只有 Linear，索引 0..n-1
        Tensor hidden = input;

        for (int index = 0; index < _linearLayers.Count; index++)
        {
            hidden = _linearLayers[index].forward(hidden);

            if (index < _linearLayers.Count - 1)
                hidden = hidden.relu();
        }

        return hidden;
    }
}
