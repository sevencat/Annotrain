using Sevencat.Core.Simulation;

namespace Sevencat.Train.Engine.Training;

/// <summary>
/// 训练运行控制：暂停 / 继续 / 取消。
/// <para>
/// 语义是「原地停住」而不是「停下来存盘再重启」：暂停时训练线程阻塞在批次边界，
/// 模型、优化器、EMA、随机数状态全部原样保留，继续后从下一个批次接着跑。
/// </para>
/// <para>
/// 只在批次边界检查，绝不打断已经开始的 forward/backward —— 否则会留下不一致的半程梯度。
/// </para>
/// </summary>
public sealed class TrainControl : IDisposable, ITrainingControl
{
    private readonly ManualResetEventSlim _resume = new(initialState: true);
    private bool _disposed;

    /// <summary>创建控制器。</summary>
    /// <param name="cancellationToken">取消令牌；取消后 <see cref="WaitUntilReady"/> 返回 false，训练循环退出。</param>
    public TrainControl(CancellationToken cancellationToken = default)
    {
        Token = cancellationToken;
    }

    /// <summary>取消令牌。</summary>
    public CancellationToken Token { get; }

    /// <summary>是否处于暂停状态。</summary>
    public bool IsPaused => !_resume.IsSet;

    /// <summary>是否已请求取消。</summary>
    public bool IsCancellationRequested => Token.IsCancellationRequested;

    /// <summary>暂停状态变化时触发（true = 已暂停，false = 已继续）。参数为当前 <see cref="IsPaused"/>。</summary>
    public event Action<bool>? PausedChanged;

    /// <summary>请求暂停：训练线程会在下一个批次边界停住。</summary>
    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_resume.IsSet)
            return;

        _resume.Reset();
        PausedChanged?.Invoke(true);
    }

    /// <summary>继续训练：释放被暂停的线程。</summary>
    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_resume.IsSet)
            return;

        _resume.Set();
        PausedChanged?.Invoke(false);
    }

    /// <summary>
    /// 训练循环在每个批次开始前调用：暂停期间阻塞（不占 CPU），
    /// 返回 false 表示已取消、调用方应结束训练。
    /// </summary>
    public bool WaitUntilReady()
    {
        if (Token.IsCancellationRequested)
            return false;

        while (!_resume.IsSet)
        {
            // 等待要有超时，否则暂停状态下取消永远唤不醒线程。
            if (Token.WaitHandle.WaitOne(100))
                return false;
        }

        return !Token.IsCancellationRequested;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _resume.Dispose();
    }
}
