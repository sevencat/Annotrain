using Sevencat.Train.Engine.Core;
using Sevencat.Train.Engine.IO.SafeTensors;
using TorchSharp;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Training;

/// <summary>
/// 训练检查点：safetensors 里的 <c>state_dict</c> + 字符串元数据。
/// <para>
/// 键名与 TorchSharp / PyTorch 的 <c>state_dict()</c> 完全一致（<c>fc0.weight</c> …），
/// 因此检查点可以直接与 PyTorch 互转（注意 Linear 权重布局差异，见 docs/formats.md）。
/// </para>
/// </summary>
public sealed class Checkpoint : IDisposable
{
    /// <summary>当前检查点格式标识。</summary>
    public const string FormatName = "omnits-checkpoint/1";

    private readonly SafeTensorFile _file;

    private Checkpoint(SafeTensorFile file) => _file = file;

    /// <summary>路径。</summary>
    public string Path => _file.Path;

    /// <summary>元数据。</summary>
    public IReadOnlyDictionary<string, string> Metadata => _file.Metadata;

    /// <summary>格式标识（缺失时为空串）。</summary>
    public string Format => Metadata.TryGetValue("format", out string? format) ? format : string.Empty;

    /// <summary>张量条目。</summary>
    public IReadOnlyList<SafeTensorEntry> Tensors => _file.Tensors;

    /// <summary>把 state_dict（或 EMA 影子）写成检查点。</summary>
    public static void Save(
        string path,
        IReadOnlyDictionary<string, Tensor> state,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(state);

        var merged = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["format"] = FormatName,
            ["created"] = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        };

        if (metadata is not null)
        {
            foreach ((string key, string value) in metadata)
                merged[key] = value;
        }

        var writer = new SafeTensorWriter();
        foreach ((string name, Tensor tensor) in state)
            writer.AddFloat32(name, tensor.shape, TorchInterop.ToArray(tensor));

        string? directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        writer.Save(path, merged);
    }

    /// <summary>把模型 state_dict 写成 safetensors 检查点。</summary>
    public static void Save(
        string path,
        Module<Tensor, Tensor> model,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        Save(path, model.state_dict(), metadata);
    }

    /// <summary>打开检查点（不加载数据）。</summary>
    public static Checkpoint Open(string path) => new(SafeTensorFile.Open(path));

    /// <summary>读出全部 F32 张量（例如把 EMA 影子灌回 <see cref="Ema"/>）。</summary>
    public Dictionary<string, Tensor> LoadState(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var state = new Dictionary<string, Tensor>(Tensors.Count, StringComparer.Ordinal);
        foreach (SafeTensorEntry entry in Tensors)
            state[entry.Name] = torch.tensor(_file.ReadFloat32(entry.Name), entry.Shape, device: device);

        return state;
    }

    /// <summary>把检查点写回模型（形状/键名必须匹配）。</summary>
    public void LoadInto(Module<Tensor, Tensor> model, Device device, bool strict = true)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(device);

        Dictionary<string, Tensor> state = model.state_dict();
        var loaded = new Dictionary<string, Tensor>(StringComparer.Ordinal);

        foreach ((string name, Tensor tensor) in LoadState(device))
        {
            if (state.ContainsKey(name))
                loaded[name] = tensor;
        }

        model.load_state_dict(loaded, strict);
    }

    public void Dispose() => _file.Dispose();
}
