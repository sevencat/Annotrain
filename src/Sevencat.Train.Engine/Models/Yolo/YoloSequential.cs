using TorchSharp;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.Yolo;

/// <summary>
/// 定长顺序容器：行为等价于 <c>nn.Sequential</c>，但额外暴露子模块列表，
/// 便于 ONNX 导出逐层展开（<c>nn.Sequential</c> 只暴露名字与不透明项）。
/// <para>
/// 子模块仍按 "0" / "1" / "2" 注册，所以 <c>state_dict</c> 键与 ultralytics 一致
/// （如 <c>model.22.cv2.0.0.conv.weight</c>）。
/// </para>
/// </summary>
public sealed class YoloSequential : Module<Tensor, Tensor>
{
    private readonly Module<Tensor, Tensor>[] _items;

    /// <summary>按给定顺序注册子模块；名字必须是唯一字符串（通常 "0","1","2"）。</summary>
    public YoloSequential(string name, params (string Name, Module<Tensor, Tensor> Module)[] items)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Length == 0)
            throw new ArgumentException($"顺序容器 '{name}' 不能为空。", nameof(items));

        _items = new Module<Tensor, Tensor>[items.Length];
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int index = 0; index < items.Length; index++)
        {
            (string childName, Module<Tensor, Tensor> module) = items[index];
            ArgumentException.ThrowIfNullOrWhiteSpace(childName);

            if (module is null)
                throw new ArgumentException($"顺序容器 '{name}' 的第 {index} 项为 null。", nameof(items));
            if (!seen.Add(childName))
                throw new ArgumentException($"顺序容器 '{name}' 的子模块名 '{childName}' 重复。", nameof(items));

            _items[index] = module;
            register_module(childName, module);
        }
    }

    /// <summary>子模块列表（导出时按顺序展开）。</summary>
    public IReadOnlyList<Module<Tensor, Tensor>> Items => _items;

    /// <inheritdoc />
    public override Tensor forward(Tensor input)
    {
        Tensor current = input;
        foreach (Module<Tensor, Tensor> item in _items)
            current = item.forward(current);

        return current;
    }
}
