using Sevencat.Core.Localization;
using Sevencat.Label.Server.Security;

namespace Sevencat.Label.Server;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", (LoginRequest request, AuthService auth) =>
            auth.Login(request.UserName, request.Password) is { } response
                ? Results.Ok(response)
                : EndpointResults.Error(StatusCodes.Status401Unauthorized, "login_failed", Text.Get("Web.LoginFailed")));

        app.MapGet("/api/auth/me", (HttpContext context, AuthService auth) =>
            auth.Find(CurrentUser.Require(context)) is { } user
                ? Results.Ok(user)
                : EndpointResults.Unauthorized());

        // 令牌是无状态的，登出只要前端丢掉它；保留这个端点让前端语义完整。
        app.MapPost("/api/auth/logout", () => Results.NoContent());

        return app;
    }
}
