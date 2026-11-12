using System.Globalization;

namespace Sevencat.Train.Engine.Data.Yaml;

/// <summary>
/// 面向 <c>data.yaml</c> 的 YAML 子集解析器。
/// <para>
/// 只支持实际用到的写法：<c>key: value</c>、嵌套映射、<c>- item</c> 列表、行内 <c>[a, b]</c>、
/// 单/双引号、注释。返回 字典 / 列表 / 字符串。
/// 不支持的复杂语法（锚点、多行文本、flow map）会抛 <see cref="FormatException"/>，不做猜测。
/// </para>
/// </summary>
public static class MiniYaml
{
    public static IReadOnlyDictionary<string, object?> ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllLines(path));
    }

    public static IReadOnlyDictionary<string, object?> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var normalized = new List<string>();
        foreach (string raw in lines)
        {
            string line = StripComment(raw).TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.Contains('\t'))
                throw new FormatException("YAML：不允许用制表符缩进，请改用空格。");

            normalized.Add(line);
        }

        if (normalized.Count == 0)
            return new Dictionary<string, object?>(StringComparer.Ordinal);

        int index = 0;
        Dictionary<string, object?> map = ParseMap(normalized, ref index, IndentOf(normalized[0]));

        if (index < normalized.Count)
            throw new FormatException($"YAML：第 {index + 1} 行缩进不一致。");

        return map;
    }

    private static Dictionary<string, object?> ParseMap(List<string> lines, ref int index, int indent)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);

        while (index < lines.Count && IndentOf(lines[index]) == indent)
        {
            string line = lines[index].Trim();
            if (line.StartsWith("- ", StringComparison.Ordinal))
                break;

            (string key, string rawValue) = SplitKey(line, index);
            index++;

            if (rawValue.Length > 0)
            {
                map[key] = ParseScalar(rawValue);
                continue;
            }

            if (index < lines.Count && IndentOf(lines[index]) > indent)
            {
                int childIndent = IndentOf(lines[index]);
                map[key] = lines[index].TrimStart().StartsWith("- ", StringComparison.Ordinal)
                    ? ParseSequence(lines, ref index, childIndent)
                    : ParseMap(lines, ref index, childIndent);
            }
            else
            {
                map[key] = null;
            }
        }

        return map;
    }

    private static List<object?> ParseSequence(List<string> lines, ref int index, int indent)
    {
        var items = new List<object?>();

        while (index < lines.Count && IndentOf(lines[index]) == indent)
        {
            string line = lines[index].Trim();
            if (!line.StartsWith("- ", StringComparison.Ordinal))
                break;

            items.Add(ParseScalar(line[2..].Trim()));
            index++;
        }

        return items;
    }

    private static object ParseScalar(string text)
    {
        if (text.Length >= 2 && text[0] == '[' && text[^1] == ']')
        {
            string inner = text[1..^1].Trim();
            return inner.Length == 0
                ? new List<object?>()
                : SplitInlineList(inner).Select(Unquote).Cast<object?>().ToList();
        }

        return Unquote(text);
    }

    private static IEnumerable<string> SplitInlineList(string text)
    {
        var parts = new List<string>();
        int start = 0;
        char quote = '\0';

        for (int i = 0; i < text.Length; i++)
        {
            char current = text[i];
            if (quote != '\0')
            {
                if (current == quote)
                    quote = '\0';
                continue;
            }

            if (current is '\'' or '"')
            {
                quote = current;
                continue;
            }

            if (current == ',')
            {
                parts.Add(text[start..i].Trim());
                start = i + 1;
            }
        }

        parts.Add(text[start..].Trim());
        return parts.Where(part => part.Length > 0);
    }

    private static string Unquote(string text) =>
        text.Length >= 2 && text[0] == text[^1] && text[0] is '\'' or '"' ? text[1..^1] : text;

    private static (string Key, string Value) SplitKey(string line, int lineIndex)
    {
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char current = line[i];
            if (quote != '\0')
            {
                if (current == quote)
                    quote = '\0';
                continue;
            }

            if (current is '\'' or '"')
            {
                quote = current;
                continue;
            }

            if (current == ':')
            {
                string key = Unquote(line[..i].Trim());
                if (key.Length == 0)
                    throw new FormatException($"YAML：第 {lineIndex + 1} 行键为空。");
                return (key, line[(i + 1)..].Trim());
            }
        }

        throw new FormatException(
            $"YAML：第 {lineIndex + 1} 行缺少 ':'（只支持 key: value 形式）：{line}");
    }

    private static int IndentOf(string line)
    {
        int indent = 0;
        while (indent < line.Length && line[indent] == ' ')
            indent++;
        return indent;
    }

    private static string StripComment(string line)
    {
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char current = line[i];
            if (quote != '\0')
            {
                if (current == quote)
                    quote = '\0';
                continue;
            }

            if (current is '\'' or '"')
            {
                quote = current;
                continue;
            }

            if (current == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1])))
                return line[..i];
        }

        return line;
    }

    internal static string AsString(object? value, string key)
    {
        if (value is string text)
            return text;
        if (value is null)
            throw new FormatException($"YAML：'{key}' 没有值。");
        throw new FormatException($"YAML：'{key}' 不是标量（实际是 {value.GetType().Name}）。");
    }

    internal static int AsInt(object? value, string key)
    {
        string text = AsString(value, key);
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
            throw new FormatException($"YAML：'{key}' 不是整数（'{text}'）。");
        return result;
    }
}
