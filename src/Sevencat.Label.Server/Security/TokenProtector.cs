using System.Security.Cryptography;
using System.Text;

namespace Sevencat.Label.Server.Security;

/// <summary>
/// 自签的登录令牌：<c>base64url(userId.expiresAtUnix).base64url(HMAC-SHA256)</c>。
/// 无状态、不用发 DB 查询；密钥首次启动生成在数据目录里，删掉即等于把所有人踢下线。
/// </summary>
public sealed class TokenProtector
{
    private readonly byte[] _key;
    private readonly TimeSpan _lifetime;

    public TokenProtector(string keyPath, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);

        _key = LoadOrCreateKey(keyPath);
        _lifetime = lifetime;
    }

    public (string Token, DateTimeOffset ExpiresAt) Issue(long userId)
    {
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.Add(_lifetime);
        string payload = $"{userId}.{expiresAt.ToUnixTimeSeconds()}";

        return ($"{Base64Url.Encode(payload)}.{Sign(payload)}", expiresAt);
    }

    /// <summary>验签 + 过期检查；不合法一律返回 <c>null</c>。</summary>
    public long? Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        int separator = token.IndexOf('.');

        if (separator <= 0 || separator == token.Length - 1)
        {
            return null;
        }

        byte[]? payloadBytes = Base64Url.TryDecode(token[..separator]);
        byte[]? signature = Base64Url.TryDecode(token[(separator + 1)..]);

        if (payloadBytes is null || signature is null)
        {
            return null;
        }

        byte[] expected = HMACSHA256.HashData(_key, payloadBytes);

        if (signature.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(signature, expected))
        {
            return null;
        }

        string[] parts = Encoding.UTF8.GetString(payloadBytes).Split('.');

        if (parts.Length != 2
            || !long.TryParse(parts[0], out long userId)
            || !long.TryParse(parts[1], out long expiresUnix))
        {
            return null;
        }

        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expiresUnix ? null : userId;
    }

    private string Sign(string payload)
        => Base64Url.Encode(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload)));

    private static byte[] LoadOrCreateKey(string path)
    {
        if (File.Exists(path))
        {
            byte[] existing = Convert.FromBase64String(File.ReadAllText(path).Trim());

            if (existing.Length >= 32)
            {
                return existing;
            }
        }

        byte[] created = RandomNumberGenerator.GetBytes(32);

        string? directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, Convert.ToBase64String(created));

        return created;
    }
}
