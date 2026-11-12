namespace Sevencat.Core.Storage;

/// <summary>
/// 字节级存储抽象。标注、数据集、图片一律通过它读写，业务层不出现 <c>File.*</c>/<c>Directory.*</c>。
/// 首版实现 <see cref="LocalBlobStore"/>；S3 等后端实现同一接口即可。
/// </summary>
public interface IBlobStore
{
    string Scheme { get; }

    Task<bool> ExistsAsync(StorageKey key, CancellationToken ct = default);

    Task<BlobInfo?> StatAsync(StorageKey key, CancellationToken ct = default);

    Task<Stream> OpenReadAsync(StorageKey key, CancellationToken ct = default);

    /// <summary>按区间读取：本地 Seek，远端 Range GET（大图分块必需）。</summary>
    Task<Stream> OpenReadRangeAsync(StorageKey key, long offset, long length, CancellationToken ct = default);

    /// <summary>覆盖写，实现负责原子可见（本地 tmp+replace，S3 单对象 PUT）。</summary>
    Task WriteAsync(StorageKey key, Stream content, CancellationToken ct = default);

    Task DeleteAsync(StorageKey key, CancellationToken ct = default);

    /// <summary>按前缀递归列举文件（不含目录）。</summary>
    IAsyncEnumerable<BlobEntry> ListAsync(StorageKey prefix, CancellationToken ct = default);
}

/// <summary>按 scheme 创建存储实例。</summary>
public interface IBlobStoreFactory
{
    bool CanHandle(string scheme);

    IBlobStore Create(StorageLocation location);
}

/// <summary>把 <see cref="StorageLocation"/> 解析成对应的 <see cref="IBlobStore"/>，带实例缓存。</summary>
public sealed class BlobStoreRegistry(IEnumerable<IBlobStoreFactory> factories)
{
    private readonly IBlobStoreFactory[] _factories = [.. factories];

    public IBlobStore Get(StorageLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);

        foreach (IBlobStoreFactory factory in _factories)
        {
            if (factory.CanHandle(location.Scheme))
            {
                return factory.Create(location);
            }
        }

        throw new NotSupportedException($"未注册的存储后端：{location.Scheme}://");
    }

    /// <summary>解析 URI 字符串并返回对应存储。</summary>
    public IBlobStore Get(string uri) => Get(StorageLocation.Parse(uri));
}
