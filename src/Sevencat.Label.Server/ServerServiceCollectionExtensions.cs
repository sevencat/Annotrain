using Microsoft.Extensions.DependencyInjection;
using Sevencat.Core.Infrastructure;
using Sevencat.Label.Server.Data;

namespace Sevencat.Label.Server;

/// <summary>Web 端的注册入口：在桌面端容器里补上服务参数、账号表与 Web 宿主。</summary>
public static class ServerServiceCollectionExtensions
{
    public static IServiceCollection AddAnnotrainWebServer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 数据目录跟着当前工作库走（库在 AddAnnotrainDatabase 里已经定死，运行期不换）；
        // 端口来自环境变量（含 .env），没有就用默认端口，地址由端口拼
        services.AddSingleton(provider => new ServerOptions
        {
            Port = ResolvePort(),
            DataDirectory = provider.GetRequiredService<IDatabaseLocation>().DatabaseDirectory,
        });

        // 账号与桌面端共用库/连接池（IFreeSql 单例），用户管理窗口直接从容器取它
        services.AddSingleton<WebUserStore>();
        services.AddSingleton<LabelWebServerHost>();

        return services;
    }

    /// <summary>
    /// 监听端口：环境变量优先，没设就用默认端口；写错了直接报出来——
    /// 悄悄退回 5180 会让人以为配置生效了。地址不单独配，恒为 <c>http://127.0.0.1:{端口}</c>。
    /// </summary>
    private static int ResolvePort()
    {
        if (AppEnvironment.Get(ServerOptions.PortVariable) is not { } configured)
        {
            return LabelWebModule.DefaultPort;
        }

        if (int.TryParse(configured, out int port) && port is > 0 and <= 65535)
        {
            return port;
        }

        throw new InvalidOperationException(
            $"环境变量 {ServerOptions.PortVariable} 必须是 1–65535 之间的端口号：{configured}");
    }
}
