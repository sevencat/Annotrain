using Sevencat.Train.Engine.Core;
using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Training;

/// <summary>训练超参数。</summary>
public sealed record TrainerOptions
{
    /// <summary>训练轮数。</summary>
    public int Epochs { get; init; } = 100;

    /// <summary>梯度全局范数裁剪阈值（&lt;= 0 表示不裁剪）。</summary>
    public float GradientClipNorm { get; init; } = 5f;

    /// <summary>每个 epoch 是否打乱批次顺序。</summary>
    public bool Shuffle { get; init; } = true;

    /// <summary>每多少个 epoch 回调一次（0 表示每个 epoch 都回调）。</summary>
    public int LogEvery { get; init; }

    /// <summary>起始 epoch（断点续训用；epoch 编号会从该值开始递增）。</summary>
    public int StartEpoch { get; init; }

    /// <summary>起始优化步（断点续训用；影响学习率调度的绝对位置）。</summary>
    public int StartStep { get; init; }
}

/// <summary>单个 epoch 结束时的指标。</summary>
public sealed record EpochMetrics(int Epoch, int Step, float Loss, float LearningRate);

/// <summary>
/// 训练循环：前向 → 损失 → 反向 → 梯度裁剪 → 优化器更新。
/// <para>
/// 自动微分、优化器、梯度全部由 TorchSharp / LibTorch 负责；
/// 本项目只编排训练流程、日志、学习率调度与检查点。
/// </para>
/// </summary>
public sealed class Trainer
{
    private readonly Module<Tensor, Tensor> _model;
    private readonly OptimizerHelper _optimizer;
    private readonly ILoss _loss;

    /// <summary>
    /// 统一损失入口：若模型实现了 <see cref="ILossExtrasProvider"/>，把附加张量一并传给损失
    /// （损失的 extras 重载默认会忽略它们，只有需要深监督的损失才真正使用）。
    /// </summary>
    private Tensor ComputeLoss(Tensor prediction, Tensor target)
    {
        IReadOnlyDictionary<string, Tensor>? extras =
            (_model as ILossExtrasProvider)?.GetLossExtras();

        return extras is null
            ? _loss.Compute(prediction, target)
            : _loss.Compute(prediction, target, extras);
    }
    private readonly TrainerOptions _options;
    private readonly Rng _rng;
    private readonly Ema? _ema;

