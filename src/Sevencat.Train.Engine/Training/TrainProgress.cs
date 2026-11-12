namespace Sevencat.Train.Engine.Training;

/// <summary>
/// 训练进度快照：每个 epoch 结束时上报一次，供 UI 画曲线 / 显示状态。
/// <para>
/// 只带 UI 需要的标量，不携带任何张量 —— 界面线程永远不会碰到 LibTorch 的对象。
/// </para>
/// </summary>
/// <param name="Epoch">已完成的 epoch 编号（从 <see cref="TrainerOptions.StartEpoch"/> 起）。</param>
/// <param name="TotalEpochs">目标总轮数（用于进度条）；动态加轮后会变大。</param>
/// <param name="Step">截至当前累计的优化步数。</param>
/// <param name="Loss">该 epoch 的平均损失。</param>
/// <param name="LearningRate">该 epoch 使用的学习率。</param>
/// <param name="Map50">该 epoch 的 mAP50（未评估时为 0）。</param>
/// <param name="Map50_95">该 epoch 的 mAP50-95（未评估时为 0）。</param>
public sealed record TrainProgress(
    int Epoch,
    int TotalEpochs,
    int Step,
    float Loss,
    float LearningRate,
    float Map50 = 0f,
    float Map50_95 = 0f)
{
    /// <summary>完成比例（0~1，用于进度条）。</summary>
    public double Fraction => TotalEpochs <= 0 ? 0 : Math.Clamp((Epoch + 1) / (double)TotalEpochs, 0, 1);
}
