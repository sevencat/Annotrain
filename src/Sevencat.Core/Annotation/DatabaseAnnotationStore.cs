using System.Text;
using FreeSql;
using Sevencat.Core.Annotation.Formats;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Localization;
using Sevencat.Core.Model;

namespace Sevencat.Core.Annotation;

/// <summary>
/// 数据库式标注存储：`annotations.DocJson` 存 labelme JSON 原文，一图一行。
/// 乐观锁用 `images.Revision`（与 `annotations.Revision` 同步递增），保存是「查版本 → 写 JSON」的小事务。
/// </summary>
public sealed class DatabaseAnnotationStore : IAnnotationStore
{
    private readonly IFreeSql _orm;
    private readonly long _projectId;
    private readonly ClassCatalog _classes;
    private readonly LabelmeCodec _codec = new();

    /// <summary>
    /// 用容器里的当前工作库连接（<c>AddAnnotrainDatabase</c> 注册的那个单例）。
    /// 只借用不持有：库的打开与释放归注册方，这里不再 <c>Dispose</c>。
    /// </summary>
    public DatabaseAnnotationStore(IFreeSql orm, long projectId)
    {
        ArgumentNullException.ThrowIfNull(orm);

        _orm = orm;
        _projectId = projectId;
        _classes = TinyLabelQueries.LoadClasses(_orm, projectId);
    }

    public async Task<bool> ExistsAsync(ImageRef image, CancellationToken ct = default)
    {
        ImageEntity? entity = TinyLabelQueries.FindImage(_orm, _projectId, image);
        if (entity is null)
        {
            return false;
        }

        AnnotationEntity? row = _orm.Select<AnnotationEntity>()
            .Where(annotation => annotation.ImageId == entity.Id)
            .First();

        return row is not null && !string.IsNullOrWhiteSpace(row.DocJson);
    }

    public async Task<AnnotationEnvelope?> LoadAsync(ImageRef image, CancellationToken ct = default)
    {
        ImageEntity? entity = TinyLabelQueries.FindImage(_orm, _projectId, image);
        if (entity is null)
        {
            return null;
        }

        AnnotationEntity? row = _orm.Select<AnnotationEntity>()
            .Where(annotation => annotation.ImageId == entity.Id)
            .First();

        if (row is null || string.IsNullOrWhiteSpace(row.DocJson))
        {
            return null;
        }

        AnnotationParseResult parsed = Parse(row.DocJson, entity);
        await Task.CompletedTask.ConfigureAwait(false);

        // 标注文档在库里的定位就是图片 Id（一图一行）
        return new AnnotationEnvelope(
            parsed.Document,
            entity.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            parsed.Warnings);
    }

    public async Task<AnnotationEnvelope> SaveAsync(
        ImageRef image,
        AnnotationDocument document,
        int? expectedRevision = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        ImageEntity entity = TinyLabelQueries.FindImage(_orm, _projectId, image)
            ?? throw new InvalidOperationException(Text.Format("Error.ImageNotInDatabase", image.RelativePath));

        int currentRevision = entity.Revision;
        if (expectedRevision is { } expected && currentRevision != 0 && expected != currentRevision)
        {
            throw new AnnotationConcurrencyException(expected, currentRevision);
        }

        int nextRevision = currentRevision + 1;
        DateTime now = DateTime.UtcNow;

        AnnotationDocument toWrite = document with
        {
            ImagePath = string.IsNullOrWhiteSpace(document.ImagePath) ? entity.RelPath : document.ImagePath,
            ImageWidth = entity.Width,
            ImageHeight = entity.Height,
            Meta = document.Meta with
            {
                Revision = nextRevision,
                UpdatedAt = new DateTimeOffset(now, TimeSpan.Zero),
            },
        };

        using var buffer = new MemoryStream();
        _codec.Write(buffer, toWrite, new AnnotationWriteContext(entity.RelPath, entity.Width, entity.Height, _classes));
        string json = Encoding.UTF8.GetString(buffer.ToArray());

        _orm.Ado.Transaction(() =>
        {
            _orm.Update<ImageEntity>()
                .Set(row => row.Revision, nextRevision)
                .Set(row => row.UpdatedAt, now)
                .Where(row => row.Id == entity.Id)
                .ExecuteAffrows();

            // 保存标注即视为"已标注"：列表里的状态列要跟着变，不能一直停在 pending。
            // 已通过 / 已排除 / 已跳过 是人工状态，保存标注不回退它们。
            _orm.Update<ImageEntity>()
                .Set(row => row.State, "labeled")
                .Where(row => row.Id == entity.Id && row.State == "pending")
                .ExecuteAffrows();

            int updated = _orm.Update<AnnotationEntity>()
                .Set(row => row.DocJson, json)
                .Set(row => row.Revision, nextRevision)
                .Set(row => row.UpdatedAt, now)
                .Where(row => row.ImageId == entity.Id)
                .ExecuteAffrows();

            if (updated == 0)
            {
                _orm.Insert(new AnnotationEntity
                {
                    ImageId = entity.Id,
                    ProjectId = _projectId,
                    Revision = nextRevision,
                    DocJson = json,
                    // 桌面端单机没有用户体系，保存者固定 0（与 TinyLabel.Server 的 long 用户 Id 对齐）
                    UpdatedBy = 0,
                    UpdatedAt = now,
                }).ExecuteAffrows();
            }
        });

        await Task.CompletedTask.ConfigureAwait(false);

        return new AnnotationEnvelope(
            toWrite,
            entity.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            []);
    }

    private AnnotationParseResult Parse(string json, ImageEntity entity)
    {
        var info = new ImageInfo
        {
            RelativePath = entity.RelPath,
            Width = entity.Width,
            Height = entity.Height,
        };

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return _codec.Read(stream, new AnnotationReadContext(info, _classes));
    }
}
