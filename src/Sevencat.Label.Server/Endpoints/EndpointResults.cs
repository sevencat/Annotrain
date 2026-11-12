using Sevencat.Core.Localization;

namespace Sevencat.Label.Server;

/// <summary>统一的错误体，形状对上前端的 <c>ApiError</c> / <c>ApiConflict</c>。</summary>
public static class EndpointResults
{
    public static IResult Error(int statusCode, string code, string message)
        => Results.Json(new ApiError(code, message), statusCode: statusCode);

    public static IResult Unauthorized()
        => Error(StatusCodes.Status401Unauthorized, "unauthorized", Text.Get("Web.Unauthorized"));

    public static IResult Forbidden()
        => Error(StatusCodes.Status403Forbidden, "forbidden", Text.Get("Web.Forbidden"));

    public static IResult NotFound(string what)
        => Error(StatusCodes.Status404NotFound, "not_found", Text.Format("Web.NotFound", what));

    public static IResult ServerError(string message)
        => Error(StatusCodes.Status500InternalServerError, "server_error", message);

    /// <summary>乐观锁 / 租约冲突：<c>current</c> 带服务端最新那张图。</summary>
    public static IResult Conflict(string message, ImageDto? current)
        => Results.Json(new ApiConflict(new ApiError("conflict", message), current), statusCode: StatusCodes.Status409Conflict);
}
