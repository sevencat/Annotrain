using System.IO.Compression;
using System.Reflection;

namespace Sevencat.Label.Server;

/// <summary>
/// 内嵌页面：<c>WebUI/index.html.gz</c>（前端 <c>npm run build:server</c> 产出）作为程序集资源，
/// 客户端支持 gzip 就直接发压缩体，否则解压后发明文。带 <see cref="ServerOptions.WebPageDirectory"/>
/// 时改读磁盘，便于边改前端边刷新，不用重编译 C#。
/// </summary>
public static class WebPageEndpoints
{
    private static readonly Lazy<byte[]?> PageBytes = new(() => ReadResource("index.html.gz"));
    private static readonly Lazy<byte[]?> IconBytes = new(() => ReadResource("favicon.ico"));

    public static IEndpointRouteBuilder MapWebPageEndpoints(this IEndpointRouteBuilder app, ServerOptions options)
    {
        app.MapGet("/", (Func<HttpContext, Task<IResult>>)(context => ServePageAsync(context, options)));

        app.MapGet("/favicon.ico", () => ServeIconAsync(options));

        // SPA 走 hash 路由，深链不会打到服务端；这里兜一层：非 /api 的 GET 一律回页面。
        app.MapFallback((Func<HttpContext, Task<IResult>>)(context =>
            context.Request.Path.StartsWithSegments("/api")
                ? Task.FromResult(EndpointResults.NotFound("api"))
                : ServePageAsync(context, options)));

        return app;
    }

    private static async Task<IResult> ServePageAsync(HttpContext context, ServerOptions options)
    {
        if (options.WebPageDirectory is { } directory)
        {
            string file = Path.Combine(directory, "index.html");

            if (File.Exists(file))
            {
                return Results.File(file, "text/html; charset=utf-8");
            }
        }

        byte[]? compressed = PageBytes.Value;

        if (compressed is null)
        {
            return EndpointResults.Error(
                StatusCodes.Status404NotFound,
                "no_page",
                "服务端没有内嵌页面：先在 src/Sevencat.Label.Web 跑 npm run build:server");
        }

        if (AcceptsGzip(context.Request))
        {
            context.Response.Headers.ContentEncoding = "gzip";

            return Results.Bytes(compressed, "text/html; charset=utf-8");
        }

        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        await gzip.CopyToAsync(output, context.RequestAborted);

        return Results.Bytes(output.ToArray(), "text/html; charset=utf-8");
    }

    private static async Task<IResult> ServeIconAsync(ServerOptions options)
    {
        if (options.WebPageDirectory is { } directory)
        {
            string file = Path.Combine(directory, "favicon.ico");

            if (File.Exists(file))
            {
                return Results.File(file, "image/x-icon");
            }
        }

        if (IconBytes.Value is { } icon)
        {
            return Results.Bytes(icon, "image/x-icon");
        }

        await Task.CompletedTask;

        return Results.NoContent();
    }

    private static bool AcceptsGzip(HttpRequest request)
        => request.Headers.AcceptEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase);

    private static byte[]? ReadResource(string suffix)
    {
        Assembly assembly = typeof(WebPageEndpoints).Assembly;
        string? name = assembly.GetManifestResourceNames()
            .FirstOrDefault(resource => resource.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        if (name is null)
        {
            return null;
        }

        using Stream? stream = assembly.GetManifestResourceStream(name);

        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return buffer.ToArray();
    }
}
