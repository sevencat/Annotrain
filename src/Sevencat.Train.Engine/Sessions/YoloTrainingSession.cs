using Sevencat.Train.Engine.Core;
using Sevencat.Train.Engine.Training;

namespace Sevencat.Train.Engine.Sessions;

/// <summary>界面侧的训练参数。</summary>
public sealed record YoloTrainingOptions
{
    /// <summary>YOLO 的 data.yaml；用 COCO 时留空。</summary>
    public string DataYaml { get; init; } = string.Empty;

    /// <summary>COCO 标注 json（RF-DETR / Roboflow 或 annotations/*.json）；设置后优先于 <see cref="DataYaml"/>。</summary>
    public string? CocoJson { get; init; }

    /// <summary>数据描述文件（日志与 train.json 里展示的那个）。</summary>
    public string Descriptor => string.IsNullOrWhiteSpace(CocoJson) ? DataYaml : CocoJson;

    /// <summary>训练轮数。</summary>
    public int Epochs { get; init; } = 10;

    /// <summary>批大小。</summary>
    public int BatchSize { get; init; } = 8;

    /// <summary>输入分辨率（必须是模型 stride 的整数倍）。</summary>
    public int ImageSize { get; init; } = 64;

    /// <summary>初始学习率。</summary>
    public float LearningRate { get; init; } = 0.01f;

    /// <summary>模型规模（n / s / m / l / x）。</summary>
    public string Scale { get; init; } = "n";

    /// <summary>模型家族（<c>v8</c> / <c>v11</c>）。</summary>
    public string Family { get; init; } = "v8";

    /// <summary>计算设备。</summary>
    public ComputeDevice Device { get; init; } = ComputeDevice.Cpu;

    /// <summary>输出目录（检查点与 train.json 写这里）。</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>每几轮评估一次（0 表示不评估）。</summary>
    public int EvalEvery { get; init; } = 1;

    /// <summary>每几轮保存一次检查点（0 表示只在结束时保存）。</summary>
    public int SaveEvery { get; init; } = 5;

    /// <summary>评估时的置信度阈值。</summary>
    public float Confidence { get; init; } = 0.001f;

    /// <summary>评估时的 NMS IoU 阈值。</summary>
    public float IouThreshold { get; init; } = 0.7f;

    /// <summary>最多评估多少张验证图（0 = 全部；大验证集先用它限速）。</summary>
    public int EvaluationLimit { get; init; }
}

/// <summary>
/// 一次训练会话：把「数据集 → 批次 → 损失 → Trainer」这套编排从 CLI 搬到界面层。
/// <para>
/// 训练全程在后台线程；UI 只拿到 <see cref="TrainProgress"/> 这样的纯标量快照，
/// 通过 <see cref="TrainControl"/> 实现暂停 / 继续 / 停止。
/// </para>
/// </summary>
public sealed partial class YoloTrainingSession : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private TrainControl? _control;
    private Task<IReadOnlyList<EpochMetrics>>? _task;

    /// <summary>是否正在训练。</summary>
    public bool IsRunning => _task is { IsCompleted: false };

    /// <summary>暂停 / 继续控制；未开始训练时为 null。</summary>
    public TrainControl? Control => _control;

    /// <summary>启动训练（立即返回，训练在后台线程执行）。</summary>
    /// <param name="options">训练参数。</param>
    /// <param name="progress">每轮进度回调（会自动回到创建它的线程）。</param>
    /// <param name="log">日志回调（设备、模型、每轮摘要）。</param>
    public Task<IReadOnlyList<EpochMetrics>> StartAsync(
        YoloTrainingOptions options,
        IProgress<TrainProgress>? progress = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (IsRunning)
            throw new InvalidOperationException("已有训练在进行中。");

        _control = new TrainControl(_cancellation.Token);
        TrainControl control = _control;
        _task = Task.Run(() => Run(options, control, progress, log), CancellationToken.None);
        return _task;
    }

    /// <summary>请求暂停：训练线程在下一个批次边界停住，参数原样保留。</summary>
    public void Pause() => _control?.Pause();

    /// <summary>继续训练。</summary>
    public void Resume() => _control?.Resume();

    /// <summary>请求停止：当前批次结束后退出，已完成的 epoch 保留在返回历史里。</summary>
    public void Stop() => _cancellation.Cancel();

    /// <inheritdoc />
    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
        _control?.Dispose();
    }
}
