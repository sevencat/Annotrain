using Avalonia;
using Sevencat.Core.Infrastructure;

namespace Sevencat.Label.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!PlatformGuard.EnsureSupported())
        {
            return 1;
        }

        // --import <源目录> --output <目标目录>：把 YOLO / RF-DETR 数据集导成标注项目，不开窗口
        if (ImportCommand.TryParse(args, out ImportCommand command))
        {
            return ImportCommandRunner.RunAsync(command).GetAwaiter().GetResult();
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseWin32()
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont()
            .LogToTrace();
}
