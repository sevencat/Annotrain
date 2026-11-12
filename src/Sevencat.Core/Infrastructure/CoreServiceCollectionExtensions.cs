using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;
using Sevencat.Core.Dataset;
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

        // 配置进数据库：data/settings.db（与项目库同目录，整个 data 目录可整体拷走）
        services.AddSingleton(_ => new SettingsStore(AppDataPaths.SettingsDatabasePath));
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
