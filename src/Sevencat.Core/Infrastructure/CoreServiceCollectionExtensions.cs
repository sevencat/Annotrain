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
    /// <summary>
    /// 打开（不存在就建）当前工作库，并把 <see cref="TinyLabelDatabase"/> 注入成单例（随容器释放）。
    /// 要在 <see cref="AddAnnotrainCore"/> 之前调用：库路径只定这一次，配置 / 账号 / 项目共用这一个连接池。
    /// </summary>
    public static IServiceCollection AddAnnotrainDatabase(this IServiceCollection services, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        services.AddSingleton(TinyLabelDatabase.Open(databasePath));

        return services;
    }

    /// <summary>注册两个应用共用的基础设施：库、配置、日志（NLog）。桌面端与 Web 端都从这里拿。</summary>
    public static IServiceCollection AddAnnotrainCore(this IServiceCollection services, string applicationName)
    {
        ArgumentNullException.ThrowIfNull(services);

        AppPaths.EnsureCreated();

        var buffer = new LogBuffer();
        services.AddSingleton(buffer);

        // 库单例由 AddAnnotrainDatabase 注册，这里只是把它的 IFreeSql 也挂出来：
        // 配置、账号、项目注入同一个连接池，谁都不 new、不 Dispose
        services.AddSingleton(provider => provider.GetRequiredService<TinyLabelDatabase>().Orm);
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
