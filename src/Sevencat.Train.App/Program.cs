using Avalonia;
using ImageFacade.SkiaSharp;
using Sevencat.Core.Imaging;
using Sevencat.Core.Infrastructure;

namespace Sevencat.Train.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!PlatformGuard.EnsureSupported())
        {
            return 1;
        }

        // 图像后端在 exe 入口注册：训练数据管线与界面共用同一个 SkiaSharp 后端。
        ImageRuntime.Use(new SkiaBackend());

        // --simulate <目录>：跑一遍"生成图 → 导入标注 → 训练 → 导出 → 验证"，不开窗口
        if (SimulationCommand.TryParse(args, out SimulationCommand command))
        {
            return SimulationCommandRunner.RunAsync(command).GetAwaiter().GetResult();
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
