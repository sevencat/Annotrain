using Microsoft.Extensions.Primitives;
using Sevencat.Core.Localization;

namespace Sevencat.Label.Server.Security;

/// <summary>
/// <c>/api</c> 下的请求都要求带令牌：<c>Authorization: Bearer &lt;token&gt;</c>，
/// 或者 <c>?token=</c>（图片走 <c>&lt;img&gt;</c> 带不了请求头，只能挂 query）。
/// 页面本身不设防——页面里没有数据，数据全靠 <c>/api</c>。
/// </summary>
public sealed class TokenAuthMiddleware(RequestDelegate next, TokenProtector tokens)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api") || IsAnonymous(context.Request.Path))
        {
            await next(context);
            return;
        }

        if (tokens.Validate(ReadToken(context)) is { } userId)
        {
            CurrentUser.Set(context, userId);
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(
            new ApiError("unauthorized", Text.Get("Web.Unauthorized")),
            context.RequestAborted);
    }

    private static bool IsAnonymous(PathString path)
        => path.StartsWithSegments("/api/auth/login") || path.StartsWithSegments("/api/health");

    private static string? ReadToken(HttpContext context)
    {
        StringValues header = context.Request.Headers.Authorization;

        if (!StringValues.IsNullOrEmpty(header))
        {
            string text = header.ToString();

            if (text.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return text["Bearer ".Length..].Trim();
            }
        }

        return context.Request.Query.TryGetValue("token", out StringValues query) ? query.ToString() : null;
    }
}
