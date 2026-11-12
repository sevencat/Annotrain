using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Infrastructure;
using Sevencat.Core.Localization;
using Sevencat.Label;
using Sevencat.Label.App.ViewModels;
using Sevencat.Label.App.Views;
using Sevencat.Label.Server;

namespace Sevencat.Label.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        string[]? args = (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Args;
        StartupOptions startup = StartupOptions.Parse(args);

        // 数据目录（库、日志都落这里）先定，再打开库：文件不在就当场建空库
        AppDataPaths.Initialize(startup.DataDirectory);

        string databasePath = AppDataPaths.ResolveDatabasePath(args);

        ServiceProvider services = new ServiceCollection()
            .AddAnnotrainDatabase(databasePath)
            .AddAnnotrainCore("label")
            .AddAnnotrainLabel()
            .AddAnnotrainWebServer()
            .AddSingleton(startup)
            .AddSingleton<MainWindowViewModel>()
            .AddSingleton<MainWindow>()
            .BuildServiceProvider();

        // 语言：设置里存 zh/en，空值跟随系统
        ApplyLanguage(services.GetRequiredService<AppSettings>());

        ILogger<App> logger = services.GetRequiredService<ILoggerFactory>().CreateLogger<App>();
        logger.LogInformation(
            "标注应用启动，数据目录：{Directory}，数据库：{Database}",
            AppDataPaths.DataDirectory,
            databasePath);

        // Web 端随桌面端启停，只绑本机回环；起不来（端口占用等）只记日志，不影响桌面端
        services.GetRequiredService<LabelWebServerHost>().Start();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = services.GetRequiredService<MainWindow>();

            desktop.Exit += (_, _) =>
            {
                logger.LogInformation("标注应用退出");

                // 容器的 DisposeAsync 会顺带停掉 Web 服务、关掉库；窗口已经关了，
                // 这里只等收尾（超时就不再等，进程反正要退）
                try
                {
                    Task.Run(async () => await services.DisposeAsync()).Wait(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "释放服务容器时报错");
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ApplyLanguage(AppSettings settings) =>
        Localizer.Instance.SetLanguage(
            string.IsNullOrWhiteSpace(settings.Language)
                ? Localizer.DetectSystemLanguage()
                : settings.Language);
}
