using TorchSharp;
using static TorchSharp.torch;

namespace Sevencat.Train.Engine.Core;

/// <summary>
/// 托管数组与 TorchSharp 张量之间的边界操作。
/// <para>
/// 只在「进入/离开」训练时使用（数据装载、检查点、ONNX 导出）；
/// 训练循环内部不要逐元素读回，否则每次都会同步设备、丢失 GPU 性能。
/// </para>
/// </summary>
public static class TorchInterop
{
    /// <summary>用托管数组创建张量（默认 F32）。</summary>
    public static Tensor FromArray(
        float[] values,
        long[] dims,
        Device device,
        bool requiresGrad = false)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(dims);
        ArgumentNullException.ThrowIfNull(device);

        long expected = 1;
        foreach (long dim in dims)
            expected *= dim;

        if (expected != values.Length)
        {
            throw new ArgumentException(
                $"数据长度 {values.Length} 与形状 [{string.Join(",", dims)}]（{expected} 个元素）不一致。",
                nameof(values));
        }

        Tensor tensor = torch.tensor(values, dims, device: device);
        return requiresGrad ? tensor.requires_grad_(true) : tensor;
    }

    /// <summary>读回连续 F32 数组（会从设备同步到 CPU）。</summary>
    public static float[] ToArray(Tensor tensor)
    {
        ArgumentNullException.ThrowIfNull(tensor);
        return tensor.detach().cpu().contiguous().data<float>().ToArray();
    }

    /// <summary>读取标量。</summary>
    public static float ToScalar(Tensor tensor)
    {
        ArgumentNullException.ThrowIfNull(tensor);
        return tensor.detach().cpu().item<float>();
    }

    /// <summary>设置全局随机种子（参数初始化与采样）。</summary>
    public static void SetSeed(long seed) => torch.manual_seed(seed);
}
