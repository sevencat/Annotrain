using System.Runtime.InteropServices;

namespace Sevencat.Core.Infrastructure;

/// <summary>
/// Annotrain 只支持 Windows x64：构建层面已锁死 <c>win-x64</c>，这里再做一次运行期兜底，
/// 避免被复制到 arm64（x64 模拟）等环境后静默跑出难排查的行为差异。
/// </summary>
public static class PlatformGuard
{
    /// <summary>支持的平台标识。</summary>
    public const string SupportedPlatform = "win-x64";

    private const uint MessageBoxIconError = 0x00000010;

    /// <summary>当前进程是否运行在受支持的平台上。</summary>
    public static bool IsSupported =>
        OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>不受支持时的说明文案。</summary>
    public static string UnsupportedMessage =>
        $"{AnnotrainInfo.ProductName} 仅支持 Windows x64（{SupportedPlatform}）。" +
        $"{Environment.NewLine}当前环境：{RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}";

    /// <summary>
    /// 启动最早期调用：不受支持时弹出提示（无控制台也能看到）并返回 <c>false</c>，调用方应直接退出。
    /// </summary>
    public static bool EnsureSupported()
    {
        if (IsSupported)
        {
            return true;
        }

        string message = UnsupportedMessage;
        Console.Error.WriteLine(message);

        if (OperatingSystem.IsWindows())
        {
            ShowMessageBox(message);
        }

        return false;
    }

    private static void ShowMessageBox(string message)
    {
        try
        {
            MessageBoxW(0, message, AnnotrainInfo.ProductName, MessageBoxIconError);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            // 弹窗失败也不影响退出码
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);
}
