using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sevencat.Core.Infrastructure;
using Sevencat.Core.Localization;
using Sevencat.Label;
using Sevencat.Label.App.ViewModels;
using Sevencat.Label.App.Views;
using Sevencat.Label.Server;

namespace Sevencat.Label.App;

public partial class App : Application
{
    /// <summary>进程内服务容器；Web 端（M3）也从这里取业务对象。</summary>
    public IServiceProvider Services { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        services.AddAnnotrainCore("label");
        services.AddAnnotrainLabel();
        Services = services.BuildServiceProvider();

        // 语言：设置里存 zh/en，空值跟随系统
        AppSettings settings = Services.GetRequiredService<AppSettings>();
        Localizer.Instance.SetLanguage(
            string.IsNullOrWhiteSpace(settings.Language)
                ? Localizer.DetectSystemLanguage()
                : settings.Language);

        ILogger<App> logger = Services.GetRequiredService<ILoggerFactory>().CreateLogger<App>();
        logger.LogInformation("标注应用启动，数据目录：{Directory}", AppDataPaths.DataDirectory);

        // 标注 Web 端：跟着桌面端起停，只绑本机回环；端口被占用时只记日志，不影响桌面端使用
        LabelWebServer? webServer = StartWebServer(logger);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = ActivatorUtilities.CreateInstance<MainWindowViewModel>(Services);
            var window = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = window;

            // --open <数据目录>：启动后直接读这个目录里的项目列表，便于从命令行/快捷方式进入
            if (ReadArgument(desktop.Args, "--open") is { } folder)
            {
                window.Opened += async (_, _) => await viewModel.OpenWorkspaceAsync(folder);
            }
            else if (ReadArgument(desktop.Args, "--annotate") is { } annotateFolder)
            {
                // --annotate <数据目录>：打开目录并直接进入首图标注（演示 / 自测用）
                window.Opened += async (_, _) =>
                {
                    await viewModel.OpenWorkspaceAsync(annotateFolder);
                    await window.AnnotateSelectedAsync();
                };
            }
            else
            {
                // 没给参数时回到上次的数据目录
                window.Opened += async (_, _) => await viewModel.OpenLastWorkspaceAsync();
            }

            desktop.Exit += (_, _) =>
            {
                logger.LogInformation("标注应用退出");

                if (webServer is not null)
                {
                    try
                    {
                        webServer.StopAsync().Wait(TimeSpan.FromSeconds(2));
                        webServer.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "标注 Web 端停止时报错");
                    }
                }

                (Services as IDisposable)?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>启动进程内的标注 Web 服务；失败（端口被占等）只记日志，桌面端照常用。</summary>
    private LabelWebServer? StartWebServer(ILogger logger)
    {
        ServerOptions options = new() { DataDirectory = AppDataPaths.DataDirectory };

        try
        {
            LabelWebServer server = LabelWebServer.Create(
                options,
                Services,
                Services.GetRequiredService<ILoggerFactory>());

            server.StartAsync().GetAwaiter().GetResult();
            logger.LogInformation("标注 Web 端已启动：{Url}", server.Url);

            return server;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "标注 Web 端启动失败（{Url} 可能被占用），桌面端继续", options.Url);

            return null;
        }
    }

    private static string? ReadArgument(string[]? args, string name)
    {
        if (args is null)
        {
            return null;
        }

        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
