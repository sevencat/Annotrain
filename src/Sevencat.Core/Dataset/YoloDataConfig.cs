namespace Sevencat.Core.Dataset;

/// <summary>
/// 极简 YAML 读取：只解析 data.yaml 需要的字段（path / train / val / test / names）。
/// 不追求完整 YAML 语义，遇到没见过的东西就忽略，避免为了一个配置文件引入解析器。
/// </summary>
public sealed class YoloDataConfig
{
    private YoloDataConfig(string? root, string? train, string? val, string? test, IReadOnlyList<string> names)
    {
        Root = root;
        Train = train;
        Val = val;
        Test = test;
        Names = names;
    }

    public string? Root { get; }

    public string? Train { get; }

    public string? Val { get; }

    public string? Test { get; }

    public IReadOnlyList<string> Names { get; }

    public bool IsValid => Names.Count > 0 && !string.IsNullOrWhiteSpace(Train);

    public static YoloDataConfig Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string? root = null, train = null, val = null, test = null;
        var namesByIndex = new SortedDictionary<int, string>();
        var namesList = new List<string>();

        bool inNames = false;
        int namesIndent = -1;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            int indent = line.Length - line.TrimStart().Length;
            string content = line.Trim();

            if (inNames)
            {
                if (indent > namesIndent)
                {
                    if (content.StartsWith("- ", StringComparison.Ordinal))
                    {
                        namesList.Add(Unquote(content[2..]));
                        continue;
                    }

                    int colon = content.IndexOf(':');
                    if (colon > 0 && int.TryParse(content[..colon].Trim(), out int index))
                    {
                        namesByIndex[index] = Unquote(content[(colon + 1)..]);
                        continue;
                    }
                }

                inNames = false;
            }

            int separator = content.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            string key = content[..separator].Trim().ToLowerInvariant();
            string value = content[(separator + 1)..].Trim();

            switch (key)
            {
                case "path":
                    root = Unquote(value);
                    break;
                case "train":
                    train = Unquote(value);
                    break;
                case "val":
                    val = Unquote(value);
                    break;
                case "test":
                    test = Unquote(value);
                    break;
                case "names":
                    if (value.StartsWith("[", StringComparison.Ordinal))
                    {
                        namesList.AddRange(ParseInlineList(value));
                    }
                    else
                    {
                        inNames = true;
                        namesIndent = indent;
                    }

                    break;
            }
        }

        IReadOnlyList<string> names = namesByIndex.Count > 0
            ? [.. namesByIndex.Values]
            : namesList;

        return new YoloDataConfig(root, train, val, test, names);
    }

    private static IEnumerable<string> ParseInlineList(string value)
    {
        string inner = value.Trim().TrimStart('[').TrimEnd(']');

        foreach (string part in inner.Split(','))
        {
            string item = Unquote(part.Trim());
            if (item.Length > 0)
            {
                yield return item;
            }
        }
    }

    private static string Unquote(string value)
        => value.Trim().Trim('"').Trim('\'').Trim();
}
