using FreeSql;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Storage;
using Sevencat.Label.Server.Data;
using Sevencat.Label.Server.Security;

namespace Sevencat.Label.Server;

/// <summary>
/// 进程内的标注 Web 服务：由桌面端启动、随桌面端退出，只绑 <see cref="ServerOptions.Url"/>（默认本机回环）。
/// 业务对象（项目目录 / 类别 / 任务流 / 存储注册表）复用桌面端的容器，Web 侧只补账号与令牌。
/// </summary>
public sealed class LabelWebServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private LabelWebServer(WebApplication app, ServerOptions options)
    {
        _app = app;
        Options = options;
    }

    public ServerOptions Options { get; }

    public string Url => Options.Url;

    public static LabelWebServer Create(
        ServerOptions options,
        IServiceProvider coreServices,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(coreServices);

        ILogger logger = loggerFactory?.CreateLogger("Sevencat.Label.Server") ?? NullLogger.Instance;

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = LabelWebModule.Name,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.WebHost.UseUrls(options.Url);

        // 日志统一走桌面端的 NLog，Kestrel 自己那套控制台输出关掉
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(coreServices.GetRequiredService<BlobStoreRegistry>());
        builder.Services.AddSingleton(coreServices.GetRequiredService<TinyLabelProjectCatalog>());
        builder.Services.AddSingleton(coreServices.GetRequiredService<TinyLabelClassService>());
        builder.Services.AddSingleton(coreServices.GetRequiredService<TinyLabelTaskService>());

        // 账号与桌面端共用同一个库/连接池（桌面端容器里的 IFreeSql 单例）
        builder.Services.AddSingleton(coreServices.GetRequiredService<IFreeSql>());
        builder.Services.AddSingleton<WebUserStore>();
        builder.Services.AddSingleton(_ => new TokenProtector(options.TokenKeyPath, options.TokenLifetime));
        builder.Services.AddSingleton<AuthService>();

        WebApplication app = builder.Build();

        app.UseMiddleware<TokenAuthMiddleware>();

        app.MapHealthEndpoints();
        app.MapAuthEndpoints();
        app.MapProjectEndpoints();
        app.MapImageEndpoints();
        app.MapWebPageEndpoints(options);

        // 第一次启动：建 admin 账号并把初始密码写在数据目录里
        WebUserStore users = app.Services.GetRequiredService<WebUserStore>();
        string? bootstrapPassword = users.EnsureBootstrapAdmin(options.BootstrapPasswordPath);

        if (bootstrapPassword is not null)
        {
            logger.LogInformation(
                "已创建标注 Web 管理员账号 admin，初始密码 {Password}（同样写在 {Path}）",
                bootstrapPassword,
                options.BootstrapPasswordPath);
        }

        return new LabelWebServer(app, options);
    }

    public Task StartAsync(CancellationToken ct = default) => _app.StartAsync(ct);

    public Task StopAsync(CancellationToken ct = default) => _app.StopAsync(ct);

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
