using System.Text;

namespace Sevencat.Core.Storage;

/// <summary>
/// 原子写文件：先写同目录临时文件并落盘，再整体替换。
/// 崩溃时最多留下 <c>.*.tmp</c>，不会出现半截主文件。
/// 只允许被 <see cref="LocalBlobStore"/> 使用，业务层不得直接调用。
/// </summary>
internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> content, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException($"无法确定目录：{path}", nameof(path));
        Directory.CreateDirectory(directory);

        string tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static byte[] Utf8Bytes(string content) => Utf8NoBom.GetBytes(content);
}
