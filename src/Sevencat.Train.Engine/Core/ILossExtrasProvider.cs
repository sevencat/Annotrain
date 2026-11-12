using TorchSharp;
using static TorchSharp.torch;

namespace Sevencat.Train.Engine.Core;

/// <summary>
/// 模型可为损失提供"额外张量"的钩子（例如两阶段检测器的提议框/提议 logits）。
/// <para>
/// 为什么放在 <c>Core</c>：<c>Models</c> 只依赖 <c>Core</c> + <c>NN</c>，不能引用 <c>Training</c>，
/// 而 <c>Trainer</c>（在 <c>Training</c>）需要探测模型是否提供 extras。把它放在两者共同的
/// 下游 <c>Core</c> 里，既满足依赖方向，又不引入循环引用。
/// </para>
/// <para>
/// 约定：键名由具体损失定义（如 <c>DetrLoss.ProposalBoxesKey</c>）；返回 <c>null</c> 表示本步无 extras。
/// 实现方必须保证张量与当前前向同源，否则会静默算错损失。
/// </para>
/// </summary>
public interface ILossExtrasProvider
{
    /// <summary>返回本步的额外张量；<c>null</c> 表示没有（训练器将走普通 <c>Compute</c>）。</summary>
    IReadOnlyDictionary<string, Tensor>? GetLossExtras();
}
