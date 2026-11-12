using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sevencat.Core.Infrastructure;
using Sevencat.Core.Localization;
using Sevencat.Train.App.ViewModels;
using Sevencat.Train.Engine;
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

        // 真训练引擎（TorchSharp）：替换 Core 里的干跑实现，必须在 AddAnnotrainCore 之后。
        services.AddAnnotrainTrainingEngine();

        Services = services.BuildServiceProvider();

        // 语言：设置里存 zh/en，空值跟随系统
        AppSettings settings = Services.GetRequiredService<AppSettings>();
        Localizer.Instance.SetLanguage(
            string.IsNullOrWhiteSpace(settings.Language)
                ? Localizer.DetectSystemLanguage()
                : settings.Language);

        ILogger<App> logger = Services.GetRequiredService<ILoggerFactory>().CreateLogger<App>();
        logger.LogInformation("训练应用启动，数据目录：{Directory}", AppDataPaths.DataDirectory);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = ActivatorUtilities.CreateInstance<MainWindowViewModel>(Services);
            var window = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = window;

            // --open <数据集目录>：启动即打开数据集，省去一次选择
            if (ReadArgument(desktop.Args, "--open") is { } dataset)
            {
                window.Opened += async (_, _) => await viewModel.OpenDatasetAsync(dataset);
            }

            desktop.Exit += (_, _) =>
            {
                logger.LogInformation("训练应用退出");
                (Services as IDisposable)?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
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
