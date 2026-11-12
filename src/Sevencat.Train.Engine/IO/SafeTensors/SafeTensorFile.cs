using System.Buffers.Binary;
using System.Text.Json;

namespace Sevencat.Train.Engine.IO.SafeTensors;

/// <summary>safetensors 里的一个张量条目（只含头部元信息，数据按需读取）。</summary>
public sealed record SafeTensorEntry(string Name, string DType, long[] Shape, long Begin, long End)
{
    /// <summary>占用字节数。</summary>
    public long ByteLength => End - Begin;

    /// <summary>元素个数。</summary>
    public long ElementCount
    {
        get
        {
            long count = 1;
            foreach (long dim in Shape)
                count *= dim;
            return count;
        }
    }

    public override string ToString() => $"{Name}:{DType}[{string.Join(",", Shape)}]";
}

/// <summary>只读 safetensors 文件（按需定位读取，不整包映射进内存）。</summary>
public sealed class SafeTensorFile : IDisposable
{
    private const int MaxHeaderBytes = 256 * 1024 * 1024;

    private readonly FileStream _stream;
    private readonly Dictionary<string, SafeTensorEntry> _byName;
    private readonly long _dataStart;

    private SafeTensorFile(
        string path,
        FileStream stream,
        long dataStart,
        SafeTensorEntry[] entries,
        IReadOnlyDictionary<string, string> metadata)
    {
        Path = path;
        _stream = stream;
        _dataStart = dataStart;
        Tensors = entries;
        Metadata = metadata;

        _byName = new Dictionary<string, SafeTensorEntry>(entries.Length, StringComparer.Ordinal);
        foreach (SafeTensorEntry entry in entries)
            _byName[entry.Name] = entry;
    }

    /// <summary>文件路径。</summary>
    public string Path { get; }

    /// <summary>头部 <c>__metadata__</c>（没有则为空）。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }

    /// <summary>按头部声明顺序列出全部张量。</summary>
    public IReadOnlyList<SafeTensorEntry> Tensors { get; }

    /// <summary>打开文件（允许共享读）。</summary>
    public static SafeTensorFile Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length < 8)
                throw new InvalidDataException($"safetensors '{path}'：文件只有 {stream.Length} B，头部至少 8 B。");

            Span<byte> prefix = stackalloc byte[8];
            stream.ReadExactly(prefix);
            ulong headerLength = BinaryPrimitives.ReadUInt64LittleEndian(prefix);

            if (headerLength == 0 || headerLength > MaxHeaderBytes || 8L + (long)headerLength > stream.Length)
            {
                throw new InvalidDataException(
                    $"safetensors '{path}'：头部长度 {headerLength} 非法（文件 {stream.Length} B）。");
            }

            byte[] header = new byte[headerLength];
            stream.ReadExactly(header);

            long dataStart = 8 + (long)headerLength;
            long dataLength = stream.Length - dataStart;

            (SafeTensorEntry[] entries, Dictionary<string, string> metadata) = ParseHeader(path, header, dataLength);
            return new SafeTensorFile(path, stream, dataStart, entries, metadata);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public bool Contains(string name) => _byName.ContainsKey(name);

    /// <summary>按名字取元信息（缺失即抛）。</summary>
    public SafeTensorEntry Info(string name) => _byName.TryGetValue(name, out SafeTensorEntry? entry)
        ? entry
        : throw new KeyNotFoundException($"safetensors '{Path}'：没有名为 '{name}' 的张量。");

    /// <summary>读取原始字节。</summary>
    public byte[] ReadBytes(string name)
    {
        SafeTensorEntry entry = Info(name);
        var buffer = new byte[entry.ByteLength];
        _stream.Position = _dataStart + entry.Begin;
        _stream.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>按 dtype 解码成 F32（F16 / BF16 / F64 / 整型都支持）。</summary>
    public float[] ReadFloat32(string name)
    {
        SafeTensorEntry entry = Info(name);
        return SafeTensorDType.ToFloat32(entry.DType, ReadBytes(name), name);
    }

    public void Dispose() => _stream.Dispose();

    private static (SafeTensorEntry[] Entries, Dictionary<string, string> Metadata) ParseHeader(
        string path,
        byte[] header,
        long dataLength)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(header);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"safetensors '{path}'：头部不是合法 JSON。", exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"safetensors '{path}'：头部根节点必须是对象。");

            var entries = new List<SafeTensorEntry>();
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("__metadata__"))
                {
                    if (property.Value.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException($"safetensors '{path}'：__metadata__ 必须是对象。");

                    foreach (JsonProperty item in property.Value.EnumerateObject())
                        metadata[item.Name] = item.Value.GetString() ?? string.Empty;
                    continue;
                }

                JsonElement value = property.Value;
                string dtype = value.GetProperty("dtype").GetString()
                    ?? throw new InvalidDataException($"safetensors '{path}'：'{property.Name}' 缺 dtype。");

                if (!SafeTensorDType.IsKnown(dtype))
                {
                    throw new InvalidDataException(
                        $"safetensors '{path}'：'{property.Name}' 的 dtype '{dtype}' 不受支持。");
                }

                JsonElement shapeElement = value.GetProperty("shape");
                var shape = new long[shapeElement.GetArrayLength()];
                int shapeIndex = 0;
                foreach (JsonElement dim in shapeElement.EnumerateArray())
                    shape[shapeIndex++] = dim.GetInt64();

                JsonElement offsets = value.GetProperty("data_offsets");
                if (offsets.GetArrayLength() != 2)
                {
                    throw new InvalidDataException(
                        $"safetensors '{path}'：'{property.Name}' 的 data_offsets 必须是两个整数。");
                }

                long begin = offsets[0].GetInt64();
                long end = offsets[1].GetInt64();

                if (begin < 0 || end < begin || end > dataLength)
                {
                    throw new InvalidDataException(
                        $"safetensors '{path}'：'{property.Name}' 的 data_offsets [{begin},{end}) "
                        + $"越界（数据区 {dataLength} B）。");
                }

                long elementCount = 1;
                foreach (long dim in shape)
                    elementCount *= dim;

                long expected = elementCount * SafeTensorDType.SizeOf(dtype);
                if (expected != end - begin)
                {
                    throw new InvalidDataException(
                        $"safetensors '{path}'：'{property.Name}' 声明 {elementCount} × {dtype} = {expected} B，"
                        + $"实际偏移区间只有 {end - begin} B。");
                }

                entries.Add(new SafeTensorEntry(property.Name, dtype, shape, begin, end));
            }

            return (entries.ToArray(), metadata);
        }
    }
}
