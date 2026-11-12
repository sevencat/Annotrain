using System.Text.Json;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Imaging;
using Sevencat.Core.Localization;
using Sevencat.Core.Storage;
using Sevencat.Label.Server.Data;
using Sevencat.Label.Server.Security;

namespace Sevencat.Label.Server;

/// <summary>单张图：元信息、图片字节、租约（claim / release）、跳过 / 退回、标注读写。</summary>
public static class ImageEndpoints
{
    /// <summary>缩略图长边默认像素。</summary>
    private const int DefaultThumbnailSize = 1024;

    private const int MinThumbnailSize = 32;

    private const int MaxThumbnailSize = 4096;

    public static IEndpointRouteBuilder MapImageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/images/{id:long}", async (
            long id,
            ServerOptions options,
            TinyLabelTaskService tasks,
            WebUserStore users,
            CancellationToken ct) =>
        {
            StorageLocation root = StorageLocation.FromPath(options.DataDirectory);
            TinyLabelImageTask? task = await tasks.GetAsync(root, id, ct);

            return task is null
                ? EndpointResults.NotFound("image")
                : Results.Ok(task.ToDto(users.FindById(task.AssigneeId ?? 0)?.DisplayName));
        });

        app.MapGet("/api/images/{id:long}/content", async (
            long id,
            ServerOptions options,
            TinyLabelTaskService tasks,
            BlobStoreRegistry registry,
            CancellationToken ct) => await ReadImageAsync(id, options, tasks, registry, ct));

        // 缩略图：按 ?size=（长边像素，默认 1024）等比缩小后编成 JPEG；原图本来就够小、
        // 没注册图像后端或解码失败时退回原图字节，保证前端列表永远有图。
        app.MapGet("/api/images/{id:long}/thumb", async (
            long id,
            int? size,
            ServerOptions options,
            TinyLabelTaskService tasks,
            BlobStoreRegistry registry,
            HttpContext context,
            CancellationToken ct) => await ReadThumbnailAsync(id, size, options, tasks, registry, context, ct));

        app.MapPost("/api/images/{id:long}/claim", async (
            long id,
            HttpContext context,
            ServerOptions options,
            TinyLabelTaskService tasks,
            WebUserStore users,
            CancellationToken ct) =>
        {
            StorageLocation root = StorageLocation.FromPath(options.DataDirectory);
            long userId = CurrentUser.Require(context);

            try
            {
                TinyLabelImageTask task = await tasks.ClaimAsync(root, id, userId, ct: ct);

                return Results.Ok(task.ToDto(users.FindById(task.AssigneeId ?? 0)?.DisplayName));
            }
            catch (TinyLabelImageLockedException ex)
            {
                return EndpointResults.Conflict(
                    ex.Message,
                    ex.Current.ToDto(users.FindById(ex.Current.AssigneeId ?? 0)?.DisplayName));
            }
            catch (KeyNotFoundException)
            {
                return EndpointResults.NotFound("image");
            }
        });

        app.MapPost("/api/images/{id:long}/release", async (
            long id,
            HttpContext context,
            ServerOptions options,
            TinyLabelTaskService tasks,
            CancellationToken ct) =>
        {
            StorageLocation root = StorageLocation.FromPath(options.DataDirectory);
            long userId = CurrentUser.Require(context);

            await tasks.ReleaseAsync(root, id, userId, ct);

            return Results.NoContent();
        });

        app.MapPost("/api/images/{id:long}/skip", async (
            long id,
            HttpContext context,
            ServerOptions options,
            TinyLabelTaskService tasks,
            WebUserStore users,
            CancellationToken ct) => await ChangeStateAsync(id, skip: true, context, options, tasks, users, ct));

        app.MapPost("/api/images/{id:long}/return", async (
            long id,
            HttpContext context,
            ServerOptions options,
            TinyLabelTaskService tasks,
            WebUserStore users,
            CancellationToken ct) => await ChangeStateAsync(id, skip: false, context, options, tasks, users, ct));

        app.MapGet("/api/images/{id:long}/annotation", async (
            long id,
            ServerOptions options,
            TinyLabelTaskService tasks,
            CancellationToken ct) =>
        {
            StorageLocation root = StorageLocation.FromPath(options.DataDirectory);
            TinyLabelAnnotationDoc? doc = await tasks.GetAnnotationAsync(root, id, ct);

            return doc is null
                ? EndpointResults.NotFound("annotation")
                : Results.Ok(ToDto(doc));
        });

        app.MapPut("/api/images/{id:long}/annotation", async (
            long id,
            SaveAnnotationRequest request,
            HttpContext context,
            ServerOptions options,
            TinyLabelTaskService tasks,
            CancellationToken ct) =>
        {
            StorageLocation root = StorageLocation.FromPath(options.DataDirectory);
            long userId = CurrentUser.Require(context);

            try
            {
                TinyLabelAnnotationDoc doc = await tasks.SaveAnnotationAsync(
                    root, id, request.Revision, request.DocJson, userId, ct);

                return Results.Ok(ToDto(doc));
            }
            catch (TinyLabelRevisionConflictException ex)
            {
                TinyLabelImageTask? current = await tasks.GetAsync(root, ex.ImageId, ct);

                return EndpointResults.Conflict(ex.Message, current?.ToDto());
            }
            catch (KeyNotFoundException)
            {
                return EndpointResults.NotFound("image");
            }
            catch (InvalidDataException ex)
            {
                return EndpointResults.Error(StatusCodes.Status400BadRequest, "invalid_document", ex.Message);
            }
        });

        return app;
    }

    private static async Task<IResult> ChangeStateAsync(
        long id,
        bool skip,
        HttpContext context,
        ServerOptions options,
        TinyLabelTaskService tasks,
        WebUserStore users,
        CancellationToken ct)
    {
        StorageLocation root = StorageLocation.FromPath(options.DataDirectory);
        long userId = CurrentUser.Require(context);
        string? remark = await ReadRemarkAsync(context.Request, ct);

        try
        {
            TinyLabelImageTask task = skip
                ? await tasks.SkipAsync(root, id, userId, remark, ct)
                : await tasks.ReturnToPoolAsync(root, id, userId, remark, ct);

            return Results.Ok(task.ToDto(users.FindById(task.AssigneeId ?? 0)?.DisplayName));
        }
        catch (KeyNotFoundException)
        {
            return EndpointResults.NotFound("image");
        }
    }

    private static async Task<IResult> ReadImageAsync(
        long id,
        ServerOptions options,
        TinyLabelTaskService tasks,
        BlobStoreRegistry registry,
        CancellationToken ct)
    {
        StorageLocation root = StorageLocation.FromPath(options.DataDirectory);
        TinyLabelImageTask? task = await tasks.GetAsync(root, id, ct);

        return task is null
            ? EndpointResults.NotFound("image")
            : await SendOriginalAsync(id, root, task, registry, ct);
    }

    /// <summary>
    /// 缩略图：长边压到 <paramref name="size"/>（默认 1024），等比、不放大，输出 JPEG。
    /// 生成结果落在项目的 <c>thumbs/</c> 目录（与桌面端共用一份缓存）；远端图或生成失败时退回原图。
    /// </summary>
    private static async Task<IResult> ReadThumbnailAsync(
        long id,
        int? size,
        ServerOptions options,
        TinyLabelTaskService tasks,
        BlobStoreRegistry registry,
        HttpContext context,
        CancellationToken ct)
    {
        StorageLocation root = StorageLocation.FromPath(options.DataDirectory);
        TinyLabelImageTask? task = await tasks.GetAsync(root, id, ct);

        if (task is null)
        {
            return EndpointResults.NotFound("image");
        }

        int target = Math.Clamp(size ?? DefaultThumbnailSize, MinThumbnailSize, MaxThumbnailSize);

        string? thumbPath = await ThumbnailCache
            .EnsureAsync(root, task.ProjectId, task.RelPath, task.FilePath, target, ct)
            .ConfigureAwait(false);

        if (thumbPath is null)
        {
            return await SendOriginalAsync(id, root, task, registry, ct);
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(thumbPath, ct).ConfigureAwait(false);

            context.Response.Headers.CacheControl = "private, max-age=86400";

            return Results.Bytes(bytes, "image/jpeg");
        }
        catch (IOException)
        {
            return await SendOriginalAsync(id, root, task, registry, ct);
        }
    }

    private static async Task<IResult> SendOriginalAsync(
        long id,
        StorageLocation root,
        TinyLabelImageTask task,
        BlobStoreRegistry registry,
        CancellationToken ct)
    {
        IBlobStore store = registry.Get(root);
        string relative = Path.GetRelativePath(root.Root, task.FilePath).Replace('\\', '/');

        try
        {
            await using Stream stream = await store.OpenReadAsync(new StorageKey(root, relative), ct);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);

            return Results.Bytes(buffer.ToArray(), ContentTypeOf(task.FileName));
        }
        catch (FileNotFoundException)
        {
            return EndpointResults.NotFound(Text.Format("Error.ImageNotFound", id));
        }
        catch (DirectoryNotFoundException)
        {
            return EndpointResults.NotFound(Text.Format("Error.ImageNotFound", id));
        }
    }

    /// <summary>备注是可选字段，空 body / 非法 JSON 都按"没填备注"处理，不要因此 400。</summary>
    private static async Task<string?> ReadRemarkAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is null or 0)
        {
            return null;
        }

        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);

            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("remark", out JsonElement remark)
                   && remark.ValueKind == JsonValueKind.String
                ? remark.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AnnotationDto ToDto(TinyLabelAnnotationDoc doc)
        => new(doc.ImageId, doc.Revision, doc.DocJson, doc.UpdatedAt, doc.UpdatedBy);

    private static string ContentTypeOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".bmp" => "image/bmp",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".tif" or ".tiff" => "image/tiff",
        _ => "image/jpeg",
    };
}
