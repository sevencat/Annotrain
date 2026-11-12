using Sevencat.Core.Annotation;
using Sevencat.Core.Imaging;
using Sevencat.Core.Model;
using Sevencat.Core.Storage;

namespace Sevencat.Label;

/// <summary>
/// 文件式标注存储：按 <see cref="IAnnotationPathRule"/> 定位文件，按 <see cref="IAnnotationCodec"/> 编解码。
/// 整个类只依赖 <see cref="IBlobStore"/>，本地与 S3 通用。
/// </summary>
public sealed class CodecAnnotationStore(
    IBlobStore store,
    IAnnotationCodec codec,
    IAnnotationPathRule pathRule,
    IImageInfoSource imageInfo,
    Func<ClassCatalog> classes) : IAnnotationStore
{
    public async Task<bool> ExistsAsync(ImageRef image, CancellationToken ct = default)
    {
        StorageKey key = pathRule.Resolve(image, codec);
        return await store.ExistsAsync(key, ct).ConfigureAwait(false);
    }

    public async Task<AnnotationEnvelope?> LoadAsync(ImageRef image, CancellationToken ct = default)
    {
        StorageKey key = pathRule.Resolve(image, codec);

        if (!await store.ExistsAsync(key, ct).ConfigureAwait(false))
        {
            return null;
        }

        ImageInfo? info = await imageInfo.ReadAsync(image, ct).ConfigureAwait(false);
        if (info is null)
        {
            return null;
        }

        await using Stream stream = await store.OpenReadAsync(key, ct).ConfigureAwait(false);
        AnnotationParseResult parsed = codec.Read(stream, new AnnotationReadContext(info, classes()));

        return new AnnotationEnvelope(parsed.Document, key.ToString(), parsed.Warnings);
    }

    public async Task<AnnotationEnvelope> SaveAsync(
        ImageRef image,
        AnnotationDocument document,
        int? expectedRevision = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        StorageKey key = pathRule.Resolve(image, codec);

        int currentRevision = 0;
        if (await store.ExistsAsync(key, ct).ConfigureAwait(false))
        {
            await using Stream existing = await store.OpenReadAsync(key, ct).ConfigureAwait(false);
            ImageInfo? info = await imageInfo.ReadAsync(image, ct).ConfigureAwait(false);

            if (info is not null)
            {
                AnnotationParseResult previous = codec.Read(existing, new AnnotationReadContext(info, classes()));
                currentRevision = previous.Document.Meta.Revision;
            }
        }

        if (expectedRevision is { } expected && currentRevision != 0 && expected != currentRevision)
        {
            throw new AnnotationConcurrencyException(expected, currentRevision);
        }

        ImageInfo? imageInfoNow = await imageInfo.ReadAsync(image, ct).ConfigureAwait(false);
        if (imageInfoNow is null)
        {
            throw new InvalidOperationException($"无法读取图片信息：{image}");
        }

        AnnotationDocument toWrite = document with
        {
            ImagePath = string.IsNullOrWhiteSpace(document.ImagePath) ? image.RelativePath : document.ImagePath,
            ImageWidth = imageInfoNow.Width,
            ImageHeight = imageInfoNow.Height,
            Meta = document.Meta with
            {
                Revision = currentRevision + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
        };

        using var buffer = new MemoryStream();
        codec.Write(buffer, toWrite, new AnnotationWriteContext(
            image.RelativePath, imageInfoNow.Width, imageInfoNow.Height, classes()));

        buffer.Position = 0;
        await store.WriteAsync(key, buffer, ct).ConfigureAwait(false);

        return new AnnotationEnvelope(toWrite, key.ToString(), []);
    }
}
