using System.Buffers.Binary;

namespace Sevencat.Train.Engine.IO.SafeTensors;

/// <summary>safetensors 头部的 dtype 字符串与内存布局换算。</summary>
public static class SafeTensorDType
{
    public const string F64 = "F64";
    public const string F32 = "F32";
    public const string F16 = "F16";
    public const string BF16 = "BF16";
    public const string I64 = "I64";
    public const string I32 = "I32";
    public const string I16 = "I16";
    public const string I8 = "I8";
    public const string U64 = "U64";
    public const string U32 = "U32";
    public const string U16 = "U16";
    public const string U8 = "U8";
    public const string Bool = "BOOL";

    /// <summary>单个元素的字节数。</summary>
    public static int SizeOf(string dtype) => dtype switch
    {
        F64 or I64 or U64 => 8,
        F32 or I32 or U32 => 4,
        F16 or BF16 or I16 or U16 => 2,
        I8 or U8 or Bool => 1,
        _ => throw new NotSupportedException($"safetensors：未知 dtype '{dtype}'。"),
    };

    /// <summary>是否为已知 dtype。</summary>
    public static bool IsKnown(string dtype) => dtype is
        F64 or F32 or F16 or BF16 or I64 or I32 or I16 or I8 or U64 or U32 or U16 or U8 or Bool;

    /// <summary>把原始字节解码成 F32（整型按数值转换；BF16 / F16 做位扩展）。</summary>
    public static float[] ToFloat32(string dtype, ReadOnlySpan<byte> data, string tensorName)
    {
        int size = SizeOf(dtype);
        if (data.Length % size != 0)
            throw new InvalidDataException($"safetensors：'{tensorName}' 的字节数 {data.Length} 不是 {dtype} 的整数倍。");

        int count = data.Length / size;
        var values = new float[count];

        switch (dtype)
        {
            case F32:
                for (int i = 0; i < count; i++)
                    values[i] = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(i * 4, 4));
                break;

            case F64:
                for (int i = 0; i < count; i++)
                    values[i] = (float)BinaryPrimitives.ReadDoubleLittleEndian(data.Slice(i * 8, 8));
                break;

            case F16:
                for (int i = 0; i < count; i++)
                {
                    ushort bits = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i * 2, 2));
                    values[i] = (float)BitConverter.UInt16BitsToHalf(bits);
                }
                break;

            case BF16:
                for (int i = 0; i < count; i++)
                {
                    ushort bits = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i * 2, 2));
                    values[i] = BitConverter.UInt32BitsToSingle((uint)bits << 16);
                }
                break;

            case I64:
                for (int i = 0; i < count; i++)
                    values[i] = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(i * 8, 8));
                break;

            case I32:
                for (int i = 0; i < count; i++)
                    values[i] = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(i * 4, 4));
                break;

            case U8:
                for (int i = 0; i < count; i++)
                    values[i] = data[i];
                break;

            case Bool:
                for (int i = 0; i < count; i++)
                    values[i] = data[i] != 0 ? 1f : 0f;
                break;

            default:
                throw new NotSupportedException(
                    $"safetensors：'{tensorName}' 的 dtype {dtype} 暂不支持转换为 F32。");
        }

        return values;
    }
}
