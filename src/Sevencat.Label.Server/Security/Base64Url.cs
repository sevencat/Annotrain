using System.Text;

namespace Sevencat.Label.Server.Security;

/// <summary>URL 安全的 base64（无填充）：令牌要能塞进 query string。</summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Encode(string text) => Encode(Encoding.UTF8.GetBytes(text));

    public static byte[]? TryDecode(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        string normalized = text.Replace('-', '+').Replace('_', '/');

        normalized = (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            1 => normalized,
            _ => normalized,
        };

        try
        {
            return Convert.FromBase64String(normalized);
        }
        catch (FormatException)
        {
            return null;
        }
    }

}
