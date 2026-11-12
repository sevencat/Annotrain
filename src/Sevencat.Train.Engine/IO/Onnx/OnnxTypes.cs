using System.Buffers.Binary;

namespace Sevencat.Train.Engine.IO.Onnx;

/// <summary>ONNX TensorProto.DataType（只列训练/导出会用到的）。</summary>
public enum OnnxDataType
{
    Float = 1,
    UInt8 = 2,
    Int8 = 3,
    UInt16 = 4,
    Int16 = 5,
    Int32 = 6,
    Int64 = 7,
    String = 8,
    Bool = 9,
    Float16 = 10,
    Double = 11,
}

/// <summary>ONNX 维度：固定值或符号名（如 <c>batch</c>）。</summary>
public readonly record struct OnnxDimension(long? Value, string? Parameter)
{
    public static implicit operator OnnxDimension(long value) => new(value, null);

    public static implicit operator OnnxDimension(string parameter) => new(null, parameter);

    public override string ToString() => Value?.ToString(System.Globalization.CultureInfo.InvariantCulture)
        ?? Parameter
        ?? "?";
}

/// <summary>图输入 / 输出的类型声明（ValueInfoProto）。</summary>
public sealed class OnnxValueInfo
{
    public OnnxValueInfo(string name, OnnxDataType elementType, params OnnxDimension[] dimensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(dimensions);

        Name = name;
        ElementType = elementType;
        Dimensions = dimensions.ToArray();
    }

    public string Name { get; }

    public OnnxDataType ElementType { get; }

    public IReadOnlyList<OnnxDimension> Dimensions { get; }

    public override string ToString() => $"{Name}: {ElementType}[{string.Join(",", Dimensions)}]";
}

/// <summary>initializer（权重常量，TensorProto）。</summary>
public sealed class OnnxTensor
{
    public OnnxTensor(string name, OnnxDataType dataType, long[] dims, byte[] rawData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(dims);
        ArgumentNullException.ThrowIfNull(rawData);

        long elementCount = 1;
        foreach (long dim in dims)
            elementCount *= dim;

        int elementSize = SizeOf(dataType);
        if (elementCount * elementSize != rawData.Length)
        {
            throw new ArgumentException(
                $"ONNX initializer '{name}' 数据 {rawData.Length} B，"
                + $"但 {elementCount} × {dataType} 需要 {elementCount * elementSize} B。",
                nameof(rawData));
        }

        Name = name;
        DataType = dataType;
        Dims = (long[])dims.Clone();
        RawData = rawData;
    }

    public string Name { get; }

    public OnnxDataType DataType { get; }

    public long[] Dims { get; }

    public byte[] RawData { get; }

    public static OnnxTensor FromFloat32(string name, long[] dims, ReadOnlySpan<float> values)
    {
        byte[] bytes = new byte[values.Length * sizeof(float)];
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), values[i]);

        return new OnnxTensor(name, OnnxDataType.Float, dims, bytes);
    }

    public static OnnxTensor FromInt64(string name, long[] dims, ReadOnlySpan<long> values)
    {
        byte[] bytes = new byte[values.Length * sizeof(long)];
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(i * sizeof(long)), values[i]);

        return new OnnxTensor(name, OnnxDataType.Int64, dims, bytes);
    }

    internal static int SizeOf(OnnxDataType dataType) => dataType switch
    {
        OnnxDataType.Float or OnnxDataType.Int32 => 4,
        OnnxDataType.UInt16 or OnnxDataType.Int16 or OnnxDataType.Float16 => 2,
        OnnxDataType.Double or OnnxDataType.Int64 => 8,
        OnnxDataType.UInt8 or OnnxDataType.Int8 or OnnxDataType.Bool => 1,
        _ => throw new NotSupportedException($"ONNX：dtype {dataType} 的字节数未知。"),
    };

    public override string ToString() => $"{Name}: {DataType}[{string.Join(",", Dims)}]";
}

/// <summary>节点属性（AttributeProto）。</summary>
public sealed class OnnxAttribute
{
    private OnnxAttribute(
        string name,
        long? intValue,
        float? floatValue,
        string? stringValue,
        long[]? ints,
        float[]? floats)
    {
        Name = name;
        IntValue = intValue;
        FloatValue = floatValue;
        StringValue = stringValue;
        IntValues = ints;
        FloatValues = floats;
    }

    public string Name { get; }

    public long? IntValue { get; }

    public float? FloatValue { get; }

    public string? StringValue { get; }

    public long[]? IntValues { get; }

    public float[]? FloatValues { get; }

    public static OnnxAttribute Int(string name, long value) =>
        new(name, value, null, null, null, null);

    public static OnnxAttribute Float(string name, float value) =>
        new(name, null, value, null, null, null);

    public static OnnxAttribute String(string name, string value) =>
        new(name, null, null, value, null, null);

    public static OnnxAttribute Ints(string name, params long[] values) =>
        new(name, null, null, null, values, null);

    public static OnnxAttribute Floats(string name, params float[] values) =>
        new(name, null, null, null, null, values);
}
