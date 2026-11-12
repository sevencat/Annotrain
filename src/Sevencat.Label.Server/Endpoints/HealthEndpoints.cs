namespace Sevencat.Label.Server;

/// <summary>探活：桌面端顶栏「打开」之前可以先用它确认服务在跑。</summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", (ServerOptions options) => Results.Ok(new
        {
            status = "ok",
            version = typeof(HealthEndpoints).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            url = options.Url,
            time = DateTimeOffset.UtcNow,
        }));

        return app;
    }
}
