using System.Globalization;
using TorchSharp;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.Yolo;

/// <summary>
/// 按 ultralytics 的层号（<c>model.0</c> … <c>model.22</c>）注册子模块，并按 <c>from</c> 索引执行前向。
/// <para>
/// 为什么不用 <c>nn.Sequential</c>：YOLO 颈部有 <c>Concat</c>（多输入）和跨层引用（第 11 层同时取第 10、6 层），
/// 顺序容器表达不了这张有向图。这里保留一张「层号 → 模块 + 输入来源」的表，
/// 与 ultralytics <c>BaseModel._predict_once</c> 的语义一致：
/// <c>-1</c> 表示上一层输出，非负整数表示该层号的输出。
/// </para>
/// <para>
/// 子模块用字符串层号注册，<c>state_dict</c> 键因此是 <c>model.0.conv.weight</c>，
/// 与 ultralytics 完全对齐。模块列表存放在非 <see cref="Module"/> 字段里，
/// 避免 TorchSharp 的字段反射把它再注册一次。
/// </para>
/// </summary>
public sealed class YoloModuleChain : Module<Tensor[], Tensor>
{
    private readonly int[][] _from;
    private readonly Module[] _modules;

    /// <summary>创建层链；<paramref name="modules"/> 与 <paramref name="from"/> 按层号一一对应。</summary>
    public YoloModuleChain(string name, IReadOnlyList<int[]> from, IReadOnlyList<Module> modules)
        : base(name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(modules);

        if (from.Count != modules.Count)
        {
            throw new ArgumentException(
                $"层链 '{name}'：from 有 {from.Count} 项，模块有 {modules.Count} 个，必须一一对应。",
                nameof(from));
        }

        if (modules.Count == 0)
            throw new ArgumentException($"层链 '{name}' 不能为空。", nameof(modules));

        _from = new int[from.Count][];
        for (int index = 0; index < from.Count; index++)
            _from[index] = (int[])from[index].Clone();

        _modules = modules.ToArray();

        for (int index = 0; index < _modules.Length; index++)
        {
            string key = index.ToString(CultureInfo.InvariantCulture);
            register_module(key, _modules[index] ?? throw new ArgumentException(
                $"层链 '{name}'：第 {index} 层为 null。", nameof(modules)));
        }
    }

    /// <summary>层数。</summary>
    public int Count => _modules.Length;

    /// <summary>每层的输入来源（<c>-1</c> = 上一层，非负 = 该层号），导出时按它连边。</summary>
    public IReadOnlyList<IReadOnlyList<int>> InputSources
    {
        get
        {
            var rows = new IReadOnlyList<int>[_from.Length];
            for (int index = 0; index < _from.Length; index++)
                rows[index] = _from[index];

            return rows;
        }
    }

    /// <summary>取某一层的模块（导出 / 调试用）。</summary>
    public Module Layer(int index)
    {
        if (index < 0 || index >= _modules.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index), index, $"层号必须在 [0,{_modules.Length}) 内。");
        }

        return _modules[index];
    }

    /// <summary>
    /// 执行整条链：<paramref name="input"/> 是模型的输入张量（通常只有 1 个）。
    /// 返回最后一层的输出。
    /// </summary>
    public override Tensor forward(Tensor[] input) => ForwardTrace(input)[^1];

    /// <summary>
    /// 逐层输出（调试 / 与参考实现逐层对齐用）。
    /// <para>返回值与层号一一对应：<c>result[i]</c> 是第 <c>i</c> 层的输出。</para>
    /// </summary>
    public IReadOnlyList<Tensor> ForwardTrace(Tensor[] input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length == 0)
            throw new ArgumentException("层链至少需要 1 个输入张量。", nameof(input));

        var outputs = new List<Tensor>(_modules.Length);

        for (int index = 0; index < _modules.Length; index++)
        {
            Tensor[] arguments = ResolveInputs(_from[index], outputs, input, index);
            outputs.Add(Invoke(_modules[index], arguments));
        }

        return outputs;
    }

    private static Tensor[] ResolveInputs(int[] from, List<Tensor> outputs, Tensor[] input, int layerIndex)
    {
        if (from.Length == 0)
            throw new InvalidOperationException($"第 {layerIndex} 层没有声明输入来源。");

        var arguments = new Tensor[from.Length];
        for (int index = 0; index < from.Length; index++)
        {
            int source = from[index];
            if (source == -1)
            {
                // 第 0 层的 -1 表示模型输入；其余层表示上一层输出。
                arguments[index] = outputs.Count == 0 ? input[0] : outputs[^1];
            }
            else if (source >= 0 && source < outputs.Count)
            {
                arguments[index] = outputs[source];
            }
            else
            {
                throw new InvalidOperationException(
                    $"第 {layerIndex} 层引用了第 {source} 层的输出，但该层还没有产生输出（当前 {outputs.Count} 层）。");
            }
        }

        return arguments;
    }

    private static Tensor Invoke(Module module, Tensor[] inputs) => module switch
    {
        Module<Tensor[], Tensor> multiInput => multiInput.forward(inputs),
        Module<Tensor, Tensor> singleInput => singleInput.forward(inputs[0]),
        _ => throw new NotSupportedException(
            $"层类型 {module.GetType().Name} 不支持：只接受 Module<Tensor,Tensor> 或 Module<Tensor[],Tensor>。"),
    };
}
