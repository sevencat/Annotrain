namespace Sevencat.Train.Engine.IO.Onnx;

/// <summary>
/// ONNX 模型（ModelProto 的极小子集）：IR 版本、producer、opset 与一张计算图。
/// <para>
/// 只写出推理图（前向）。训练状态（优化器、EMA）放在 safetensors 检查点里，不进 ONNX。
/// </para>
/// </summary>
public sealed class OnnxModel
{
    public OnnxModel(OnnxGraph graph, int opsetVersion = 17)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (opsetVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(opsetVersion), opsetVersion, "opset 必须大于 0");

        Graph = graph;
        OpsetVersion = opsetVersion;
    }

    /// <summary>IR 版本，默认 8（ONNX 1.13+）。</summary>
    public long IrVersion { get; set; } = 8;

    public string ProducerName { get; set; } = "Sevencat.Train.Engine";

    public string ProducerVersion { get; set; } = "0.1.0";

    public int OpsetVersion { get; }

    public OnnxGraph Graph { get; }

    public byte[] ToBytes() => EncodeModel(this);

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllBytes(path, ToBytes());
    }

    private static byte[] EncodeModel(OnnxModel model)
    {
        var writer = new ProtoWriter();
        writer.Int64Field(1, model.IrVersion);
        writer.StringField(2, model.ProducerName);
        writer.StringField(3, model.ProducerVersion);
        writer.MessageField(7, graph => EncodeGraph(graph, model.Graph));
        writer.MessageField(8, opset => opset.Int64Field(2, model.OpsetVersion));
        return writer.ToArray();
    }

    private static void EncodeGraph(ProtoWriter writer, OnnxGraph graph)
    {
        writer.StringField(2, graph.Name);

        foreach (OnnxNode node in graph.Nodes)
            writer.MessageField(1, nested => EncodeNode(nested, node));

        foreach (OnnxTensor initializer in graph.Initializers)
            writer.MessageField(5, nested => EncodeTensor(nested, initializer));

        foreach (OnnxValueInfo input in graph.Inputs)
            writer.MessageField(11, nested => EncodeValueInfo(nested, input));

        foreach (OnnxValueInfo output in graph.Outputs)
            writer.MessageField(12, nested => EncodeValueInfo(nested, output));
    }

    private static void EncodeNode(ProtoWriter writer, OnnxNode node)
    {
        foreach (string input in node.Inputs)
            writer.StringField(1, input);

        foreach (string output in node.Outputs)
            writer.StringField(2, output);

        if (!string.IsNullOrEmpty(node.Name))
            writer.StringField(3, node.Name);

        writer.StringField(4, node.OpType);

        foreach (OnnxAttribute attribute in node.Attributes)
            writer.MessageField(5, nested => EncodeAttribute(nested, attribute));
    }

    private static void EncodeTensor(ProtoWriter writer, OnnxTensor tensor)
    {
        writer.PackedInt64Field(1, tensor.Dims);
        writer.Int32Field(2, (int)tensor.DataType);
        writer.StringField(8, tensor.Name);
        writer.BytesField(9, tensor.RawData);
    }

    private static void EncodeValueInfo(ProtoWriter writer, OnnxValueInfo info)
    {
        writer.StringField(1, info.Name);
        writer.MessageField(2, type => type.MessageField(1, tensor =>
        {
            tensor.Int32Field(1, (int)info.ElementType);
            tensor.MessageField(2, shape =>
            {
                foreach (OnnxDimension dimension in info.Dimensions)
                {
                    shape.MessageField(1, dim =>
                    {
                        if (dimension.Value is { } value)
                            dim.Int64Field(1, value);
                        else if (!string.IsNullOrEmpty(dimension.Parameter))
                            dim.StringField(2, dimension.Parameter);
                    });
                }
            });
        }));
    }

    private static void EncodeAttribute(ProtoWriter writer, OnnxAttribute attribute)
    {
        writer.StringField(1, attribute.Name);

        if (attribute.FloatValue is { } floatValue)
        {
            writer.FloatField(2, floatValue);
            writer.Int64Field(20, 1); // FLOAT
        }
        else if (attribute.IntValue is { } intValue)
        {
            writer.Int64Field(3, intValue);
            writer.Int64Field(20, 2); // INT
        }
        else if (attribute.StringValue is { } stringValue)
        {
            writer.StringField(4, stringValue);
            writer.Int64Field(20, 3); // STRING
        }
        else if (attribute.FloatValues is { } floats)
        {
            writer.PackedFloatField(7, floats);
            writer.Int64Field(20, 6); // FLOATS
        }
        else if (attribute.IntValues is { } ints)
        {
            writer.PackedInt64Field(8, ints);
            writer.Int64Field(20, 7); // INTS
        }
        else
        {
            throw new InvalidOperationException($"ONNX 属性 '{attribute.Name}' 没有值。");
        }
    }
}
