namespace Sevencat.Core.Infrastructure;

/// <summary>用户级目录约定：配置与日志都落在用户配置目录下，避免写程序目录（安装目录可能只读）。</summary>
public static class AppPaths
{
    public static string ConfigDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AnnotrainInfo.AppFolderName);

    public static string LogDirectory => Path.Combine(ConfigDirectory, "logs");

    public static string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");

    /// <summary>创建目录；放在进程启动最早期调用。</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
