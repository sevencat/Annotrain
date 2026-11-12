namespace Sevencat.Train.Engine.Core;

/// <summary>学习率调度：按优化步给出学习率（0 起）。</summary>
public interface ILearningRateSchedule
{
    /// <summary>第 <paramref name="step"/> 步（从 0 开始）应使用的学习率。</summary>
    float GetLearningRate(int step);
}

/// <summary>常数学习率。</summary>
public sealed class ConstantSchedule : ILearningRateSchedule
{
    public ConstantSchedule(float learningRate) => LearningRate = learningRate;

    public float LearningRate { get; }

    public float GetLearningRate(int step) => LearningRate;
}

/// <summary>线性 warmup + 余弦退火到 <see cref="MinLearningRate"/>。</summary>
public sealed class CosineSchedule : ILearningRateSchedule
{
    private readonly float _baseLearningRate;
    private readonly int _totalSteps;
    private readonly int _warmupSteps;
    private readonly float _minLearningRate;

    public CosineSchedule(
        float baseLearningRate,
        int totalSteps,
        int warmupSteps = 0,
        float minLearningRate = 0f)
    {
        if (totalSteps <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalSteps), totalSteps, "总步数必须大于 0");
        if (warmupSteps < 0 || warmupSteps >= totalSteps)
            throw new ArgumentOutOfRangeException(nameof(warmupSteps), warmupSteps, "warmup 步数必须在 [0, totalSteps) 内");

        _baseLearningRate = baseLearningRate;
        _totalSteps = totalSteps;
        _warmupSteps = warmupSteps;
        _minLearningRate = minLearningRate;
    }

    public float GetLearningRate(int step)
    {
        if (step < 0)
            throw new ArgumentOutOfRangeException(nameof(step), step, "步数不能为负");

        if (_warmupSteps > 0 && step < _warmupSteps)
            return _baseLearningRate * (step + 1) / _warmupSteps;

        int decaySteps = _totalSteps - _warmupSteps;
        if (decaySteps <= 0)
            return _minLearningRate;

        int decayStep = Math.Min(step - _warmupSteps, decaySteps);
        float progress = (float)decayStep / decaySteps;
        float cosine = 0.5f * (1f + MathF.Cos(MathF.PI * progress));
        return _minLearningRate + (_baseLearningRate - _minLearningRate) * cosine;
    }
}
