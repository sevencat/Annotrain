using System.Globalization;
using TorchSharp;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.NN;

/// <summary>
/// MLP 结构工厂：<c>Linear + SiLU</c> 堆叠，最后一层不带激活。
/// <para>
/// 参数命名固定为 <c>fc0.weight</c> / <c>fc0.bias</c> / <c>fc1.weight</c> …，
/// 与 PyTorch <c>state_dict()</c> 以及本项目导出的 safetensors 键名一致。
/// </para>
/// <para>
/// P0 用它跑通「训练 → safetensors → ONNX」；YOLO / RF-DETR 会在
/// <c>Sevencat.Train.Engine.Models</c>（P1 新建）里用 TorchSharp 原生层搭建。
/// </para>
/// </summary>
public static class MlpModel
{
    /// <summary>按层尺寸创建 MLP（例如 <c>[2, 32, 32, 1]</c>）。</summary>
    public static Module<Tensor, Tensor> Create(Device device, IReadOnlyList<int> layerSizes)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(layerSizes);
        if (layerSizes.Count < 2)
            throw new ArgumentException("MLP 至少需要输入层与输出层两个尺寸。", nameof(layerSizes));

        var modules = new List<(string Name, Module<Tensor, Tensor> Module)>(layerSizes.Count * 2);

        for (int index = 0; index < layerSizes.Count - 1; index++)
        {
            if (layerSizes[index] <= 0 || layerSizes[index + 1] <= 0)
                throw new ArgumentOutOfRangeException(nameof(layerSizes), "每一层尺寸必须大于 0。");

            modules.Add(($"fc{index}", nn.Linear(
                layerSizes[index],
                layerSizes[index + 1],
                true,
                device,
                ScalarType.Float32)));

            if (index < layerSizes.Count - 2)
                modules.Add(($"act{index}", nn.SiLU()));
        }

        return nn.Sequential(modules.ToArray());
    }

    /// <summary>结构标识，写进检查点元数据（如 <c>mlp-2-32-32-1</c>）。</summary>
    public static string Format(IReadOnlyList<int> layerSizes)
    {
        ArgumentNullException.ThrowIfNull(layerSizes);
        return "mlp-" + string.Join("-", layerSizes);
    }

    /// <summary>解析 <see cref="Format"/> 的输出。</summary>
    public static int[] Parse(string architecture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(architecture);

        const string prefix = "mlp-";
        if (!architecture.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"目前只支持 {prefix}<sizes> 结构，实际是 '{architecture}'。");
        }

        string[] parts = architecture[prefix.Length..].Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new FormatException($"architecture 至少需要两层：'{architecture}'。");

        var sizes = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out sizes[i])
                || sizes[i] <= 0)
            {
                throw new FormatException($"architecture 第 {i} 段不是正整数：'{architecture}'。");
            }
        }

        return sizes;
    }

    /// <summary>参数总量（用于日志）。</summary>
    public static long CountParameters(Module<Tensor, Tensor> module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return module.parameters().Sum(parameter => parameter.numel());
    }
}
