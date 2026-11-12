using System.Buffers.Binary;
using System.Text.Json;

namespace Sevencat.Train.Engine.IO.SafeTensors;

/// <summary>
/// safetensors 写入器。
/// <para>
/// 格式（官方 spec）：8 字节小端 <c>u64</c> 头部长度 → N 字节 JSON 头部 → 连续数据区；
/// 每个张量的 <c>data_offsets</c> 是相对数据区起点的 <c>[begin, end)</c>。
/// 头部用空格填充到 8 字节对齐，和 Hugging Face <c>safetensors</c> 的行为一致。
/// </para>
/// </summary>
public sealed class SafeTensorWriter
{
    private readonly List<Entry> _entries = new();
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);

    private sealed record Entry(string Name, string DType, long[] Shape, byte[] Data);

    /// <summary>已加入的张量数量。</summary>
    public int Count => _entries.Count;

    /// <summary>数据区总字节数。</summary>
    public long ByteCount
    {
        get
        {
            long total = 0;
            foreach (Entry entry in _entries)
                total += entry.Data.Length;
            return total;
        }
    }

    /// <summary>加入一个张量（<paramref name="data"/> 必须是 <paramref name="dtype"/> × 元素数的小端字节）。</summary>
    public SafeTensorWriter Add(string name, string dtype, long[] shape, ReadOnlySpan<byte> data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(shape);

        if (!_names.Add(name))
            throw new ArgumentException($"safetensors：张量名重复 '{name}'。", nameof(name));

        long elementCount = 1;
        foreach (long dim in shape)
        {
            if (dim < 0)
                throw new ArgumentOutOfRangeException(nameof(shape), dim, "维度不能为负");
            elementCount *= dim;
        }

        int elementSize = SafeTensorDType.SizeOf(dtype);
        long expected = elementCount * elementSize;
        if (data.Length != expected)
        {
            throw new ArgumentException(
                $"safetensors：张量 '{name}' 数据 {data.Length} B，但 {elementCount} × {dtype} 需要 {expected} B。",
                nameof(data));
        }

        _entries.Add(new Entry(name, dtype, (long[])shape.Clone(), data.ToArray()));
        return this;
    }

    /// <summary>加入 F32 张量。</summary>
    public SafeTensorWriter AddFloat32(string name, long[] shape, ReadOnlySpan<float> data)
    {
        byte[] bytes = new byte[data.Length * sizeof(float)];
        for (int i = 0; i < data.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), data[i]);

        return Add(name, SafeTensorDType.F32, shape, bytes);
    }

    /// <summary>加入 I64 张量。</summary>
    public SafeTensorWriter AddInt64(string name, long[] shape, ReadOnlySpan<long> data)
    {
        byte[] bytes = new byte[data.Length * sizeof(long)];
        for (int i = 0; i < data.Length; i++)
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(i * sizeof(long)), data[i]);

        return Add(name, SafeTensorDType.I64, shape, bytes);
    }

    /// <summary>生成完整文件字节。</summary>
    public byte[] ToBytes(IReadOnlyDictionary<string, string>? metadata = null)
    {
        long offset = 0;
        using var headerStream = new MemoryStream();
        using (var json = new Utf8JsonWriter(headerStream))
        {
            json.WriteStartObject();
            foreach (Entry entry in _entries)
            {
                json.WritePropertyName(entry.Name);
                json.WriteStartObject();
                json.WriteString("dtype", entry.DType);
                json.WritePropertyName("shape");
                json.WriteStartArray();
                foreach (long dim in entry.Shape)
                    json.WriteNumberValue(dim);
                json.WriteEndArray();
                json.WritePropertyName("data_offsets");
                json.WriteStartArray();
                json.WriteNumberValue(offset);
                offset += entry.Data.Length;
                json.WriteNumberValue(offset);
                json.WriteEndArray();
                json.WriteEndObject();
            }

            if (metadata is { Count: > 0 })
            {
                json.WritePropertyName("__metadata__");
                json.WriteStartObject();
                foreach ((string key, string value) in metadata)
                    json.WriteString(key, value);
                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        byte[] header = headerStream.ToArray();
        int padding = (8 - (header.Length % 8)) % 8;
        if (padding > 0)
            Array.Resize(ref header, header.Length + padding);
        for (int i = header.Length - padding; padding > 0 && i < header.Length; i++)
            header[i] = (byte)' ';

        long totalLength = 8 + header.Length + offset;
        byte[] file = new byte[checked((int)totalLength)];
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(0, 8), (ulong)header.Length);
        header.CopyTo(file, 8);

        int position = 8 + header.Length;
        foreach (Entry entry in _entries)
        {
            entry.Data.CopyTo(file, position);
            position += entry.Data.Length;
        }

        return file;
    }

    /// <summary>写入文件。</summary>
    public void Save(string path, IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] bytes = ToBytes(metadata);
        File.WriteAllBytes(path, bytes);
    }
}
