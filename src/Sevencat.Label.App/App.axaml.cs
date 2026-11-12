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

        ServiceProvider services = new ServiceCollection()
            // 参数已经解析过，直接用这一份注册：窗口与启动逻辑看到的是同一个对象，
            // 它同时就是「库在哪」（IDatabaseLocation），开库时直接拿它
            .AddSingleton(startup)
            .AddAnnotrainDatabase(startup)
            .AddAnnotrainCore("label")
            .AddAnnotrainLabel()
            .AddAnnotrainWebServer()
            // Injectio 按程序集生成：MainWindowViewModel / MainWindow 在特性里注册
            .AddSevencatLabelApp()
            .BuildServiceProvider();

        // 逃生舱口：少数拿不到构造函数注入的地方（XAML 预览、转换器回调）可以从这里偶尔取一次
        BeanFactory.Initialize(services);

        // 语言：设置里存 zh/en，空值跟随系统
        ApplyLanguage(services.GetRequiredService<AppSettings>());

        ILogger<App> logger = services.GetRequiredService<ILoggerFactory>().CreateLogger<App>();

        ServerOptions serverOptions;

        try
        {
            serverOptions = services.GetRequiredService<ServerOptions>();
        }
        catch (InvalidOperationException ex)
        {
            // .env / 环境变量写错了：日志里留下原因，再往上抛让进程直接退出，别带着错配置跑
            logger.LogError(ex, "启动配置不合法，请检查 exe 同级的 .env 或环境变量");
            throw;
        }

        logger.LogInformation(
            "标注应用启动，数据目录：{Directory}，数据库：{Database}，Web 地址：{Url}",
            AppDataPaths.DataDirectory,
            startup.DatabasePath,
            serverOptions.Url);

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
                finally
                {
                    // 容器已经释放，再留着引用只会让后续解析调到已释放的 provider
                    BeanFactory.Reset();
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
