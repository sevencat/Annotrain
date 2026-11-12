using Avalonia;
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
