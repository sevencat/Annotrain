using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sevencat.Core.Infrastructure;
using Sevencat.Train.App.ViewModels;
using Sevencat.Train.App.Views;

namespace Sevencat.Train.App;

public partial class App : Application
{
    /// <summary>进程内服务容器；Web 端（M7）也从这里取任务队列。</summary>
    public IServiceProvider Services { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        services.AddAnnotrainCore("train");
        Services = services.BuildServiceProvider();

        ILogger<App> logger = Services.GetRequiredService<ILoggerFactory>().CreateLogger<App>();
        logger.LogInformation("训练应用启动，配置目录：{Directory}", AppPaths.ConfigDirectory);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = ActivatorUtilities.CreateInstance<MainWindowViewModel>(Services);
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.Exit += (_, _) =>
            {
                logger.LogInformation("训练应用退出");
                (Services as IDisposable)?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
