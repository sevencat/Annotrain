using Sevencat.Train.Engine.Core;
using Sevencat.Train.Engine.IO.Onnx;
using TorchSharp;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Export;

/// <summary>
/// 把 MLP 的 <c>state_dict</c> 导出成 ONNX 推理图。
/// <para>
/// TorchSharp 没有 <c>torch.onnx.export</c>，所以这里按结构手工拼图：
/// <c>MatMul + Add</c> 链，激活按层类型展开（SiLU = Sigmoid + Mul）。
/// PyTorch 的 Linear 权重是 <c>[out, in]</c>，导出时转置成 <c>[in, out]</c> 直接作 MatMul 右操作数。
/// </para>
/// </summary>
public static class MlpOnnxExporter
{
    /// <summary>按层尺寸与 state_dict 构图。</summary>
    public static OnnxModel Build(
        IReadOnlyList<int> layerSizes,
        IReadOnlyDictionary<string, Tensor> state,
        string inputName = "input",
        string outputName = "output")
    {
        ArgumentNullException.ThrowIfNull(layerSizes);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputName);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputName);

        if (layerSizes.Count < 2)
            throw new ArgumentException("MLP 至少需要输入层与输出层。", nameof(layerSizes));

        var builder = new OnnxGraphBuilder("omnits-" + MlpArchitecture(layerSizes));
        builder.Input(inputName, OnnxDataType.Float, "batch", layerSizes[0]);

        string current = inputName;
        int activationIndex = 0;

        for (int index = 0; index < layerSizes.Count - 1; index++)
        {
            current = AppendLinear(builder, state, index, layerSizes[index], layerSizes[index + 1], current);

            if (index < layerSizes.Count - 2)
            {
                current = AppendSiLU(builder, current, $"act{activationIndex}");
                activationIndex++;
            }
        }

        if (!string.Equals(current, outputName, StringComparison.Ordinal))
        {
            // 图输出名固定为 output（见 docs/formats.md），中间张量用 Identity 转名。
            builder.Node("Identity", new[] { current }, new[] { outputName });
        }

        builder.Output(outputName, OnnxDataType.Float, "batch", layerSizes[^1]);
        return new OnnxModel(builder.Build());
    }

    /// <summary>从模型直接导出（取 <c>state_dict()</c>）。</summary>
    public static OnnxModel Build(
        Module<Tensor, Tensor> model,
        IReadOnlyList<int> layerSizes,
        string inputName = "input",
        string outputName = "output")
    {
        ArgumentNullException.ThrowIfNull(model);
        return Build(layerSizes, model.state_dict(), inputName, outputName);
    }

    /// <summary>构建并保存 ONNX 文件。</summary>
    public static void Save(
        Module<Tensor, Tensor> model,
        IReadOnlyList<int> layerSizes,
        string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Build(model, layerSizes).Save(path);
    }

    private static string AppendLinear(
        OnnxGraphBuilder builder,
        IReadOnlyDictionary<string, Tensor> state,
        int index,
        int inFeatures,
        int outFeatures,
        string input)
    {
        string prefix = $"fc{index}";
        if (!state.TryGetValue(prefix + ".weight", out Tensor? weight))
            throw new InvalidDataException($"state_dict 缺少 '{prefix}.weight'。");

        // [out, in] -> [in, out]
        float[] transposed = TorchInterop.ToArray(weight.transpose(0, 1).contiguous());
        builder.Initializer(OnnxTensor.FromFloat32(
            prefix + ".weight",
            new long[] { inFeatures, outFeatures },
            transposed));

        string product = prefix + ".matmul";
        builder.Node("MatMul", new[] { input, prefix + ".weight" }, new[] { product });

        if (!state.TryGetValue(prefix + ".bias", out Tensor? bias))
            return product;

        builder.Initializer(OnnxTensor.FromFloat32(
            prefix + ".bias",
            new long[] { outFeatures },
            TorchInterop.ToArray(bias)));

        string sum = prefix + ".out";
        builder.Node("Add", new[] { product, prefix + ".bias" }, new[] { sum });
        return sum;
    }

    private static string AppendSiLU(OnnxGraphBuilder builder, string input, string prefix)
    {
        string sigmoid = prefix + ".sigmoid";
        builder.Node("Sigmoid", new[] { input }, new[] { sigmoid });

        string output = prefix + ".out";
        builder.Node("Mul", new[] { input, sigmoid }, new[] { output });
        return output;
    }

    private static string MlpArchitecture(IReadOnlyList<int> layerSizes) =>
        "mlp-" + string.Join("-", layerSizes);
}
