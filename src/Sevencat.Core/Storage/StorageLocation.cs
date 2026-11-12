namespace Sevencat.Core.Storage;

/// <summary>
/// 存储位置：<c>scheme://root</c>。首版只有 <c>file</c>，后续接 <c>s3</c>/<c>http</c> 不改上层。
/// 不带 scheme 的路径按本地文件处理。
/// </summary>
public sealed record StorageLocation(string Scheme, string Root)
{
    public const string FileScheme = "file";

    public static StorageLocation FromPath(string path)
        => new(FileScheme, NormalizeLocalPath(path));

    /// <summary>解析 <c>file:///D:/data</c>、<c>D:\data</c>、<c>s3://bucket/prefix</c>。</summary>
    public static StorageLocation Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        int separator = value.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0)
        {
            return FromPath(value);
        }

        string scheme = value[..separator].ToLowerInvariant();
        string root = value[(separator + 3)..];

        return scheme == FileScheme
            ? new StorageLocation(scheme, NormalizeLocalPath(root))
            : new StorageLocation(scheme, root.TrimEnd('/'));
    }

    public bool IsFile => string.Equals(Scheme, FileScheme, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{Scheme}://{Root}";

    private static string NormalizeLocalPath(string path)
    {
        // file:///D:/data 会带来前导斜杠；Windows 下 "C:\x" 与 "C:/x" 都接受
        if (path.Length > 2 && path[0] == '/' && path[2] == ':')
        {
            path = path[1..];
        }

        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

/// <summary>某个存储位置下的相对路径。</summary>
public sealed record StorageKey(StorageLocation Location, string RelativePath)
{
    /// <summary>统一用 <c>/</c> 分隔，便于跨后端与跨平台。</summary>
    public string NormalizedPath => RelativePath.Replace('\\', '/').TrimStart('/');

    public string FileName => Path.GetFileName(NormalizedPath);

    public string Extension => Path.GetExtension(NormalizedPath);

    public StorageKey Combine(string child)
        => this with { RelativePath = $"{NormalizedPath.TrimEnd('/')}/{child.TrimStart('/')}" };

    public override string ToString() => $"{Location}/{NormalizedPath}";
}

public sealed record BlobInfo(long Length, DateTimeOffset? LastModified);

public sealed record BlobEntry(string RelativePath, long Length, DateTimeOffset? LastModified)
{
    public bool IsImage => Model.ImageFiles.IsSupported(RelativePath);
}
