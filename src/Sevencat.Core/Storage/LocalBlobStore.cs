using Sevencat.Core.Localization;

namespace Sevencat.Core.Storage;

/// <summary>
/// 本地文件系统后端。整个解决方案里唯一允许出现 <c>File.*</c>/<c>Directory.*</c> 的地方。
/// </summary>
public sealed class LocalBlobStore(StorageLocation location) : IBlobStore
{
    public string Scheme => StorageLocation.FileScheme;

    public StorageLocation Location { get; } = location;

    public string Root => Location.Root;

    public Task<bool> ExistsAsync(StorageKey key, CancellationToken ct = default)
        => Task.FromResult(File.Exists(Resolve(key)) || Directory.Exists(Resolve(key)));

    public Task<BlobInfo?> StatAsync(StorageKey key, CancellationToken ct = default)
    {
        var file = new FileInfo(Resolve(key));
        return Task.FromResult<BlobInfo?>(
            file.Exists ? new BlobInfo(file.Length, file.LastWriteTimeUtc) : null);
    }

    public Task<Stream> OpenReadAsync(StorageKey key, CancellationToken ct = default)
        => Task.FromResult<Stream>(new FileStream(
            Resolve(key), FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));

    public Task<Stream> OpenReadRangeAsync(StorageKey key, long offset, long length, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        var stream = new FileStream(
            Resolve(key), FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);

        if (offset > 0)
        {
            if (offset >= stream.Length)
            {
                stream.Dispose();
                return Task.FromResult<Stream>(Stream.Null);
            }

            stream.Seek(offset, SeekOrigin.Begin);
        }

        return Task.FromResult<Stream>(new LimitedStream(stream, length));
    }

    public async Task WriteAsync(StorageKey key, Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        string path = Resolve(key);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        await AtomicFile.WriteAllBytesAsync(path, buffer.ToArray(), ct).ConfigureAwait(false);
    }

    public Task DeleteAsync(StorageKey key, CancellationToken ct = default)
    {
        AtomicFile.TryDelete(Resolve(key));
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<BlobEntry> ListAsync(
        StorageKey prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        string directory = Resolve(prefix);
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var info = new FileInfo(file);
            string relative = Path.GetRelativePath(Root, file).Replace('\\', '/');
            yield return new BlobEntry(relative, info.Length, info.LastWriteTimeUtc);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>相对路径 → 绝对路径，并阻止跳出根目录。</summary>
    internal string Resolve(StorageKey key)
        => Resolve(key.NormalizedPath);

    internal string Resolve(string relativePath)
    {
        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(Root, normalized));

        if (!full.StartsWith(Root, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, Root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(Text.Format("Error.PathOutsideRoot", relativePath));
        }

        return full;
    }

    public static LocalBlobStore Create(string path) => new(StorageLocation.FromPath(path));

    /// <summary>只读取前 n 个字节的流包装（探测文件头用）。</summary>
    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long _remaining = limit;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            int want = (int)Math.Min(buffer.Length, _remaining);
            int read = inner.Read(buffer[..want]);
            _remaining -= read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => ReadAsyncCore(buffer, ct);

        private async ValueTask<int> ReadAsyncCore(Memory<byte> buffer, CancellationToken ct)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            int want = (int)Math.Min(buffer.Length, _remaining);
            int read = await inner.ReadAsync(buffer[..want], ct).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>本地后端工厂。</summary>
public sealed class LocalBlobStoreFactory : IBlobStoreFactory
{
    public bool CanHandle(string scheme) => string.Equals(scheme, StorageLocation.FileScheme, StringComparison.OrdinalIgnoreCase);

    public IBlobStore Create(StorageLocation location) => new LocalBlobStore(location);
}
