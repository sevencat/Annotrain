namespace Sevencat.Train.Engine.IO.Onnx;

/// <summary>ONNX initializer 的结构信息。</summary>
public sealed record OnnxTensorSummary(string Name, OnnxDataType DataType, long[] Dims);

/// <summary>ONNX 输入 / 输出的类型信息。</summary>
public sealed record OnnxValueSummary(string Name, OnnxDataType DataType, IReadOnlyList<OnnxDimension> Dimensions);

/// <summary>ONNX 节点摘要。</summary>
public sealed record OnnxNodeSummary(
    string OpType,
    string? Name,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Outputs);

/// <summary>ONNX 文件的结构摘要（不加载权重内容）。</summary>
public sealed record OnnxModelSummary(
    long IrVersion,
    string? ProducerName,
    int OpsetVersion,
    string? GraphName,
    IReadOnlyList<OnnxNodeSummary> Nodes,
    IReadOnlyList<OnnxTensorSummary> Initializers,
    IReadOnlyList<OnnxValueSummary> Inputs,
    IReadOnlyList<OnnxValueSummary> Outputs);

/// <summary>
/// 读取 ONNX 结构（ModelProto 的只读子集）。
/// <para>
/// 用途：写出后自检、<c>omnits inspect</c>，以及 Python <c>onnx.checker</c> 之外的纯 C# 校验路径。
/// </para>
/// </summary>
public static class OnnxModelReader
{
    public static OnnxModelSummary Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllBytes(path));
    }

    public static OnnxModelSummary Parse(ReadOnlySpan<byte> bytes)
    {
        long irVersion = 0;
        string? producerName = null;
        int opsetVersion = 0;
        GraphResult? graph = null;

        var reader = new ProtoReader(bytes);
        while (reader.TryReadTag(out int field, out ProtoWireType wireType))
        {
            switch (field)
            {
                case 1:
                    irVersion = reader.ReadInt64();
                    break;
                case 2:
                    producerName = reader.ReadString();
                    break;
                case 7:
                    graph = ParseGraph(reader.ReadLengthDelimited());
                    break;
                case 8:
                    opsetVersion = ParseOpset(reader.ReadLengthDelimited());
                    break;
                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        if (graph is null)
            throw new InvalidDataException("ONNX：文件里没有 graph 字段。");

        return new OnnxModelSummary(
            irVersion,
            producerName,
            opsetVersion,
            graph.Name,
            graph.Nodes,
            graph.Initializers,
            graph.Inputs,
            graph.Outputs);
    }

    private sealed record GraphResult(
        string? Name,
        List<OnnxNodeSummary> Nodes,
        List<OnnxTensorSummary> Initializers,
        List<OnnxValueSummary> Inputs,
        List<OnnxValueSummary> Outputs);

    private static int ParseOpset(ReadOnlySpan<byte> span)
    {
        int version = 0;
        var reader = new ProtoReader(span);
        while (reader.TryReadTag(out int field, out ProtoWireType wireType))
        {
            if (field == 2)
                version = reader.ReadInt32();
            else
                reader.Skip(wireType);
        }

        return version;
    }

    private static GraphResult ParseGraph(ReadOnlySpan<byte> span)
    {
        string? name = null;
        var nodes = new List<OnnxNodeSummary>();
        var initializers = new List<OnnxTensorSummary>();
        var inputs = new List<OnnxValueSummary>();
        var outputs = new List<OnnxValueSummary>();

        var reader = new ProtoReader(span);
        while (reader.TryReadTag(out int field, out ProtoWireType wireType))
        {
            switch (field)
            {
                case 1:
                    nodes.Add(ParseNode(reader.ReadLengthDelimited()));
                    break;
                case 2:
                    name = reader.ReadString();
                    break;
                case 5:
                    initializers.Add(ParseTensor(reader.ReadLengthDelimited()));
                    break;
                case 11:
                    inputs.Add(ParseValueInfo(reader.ReadLengthDelimited()));
                    break;
                case 12:
                    outputs.Add(ParseValueInfo(reader.ReadLengthDelimited()));
                    break;
                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        return new GraphResult(name, nodes, initializers, inputs, outputs);
    }

    private static OnnxNodeSummary ParseNode(ReadOnlySpan<byte> span)
    {
        var inputs = new List<string>();
        var outputs = new List<string>();
        string? name = null;
        string opType = string.Empty;

        var reader = new ProtoReader(span);
        while (reader.TryReadTag(out int field, out ProtoWireType wireType))
        {
            switch (field)
            {
                case 1:
                    inputs.Add(reader.ReadString());
                    break;
                case 2:
                    outputs.Add(reader.ReadString());
                    break;
                case 3:
                    name = reader.ReadString();
                    break;
                case 4:
                    opType = reader.ReadString();
                    break;
                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        return new OnnxNodeSummary(opType, name, inputs, outputs);
    }

    private static OnnxTensorSummary ParseTensor(ReadOnlySpan<byte> span)
    {
        var dims = new List<long>();
        var dataType = OnnxDataType.Float;
        string name = string.Empty;

        var reader = new ProtoReader(span);
        while (reader.TryReadTag(out int field, out ProtoWireType wireType))
        {
            switch (field)
            {
                case 1:
                    if (wireType == ProtoWireType.LengthDelimited)
                    {
                        var packed = new ProtoReader(reader.ReadLengthDelimited());
                        while (packed.Remaining > 0)
                            dims.Add(packed.ReadInt64());
                    }
                    else
                    {
                        dims.Add(reader.ReadInt64());
                    }

                    break;
                case 2:
                    dataType = (OnnxDataType)reader.ReadInt32();
                    break;
                case 8:
                    name = reader.ReadString();
                    break;
                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        return new OnnxTensorSummary(name, dataType, dims.ToArray());
    }

    private static OnnxValueSummary ParseValueInfo(ReadOnlySpan<byte> span)
    {
        string name = string.Empty;
        var dataType = OnnxDataType.Float;
        var dimensions = new List<OnnxDimension>();

        var reader = new ProtoReader(span);
        while (reader.TryReadTag(out int field, out ProtoWireType wireType))
        {
            switch (field)
            {
                case 1:
                    name = reader.ReadString();
                    break;
                case 2:
                    ParseType(reader.ReadLengthDelimited(), out dataType, dimensions);
                    break;
                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        return new OnnxValueSummary(name, dataType, dimensions);
    }

    private static void ParseType(
        ReadOnlySpan<byte> span,
        out OnnxDataType dataType,
        List<OnnxDimension> dimensions)
    {
        dataType = OnnxDataType.Float;

        var reader = new ProtoReader(span);
        while (reader.TryReadTag(out int field, out ProtoWireType wireType))
        {
            if (field != 1)
            {
                reader.Skip(wireType);
                continue;
            }

            var tensor = new ProtoReader(reader.ReadLengthDelimited());
            while (tensor.TryReadTag(out int tensorField, out ProtoWireType tensorWire))
            {
                switch (tensorField)
                {
                    case 1:
                        dataType = (OnnxDataType)tensor.ReadInt32();
                        break;
                    case 2:
                        ParseShape(tensor.ReadLengthDelimited(), dimensions);
                        break;
                    default:
                        tensor.Skip(tensorWire);
                        break;
                }
            }
        }
    }

    private static void ParseShape(ReadOnlySpan<byte> span, List<OnnxDimension> dimensions)
    {
        var reader = new ProtoReader(span);
        while (reader.TryReadTag(out int field, out ProtoWireType wireType))
        {
            if (field != 1)
            {
                reader.Skip(wireType);
                continue;
            }

            long? value = null;
            string? parameter = null;
            var dimension = new ProtoReader(reader.ReadLengthDelimited());
            while (dimension.TryReadTag(out int dimField, out ProtoWireType dimWire))
            {
                switch (dimField)
                {
                    case 1:
                        value = dimension.ReadInt64();
                        break;
                    case 2:
                        parameter = dimension.ReadString();
                        break;
                    default:
                        dimension.Skip(dimWire);
                        break;
                }
            }

            dimensions.Add(new OnnxDimension(value, parameter));
        }
    }
}
