using Avalonia;
using ImageFacade.SkiaSharp;
using Sevencat.Core.Imaging;
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

        // --data <目录>：数据根目录（项目库 / 配置库 / 日志），默认 exe 同级 data
        AppDataPaths.Initialize(AppDataPaths.ReadDataArgument(args));
        AppDataPaths.EnsureCreated();

        // 图像后端在 exe 入口注册：界面与数据管线共用同一个 SkiaSharp 后端（导入图片读尺寸也用它）
        ImageRuntime.Use(new SkiaBackend());

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
