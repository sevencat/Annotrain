using System.Buffers.Binary;
using System.Text;

namespace Sevencat.Train.Engine.IO.Onnx;

/// <summary>protobuf wire type（只实现 ONNX 用得到的四种）。</summary>
internal enum ProtoWireType
{
    Varint = 0,
    Fixed64 = 1,
    LengthDelimited = 2,
    Fixed32 = 5,
}

/// <summary>最小 protobuf 编码器（ONNX ModelProto 手写序列化，不依赖 Google.Protobuf）。</summary>
internal sealed class ProtoWriter
{
    private readonly MemoryStream _stream = new();

    public void Tag(int field, ProtoWireType wireType) =>
        Varint(((ulong)field << 3) | (ulong)wireType);

    public void Varint(ulong value)
    {
        while (value >= 0x80)
        {
            _stream.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        _stream.WriteByte((byte)value);
    }

    public void Int64Field(int field, long value)
    {
        Tag(field, ProtoWireType.Varint);
        Varint((ulong)value);
    }

    public void Int32Field(int field, int value) => Int64Field(field, value);

    public void StringField(int field, string value)
    {
        Tag(field, ProtoWireType.LengthDelimited);
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Varint((ulong)bytes.Length);
        _stream.Write(bytes);
    }

    public void BytesField(int field, ReadOnlySpan<byte> data)
    {
        Tag(field, ProtoWireType.LengthDelimited);
        Varint((ulong)data.Length);
        _stream.Write(data);
    }

    public void FloatField(int field, float value)
    {
        Tag(field, ProtoWireType.Fixed32);
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(buffer, value);
        _stream.Write(buffer);
    }

    public void MessageField(int field, Action<ProtoWriter> body)
    {
        var nested = new ProtoWriter();
        body(nested);
        BytesField(field, nested.ToArray());
    }

    public void PackedInt64Field(int field, IReadOnlyList<long> values)
    {
        var nested = new ProtoWriter();
        foreach (long value in values)
            nested.Varint((ulong)value);
        BytesField(field, nested.ToArray());
    }

    public void PackedFloatField(int field, IReadOnlyList<float> values)
    {
        var nested = new ProtoWriter();
        Span<byte> buffer = stackalloc byte[4];
        foreach (float value in values)
        {
            BinaryPrimitives.WriteSingleLittleEndian(buffer, value);
            nested._stream.Write(buffer);
        }

        BytesField(field, nested.ToArray());
    }

    public byte[] ToArray() => _stream.ToArray();
}

/// <summary>最小 protobuf 读取器（用于把写出的 ONNX 读回来做结构校验）。</summary>
internal ref struct ProtoReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    public ProtoReader(ReadOnlySpan<byte> buffer, int position = 0)
    {
        _buffer = buffer;
        _position = position;
    }

    public int Position => _position;

    /// <summary>剩余可读字节数。</summary>
    public int Remaining => _buffer.Length - _position;

    public bool TryReadTag(out int field, out ProtoWireType wireType)
    {
        if (_position >= _buffer.Length)
        {
            field = 0;
            wireType = default;
            return false;
        }

        ulong tag = ReadVarint();
        field = (int)(tag >> 3);
        wireType = (ProtoWireType)(tag & 0x7);
        return true;
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        int shift = 0;

        while (true)
        {
            if (_position >= _buffer.Length)
                throw new InvalidDataException("protobuf：varint 被截断。");

            byte current = _buffer[_position++];
            result |= (ulong)(current & 0x7F) << shift;

            if ((current & 0x80) == 0)
                return result;

            shift += 7;
            if (shift > 63)
                throw new InvalidDataException("protobuf：varint 超过 64 位。");
        }
    }

    public long ReadInt64() => (long)ReadVarint();

    public int ReadInt32() => (int)ReadVarint();

    public ReadOnlySpan<byte> ReadLengthDelimited()
    {
        int length = checked((int)ReadVarint());
        if (_position + length > _buffer.Length)
            throw new InvalidDataException("protobuf：length-delimited 字段越界。");

        ReadOnlySpan<byte> slice = _buffer.Slice(_position, length);
        _position += length;
        return slice;
    }

    public string ReadString() => Encoding.UTF8.GetString(ReadLengthDelimited());

    public float ReadFloat()
    {
        if (_position + 4 > _buffer.Length)
            throw new InvalidDataException("protobuf：fixed32 字段越界。");

        float value = BinaryPrimitives.ReadSingleLittleEndian(_buffer.Slice(_position, 4));
        _position += 4;
        return value;
    }

    public void Skip(ProtoWireType wireType)
    {
        switch (wireType)
        {
            case ProtoWireType.Varint:
                ReadVarint();
                break;
            case ProtoWireType.Fixed64:
                Advance(8);
                break;
            case ProtoWireType.LengthDelimited:
                ReadLengthDelimited();
                break;
            case ProtoWireType.Fixed32:
                Advance(4);
                break;
            default:
                throw new InvalidDataException($"protobuf：不支持的 wire type {(int)wireType}。");
        }
    }

    private void Advance(int count)
    {
        if (_position + count > _buffer.Length)
            throw new InvalidDataException("protobuf：字段越界。");
        _position += count;
    }
}
