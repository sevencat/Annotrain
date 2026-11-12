namespace Sevencat.Train.Engine.IO.Onnx;

/// <summary>计算图节点（NodeProto）。</summary>
public sealed class OnnxNode
{
    public OnnxNode(
        string opType,
        IReadOnlyList<string> inputs,
        IReadOnlyList<string> outputs,
        IReadOnlyList<OnnxAttribute>? attributes = null,
        string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(opType);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);

        OpType = opType;
        Inputs = inputs.ToArray();
        Outputs = outputs.ToArray();
        Attributes = attributes?.ToArray() ?? Array.Empty<OnnxAttribute>();
        Name = name;
    }

    public string OpType { get; }

    public IReadOnlyList<string> Inputs { get; }

    public IReadOnlyList<string> Outputs { get; }

    public IReadOnlyList<OnnxAttribute> Attributes { get; }

    public string? Name { get; }

    public override string ToString() => $"{OpType}({string.Join(",", Inputs)}) -> {string.Join(",", Outputs)}";
}

/// <summary>计算图（GraphProto）。</summary>
public sealed class OnnxGraph
{
    public OnnxGraph(
        string name,
        IReadOnlyList<OnnxNode> nodes,
        IReadOnlyList<OnnxTensor> initializers,
        IReadOnlyList<OnnxValueInfo> inputs,
        IReadOnlyList<OnnxValueInfo> outputs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(initializers);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);

        Name = name;
        Nodes = nodes.ToArray();
        Initializers = initializers.ToArray();
        Inputs = inputs.ToArray();
        Outputs = outputs.ToArray();
    }

    public string Name { get; }

    public IReadOnlyList<OnnxNode> Nodes { get; }

    public IReadOnlyList<OnnxTensor> Initializers { get; }

    public IReadOnlyList<OnnxValueInfo> Inputs { get; }

    public IReadOnlyList<OnnxValueInfo> Outputs { get; }
}

/// <summary>流式构建 ONNX 图。</summary>
public sealed class OnnxGraphBuilder
{
    private readonly string _name;
    private readonly List<OnnxNode> _nodes = new();
    private readonly List<OnnxTensor> _initializers = new();
    private readonly List<OnnxValueInfo> _inputs = new();
    private readonly List<OnnxValueInfo> _outputs = new();

    public OnnxGraphBuilder(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
    }

    public OnnxGraphBuilder Input(string name, OnnxDataType elementType, params OnnxDimension[] dimensions)
    {
        _inputs.Add(new OnnxValueInfo(name, elementType, dimensions));
        return this;
    }

    public OnnxGraphBuilder Output(string name, OnnxDataType elementType, params OnnxDimension[] dimensions)
    {
        _outputs.Add(new OnnxValueInfo(name, elementType, dimensions));
        return this;
    }

    public OnnxGraphBuilder Initializer(OnnxTensor tensor)
    {
        ArgumentNullException.ThrowIfNull(tensor);
        _initializers.Add(tensor);
        return this;
    }

    public OnnxGraphBuilder Node(
        string opType,
        string[] inputs,
        string[] outputs,
        params OnnxAttribute[] attributes)
    {
        // 节点名必须唯一：ONNX Runtime 的图层优化会按节点名做融合/改名，
        // 无 name 的节点在可变形注意力、多层重复结构里会撞名（invalid model）。
        _nodes.Add(new OnnxNode(opType, inputs, outputs, attributes, $"{opType}_{_nodes.Count}"));
        return this;
    }

    public OnnxGraph Build() => new(_name, _nodes, _initializers, _inputs, _outputs);
}
