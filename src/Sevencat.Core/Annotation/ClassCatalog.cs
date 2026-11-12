namespace Sevencat.Core.Annotation;

/// <summary>类别表：下标即 classId（YOLO/COCO 共用的顺序语义）。</summary>
public sealed class ClassCatalog
{
    public static ClassCatalog Empty { get; } = new([]);

    private readonly List<string> _names;
    private readonly Dictionary<string, int> _index;

    private ClassCatalog(IEnumerable<string> names)
    {
        _names = [.. names];
        _index = new Dictionary<string, int>(_names.Count, StringComparer.Ordinal);

        for (int i = 0; i < _names.Count; i++)
        {
            _index.TryAdd(_names[i], i);
        }
    }

    public IReadOnlyList<string> Names => _names;

    public int Count => _names.Count;

    public static ClassCatalog FromNames(IEnumerable<string> names) => new(names);

    /// <summary>按名字取下标；不存在返回 -1。</summary>
    public int IndexOf(string? name)
        => name is not null && _index.TryGetValue(name, out int index) ? index : -1;

    /// <summary>按下标取名字；越界返回空串（避免读脏数据时崩溃）。</summary>
    public string NameOf(int index) => index >= 0 && index < _names.Count ? _names[index] : string.Empty;

    /// <summary>把未知类别补齐到末尾（导入外部数据集时用），返回新实例与本次新增的名字。</summary>
    public (ClassCatalog Catalog, IReadOnlyList<string> Added) WithMissing(IEnumerable<string> names)
    {
        var result = new List<string>(_names);
        var added = new List<string>();

        foreach (string name in names)
        {
            if (!result.Contains(name, StringComparer.Ordinal))
            {
                result.Add(name);
                added.Add(name);
            }
        }

        return added.Count == 0 ? (this, added) : (new ClassCatalog(result), added);
    }
}
