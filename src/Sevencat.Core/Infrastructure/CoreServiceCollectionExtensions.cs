using FreeSql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;
using Sevencat.Core.Dataset;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Infrastructure.Logging;

namespace Sevencat.Core.Infrastructure;

public static class CoreServiceCollectionExtensions
{
    /// <summary>注册两个应用共用的基础设施：配置、日志（NLog）。桌面端与 Web 端都从这里拿。</summary>
    public static IServiceCollection AddAnnotrainCore(this IServiceCollection services, string applicationName)
    {
        ArgumentNullException.ThrowIfNull(services);

        AppPaths.EnsureCreated();

        var buffer = new LogBuffer();
        services.AddSingleton(buffer);

        // 库是进程级单例：各入口启动时先 TinyLabelDatabase.Initialize（文件不在就建空库），
        // 这里只把它的 IFreeSql 注册出去——配置、账号、项目共用同一个连接池，不各开各的
        services.AddSingleton(_ => TinyLabelDatabase.Current.Orm);
        services.AddSingleton<SettingsStore>();
        services.AddSingleton(provider => provider.GetRequiredService<SettingsStore>().Load());

        NLogSetup.Configure(buffer, AppPaths.LogDirectory, applicationName);

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddNLog();
        });

        services.AddAnnotrainDatasets();

        return services;
    }
}
