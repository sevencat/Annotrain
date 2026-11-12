using Sevencat.Train.Engine.Core;
using Sevencat.Train.Engine.Training;

namespace Sevencat.Train.Engine.Sessions;

/// <summary>界面侧的 RF-DETR / DETR 训练参数。</summary>
public sealed record DetrTrainingOptions
{
    /// <summary>YOLO 的 data.yaml；用 COCO 时留空。</summary>
    public string DataYaml { get; init; } = string.Empty;

    /// <summary>COCO 标注 json（RF-DETR / Roboflow 或 annotations/*.json）；设置后优先于 <see cref="DataYaml"/>。</summary>
    public string? CocoJson { get; init; }

    /// <summary>数据描述文件（日志与 train.json 里展示的那个）。</summary>
    public string Descriptor => string.IsNullOrWhiteSpace(CocoJson) ? DataYaml : CocoJson;

    /// <summary>训练轮数。</summary>
    public int Epochs { get; init; } = 10;

    /// <summary>批大小（DETR 类模型显存占用高，默认小一些）。</summary>
    public int BatchSize { get; init; } = 2;

    /// <summary>
    /// 输入分辨率；必须同时满足两个约束：RF-DETR 要 112 的倍数（patch 14 × 下采样 16）、
    /// 预处理管线要 32 的倍数（P3/P4/P5 下采样），公倍数是 <b>224</b>。
    /// </summary>
    public int ImageSize { get; init; } = 224;

    /// <summary>初始学习率。</summary>
    public float LearningRate { get; init; } = 1e-3f;

    /// <summary>query 数量。</summary>
    public int Queries { get; init; } = 20;

    /// <summary>编码器层数。</summary>
    public int EncoderLayers { get; init; } = 2;

    /// <summary>解码器层数。</summary>
    public int DecoderLayers { get; init; } = 2;

    /// <summary>Group-DETR 分组数（1 = 关闭）。</summary>
    public int GroupDetr { get; init; } = 1;

    /// <summary>是否使用 RF-DETR（官方结构）；false 用简化 DETR。</summary>
    public bool UseRfDetr { get; init; } = true;

    /// <summary>DINOv2 主干权重（可选；不给就随机初始化）。</summary>
    public string? DinoWeights { get; init; }

    /// <summary>DINOv2 嵌入维度。</summary>
    public int DinoEmbedding { get; init; } = 384;

    /// <summary>DINOv2 深度。</summary>
    public int DinoDepth { get; init; } = 12;

    /// <summary>DINOv2 注意力头数。</summary>
    public int DinoHeads { get; init; } = 6;

    /// <summary>检测头的注意力头数。</summary>
    public int ModelHeads { get; init; } = 8;

    /// <summary>计算设备。</summary>
    public ComputeDevice Device { get; init; } = ComputeDevice.Cpu;

    /// <summary>输出目录。</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>每几轮评估一次（0 = 不评估）。</summary>
    public int EvalEvery { get; init; } = 1;

    /// <summary>评估置信度阈值（DETR 用低阈值配合一对一匹配）。</summary>
    public float Confidence { get; init; } = 0.05f;

    /// <summary>最多评估多少张（0 = 全部）。</summary>
    public int EvaluationLimit { get; init; }
}

/// <summary>
/// RF-DETR / DETR 训练会话：与 <see cref="YoloTrainingSession"/> 并列，接口形状保持一致，
/// 这样界面上的开始 / 暂停 / 继续 / 停止可以按所选模型族直接分发。
/// </summary>
public sealed partial class DetrTrainingSession : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private TrainControl? _control;
    private Task<IReadOnlyList<EpochMetrics>>? _task;

    /// <summary>是否正在训练。</summary>
    public bool IsRunning => _task is { IsCompleted: false };

    /// <summary>暂停 / 继续控制。</summary>
    public TrainControl? Control => _control;

    /// <summary>启动训练（立即返回，训练在后台线程执行）。</summary>
    public Task<IReadOnlyList<EpochMetrics>> StartAsync(
        DetrTrainingOptions options,
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

    /// <summary>请求暂停。</summary>
    public void Pause() => _control?.Pause();

    /// <summary>继续训练。</summary>
    public void Resume() => _control?.Resume();

    /// <summary>请求停止。</summary>
    public void Stop() => _cancellation.Cancel();

    /// <inheritdoc />
    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
        _control?.Dispose();
    }
}
