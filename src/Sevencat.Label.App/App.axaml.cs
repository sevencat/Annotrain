using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sevencat.Core.Infrastructure;
using Sevencat.Label.App.ViewModels;
using Sevencat.Label.App.Views;

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
        Services = services.BuildServiceProvider();

        ILogger<App> logger = Services.GetRequiredService<ILoggerFactory>().CreateLogger<App>();
        logger.LogInformation("标注应用启动，配置目录：{Directory}", AppPaths.ConfigDirectory);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = ActivatorUtilities.CreateInstance<MainWindowViewModel>(Services);
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.Exit += (_, _) =>
            {
                logger.LogInformation("标注应用退出");
                (Services as IDisposable)?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