    public Trainer(
        Module<Tensor, Tensor> model,
        OptimizerHelper optimizer,
        ILoss loss,
        TrainerOptions? options = null,
        Rng? rng = null,
        Ema? ema = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(optimizer);
        ArgumentNullException.ThrowIfNull(loss);

        _model = model;
        _optimizer = optimizer;
        _loss = loss;
        _options = options ?? new TrainerOptions();
        _rng = rng ?? new Rng(1);
        _ema = ema;
        Step = _options.StartStep;

        if (_options.Epochs <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), _options.Epochs, "Epochs 必须大于 0");
    }

    /// <summary>已经执行的优化步数。</summary>
    public int Step { get; private set; }

    /// <summary>最近一个 epoch 的平均损失。</summary>
    public float LastLoss { get; private set; } = float.NaN;

    /// <summary>
    /// 训练多个 epoch，返回每个 epoch 的指标。
    /// <para>批次集合固定；<see cref="TrainerOptions.Shuffle"/> 决定每个 epoch 是否重排。</para>
    /// </summary>
    public IReadOnlyList<EpochMetrics> Fit(
        IReadOnlyList<Batch> batches,
        ILearningRateSchedule? schedule = null,
        Action<EpochMetrics>? onEpoch = null,
        TrainControl? control = null,
        IProgress<TrainProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(batches);
        if (batches.Count == 0)
            throw new ArgumentException("训练批次为空。", nameof(batches));

        int[] order = Enumerable.Range(0, batches.Count).ToArray();

        return Fit(
            _ =>
            {
                if (_options.Shuffle)
                    Shuffle(order);

                return order.Select(index => batches[index]);
            },
            schedule,
            onEpoch,
            control,
            progress);
    }

    /// <summary>
    /// 训练多个 epoch，批次由委托按 epoch 惰性提供：
    /// 用于 mosaic / 多尺度训练这种「每个 epoch 重新构建批次」的场景，避免把整轮数据常驻内存。
    /// </summary>
    /// <param name="control">可选的暂停 / 取消控制；null 表示一路跑到底。</param>
    /// <param name="progress">可选的进度回调：每个 epoch 上报一次（不受 <see cref="TrainerOptions.LogEvery"/> 影响）。</param>
    /// <returns>已完成的 epoch 指标；被取消时返回的是取消前的部分历史。</returns>
    public IReadOnlyList<EpochMetrics> Fit(
        Func<int, IEnumerable<Batch>> batchesForEpoch,
        ILearningRateSchedule? schedule = null,
        Action<EpochMetrics>? onEpoch = null,
        TrainControl? control = null,
        IProgress<TrainProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(batchesForEpoch);

        var history = new List<EpochMetrics>(_options.Epochs);
        int lastEpoch = _options.StartEpoch + _options.Epochs;
        bool stopped = false;

        for (int epoch = _options.StartEpoch; epoch < lastEpoch && !stopped; epoch++)
        {
            _model.train();

            double epochLoss = 0;
            int batchCount = 0;

            foreach (Batch batch in batchesForEpoch(epoch))
            {
                ArgumentNullException.ThrowIfNull(batch);

                // 暂停/取消只在批次边界生效：不打断已开始的 forward/backward，
                // 参数与优化器状态始终停在一致的位置，继续时无需重新加载任何东西。
                if (control is not null && !control.WaitUntilReady())
                {
                    stopped = true;
                    break;
                }

                _optimizer.zero_grad();
                Tensor prediction = _model.forward(batch.Input);
                Tensor loss = ComputeLoss(prediction, batch.Target);
                loss.backward();

                ClipGradients();

                if (schedule is not null)
                    SetLearningRate(schedule.GetLearningRate(Step));

                _optimizer.step();
                _ema?.Update(_model);
                Step++;

                epochLoss += loss.detach().item<float>();
                batchCount++;
            }

            if (batchCount == 0)
                throw new InvalidOperationException($"第 {epoch} 个 epoch 没有产生任何批次。");

            LastLoss = (float)(epochLoss / batchCount);
            var metrics = new EpochMetrics(epoch, Step, LastLoss, GetLearningRate());
            history.Add(metrics);

            // 进度上报与日志节流解耦：UI 每轮都要刷新曲线，而控制台日志可以少打。
            progress?.Report(new TrainProgress(epoch, lastEpoch, Step, LastLoss, metrics.LearningRate));

            if (onEpoch is not null
                && (_options.LogEvery <= 0 || epoch % _options.LogEvery == 0 || epoch == lastEpoch - 1))
            {
                onEpoch(metrics);
            }
        }

        return history;
    }

    /// <summary>在给定批次上评估（不更新参数），返回平均损失。</summary>
    public float Evaluate(IReadOnlyList<Batch> batches)
    {
        ArgumentNullException.ThrowIfNull(batches);
        if (batches.Count == 0)
            throw new ArgumentException("评估批次为空。", nameof(batches));

        _model.eval();
        double total = 0;

        using (torch.no_grad())
        {
            foreach (Batch batch in batches)
            {
                Tensor prediction = _model.forward(batch.Input);
                total += ComputeLoss(prediction, batch.Target).item<float>();
            }
        }

        _model.train();
        return (float)(total / batches.Count);
    }

    private void ClipGradients()
    {
        if (_options.GradientClipNorm <= 0f)
            return;

        double sumSquares = 0;
        foreach (Parameter parameter in _model.parameters())
        {
            if (parameter.grad is null)
                continue;

            // item<T>() 要求类型与张量一致：F32 张量只能读 float。
            sumSquares += parameter.grad.pow(2).sum().item<float>();
        }

        double norm = Math.Sqrt(sumSquares);
        if (norm <= _options.GradientClipNorm || norm <= 0)
            return;

        var scale = (float)(_options.GradientClipNorm / norm);
        foreach (Parameter parameter in _model.parameters())
            parameter.grad?.mul_(scale);
    }

    private void SetLearningRate(float learningRate)
    {
        foreach (ParamGroup group in _optimizer.ParamGroups)
            group.LearningRate = learningRate;
    }

    private float GetLearningRate() => (float)_optimizer.ParamGroups.First().LearningRate;

    private void Shuffle(int[] order)
    {
        for (int i = order.Length - 1; i > 0; i--)
        {
            int j = _rng.NextInt(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
    }
}
