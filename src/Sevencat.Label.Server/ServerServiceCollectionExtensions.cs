using Microsoft.Extensions.DependencyInjection;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Infrastructure;
using Sevencat.Label.Server.Data;

namespace Sevencat.Label.Server;

/// <summary>Web 端的注册入口：在桌面端容器里补上服务参数、账号表与 Web 宿主。</summary>
public static class ServerServiceCollectionExtensions
{
    public static IServiceCollection AddAnnotrainWebServer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 数据目录跟着当前工作库走（库在 AddAnnotrainDatabase 里已经定死，运行期不换）
        services.AddSingleton(provider => new ServerOptions
        {
            DataDirectory = Path.GetDirectoryName(provider.GetRequiredService<TinyLabelDatabase>().Path)
                            ?? AppDataPaths.DataDirectory,
        });

        // 账号与桌面端共用库/连接池（IFreeSql 单例），用户管理窗口直接从容器取它
        services.AddSingleton<WebUserStore>();
        services.AddSingleton<LabelWebServerHost>();

        return services;
    }
}
