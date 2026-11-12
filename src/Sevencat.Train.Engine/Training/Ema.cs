using TorchSharp;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Training;

/// <summary>
/// 指数滑动平均（EMA）：按 state_dict 维护一份参数影子副本，评估/导出时写回模型。
/// <para>更新公式：<c>shadow = decay * shadow + (1 - decay) * param</c>，全程在设备上原地完成。</para>
/// </summary>
public sealed class Ema
{
    private readonly Dictionary<string, Tensor> _shadow = new(StringComparer.Ordinal);

    public Ema(Module<Tensor, Tensor> model, float decay = 0.999f)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (decay is <= 0f or >= 1f)
            throw new ArgumentOutOfRangeException(nameof(decay), decay, "decay 必须在 (0,1) 内");

        Decay = decay;

        foreach ((string name, Tensor tensor) in model.state_dict())
            _shadow[name] = torch.zeros_like(tensor);
    }

    /// <summary>衰减系数（越接近 1 越平滑）。</summary>
    public float Decay { get; }

    /// <summary>影子参数（只读视图，用于检查点）。</summary>
    public IReadOnlyDictionary<string, Tensor> Shadow => _shadow;

    /// <summary>用当前模型参数更新影子副本（每次优化器 Step 之后调用）。</summary>
    public void Update(Module<Tensor, Tensor> model)
    {
        ArgumentNullException.ThrowIfNull(model);

        foreach ((string name, Tensor tensor) in model.state_dict())
        {
            if (!_shadow.TryGetValue(name, out Tensor? shadow))
                throw new InvalidOperationException($"EMA 里没有参数 '{name}'（模型结构与创建 EMA 时不一致）。");

            shadow.mul_(Decay);
            shadow.add_(tensor.detach(), alpha: 1f - Decay);
        }
    }

    /// <summary>用检查点里的影子参数覆盖当前 EMA 状态（断点续训时调用）。</summary>
    public void LoadFrom(IReadOnlyDictionary<string, Tensor> state)
    {
        ArgumentNullException.ThrowIfNull(state);

        using (torch.no_grad())
        {
            foreach ((string name, Tensor shadow) in _shadow)
            {
                if (!state.TryGetValue(name, out Tensor? source))
                    throw new InvalidDataException($"EMA 检查点缺少 '{name}'，无法续训。");

                if (!source.shape.SequenceEqual(shadow.shape))
                {
                    throw new InvalidDataException(
                        $"EMA 检查点 '{name}' 形状 [{string.Join(",", source.shape)}] 与模型 "
                        + $"[{string.Join(",", shadow.shape)}] 不一致。");
                }

                shadow.copy_(source);
            }
        }
    }

    /// <summary>把影子参数写回模型（评估或导出前调用）。</summary>
    public void CopyTo(Module<Tensor, Tensor> model)
    {
        ArgumentNullException.ThrowIfNull(model);

        using (torch.no_grad())
        {
            var state = new Dictionary<string, Tensor>(StringComparer.Ordinal);
            foreach ((string name, Tensor tensor) in model.state_dict())
            {
                if (!_shadow.TryGetValue(name, out Tensor? shadow))
                    throw new InvalidOperationException($"EMA 里没有参数 '{name}'。");

                state[name] = shadow;
            }

            model.load_state_dict(state);
        }
    }
}
