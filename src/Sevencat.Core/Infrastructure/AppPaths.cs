namespace Sevencat.Core.Infrastructure;

/// <summary>
/// 用户级目录约定：配置与日志默认落在 <c>exe/data</c>（见 <see cref="AppDataPaths"/>）。
/// <see cref="ConfigDirectory"/> 只保留给旧版 settings.json 的迁移读取与 NLog 外部配置文件。
/// </summary>
public static class AppPaths
{
    /// <summary>旧版用户配置目录（<c>%APPDATA%\Sevencat.Annotrain</c>）；仅用于迁移。</summary>
    public static string ConfigDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AnnotrainInfo.AppFolderName);

    public static string LogDirectory => AppDataPaths.LogDirectory;

    /// <summary>旧版设置文件；新版配置存数据库（<see cref="AppDataPaths.SettingsDatabasePath"/>）。</summary>
    public static string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");

    /// <summary>创建目录；放在进程启动最早期调用。</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigDirectory);
        AppDataPaths.EnsureCreated();
    }
}
