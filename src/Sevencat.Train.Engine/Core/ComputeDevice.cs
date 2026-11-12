using TorchSharp;
using static TorchSharp.torch;

namespace Sevencat.Train.Engine.Core;

/// <summary>可选计算设备。</summary>
public enum ComputeDevice
{
    /// <summary>CPU（默认，任何机器可用）。</summary>
    Cpu,

    /// <summary>CUDA（需要 libtorch-cuda 原生包 + NVIDIA 驱动）。</summary>
    Cuda,
}

/// <summary>
/// TorchSharp 设备选择与可用性检查。
/// <para>
/// TorchSharp / LibTorch 只有 CPU 与 CUDA 两种设备；本项目<b>不提供 Vulkan</b>。
/// CUDA 不可用时直接抛错并给出安装指引，不做静默回退。
/// </para>
/// </summary>
public static class DeviceFactory
{
    /// <summary>解析命令行 / 配置里的设备名（cpu / cuda）。</summary>
    public static ComputeDevice Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return ComputeDevice.Cpu;

        return value.Trim().ToLowerInvariant() switch
        {
            "cpu" => ComputeDevice.Cpu,
            "cuda" or "gpu" => ComputeDevice.Cuda,
            _ => throw new ArgumentException($"未知设备 '{value}'；可选：cpu / cuda。", nameof(value)),
        };
    }

    /// <summary>创建 TorchSharp 设备对象；CUDA 不可用时抛错。</summary>
    public static Device Create(ComputeDevice device)
    {
        if (device == ComputeDevice.Cpu)
            return torch.CPU;

        if (!torch.cuda.is_available())
        {
            throw new InvalidOperationException(
                "CUDA 不可用：当前进程加载的是 CPU 版 LibTorch，或机器没有可用的 NVIDIA 驱动。"
                + "请把 libtorch-cpu 换成对应版本的 libtorch-cuda-*-win-x64，"
                + "并确认 nvidia-smi 可用；详见 docs/backends.md。");
        }

        return torch.CUDA;
    }

    /// <summary>设备是否可用（不抛异常）。</summary>
    public static bool IsAvailable(ComputeDevice device) =>
        device == ComputeDevice.Cpu || torch.cuda.is_available();

    /// <summary>人类可读描述（写进日志与检查点元数据）。</summary>
    public static string Describe(ComputeDevice device) => device switch
    {
        ComputeDevice.Cpu => "cpu",
        ComputeDevice.Cuda => "cuda",
        _ => device.ToString().ToLowerInvariant(),
    };

    /// <summary>设备详细信息（CUDA 时带设备名）。</summary>
    public static string DescribeDetailed(ComputeDevice device)
    {
        if (device == ComputeDevice.Cpu)
            return $"cpu (torch {torch.__version__})";

        if (!torch.cuda.is_available())
            return "cuda (unavailable)";

        return $"cuda:0 (torch {torch.__version__})";
    }
}
