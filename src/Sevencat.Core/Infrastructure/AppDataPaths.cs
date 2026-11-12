namespace Sevencat.Core.Infrastructure;

/// <summary>
/// 数据根目录：默认在 <c>exe 同级/data</c>，可用命令行 <c>--data &lt;目录&gt;</c> 覆盖。
/// <para>
/// 项目库（annotrain.db）、配置库（settings.db）、日志都放在这里面，
/// 这样整个程序是"绿色"的：拷走 exe 目录就把数据一起带走。
/// 用户配置目录 <see cref="AppPaths.ConfigDirectory"/> 只留给旧配置迁移与 NLog 外部配置。
/// </para>
/// </summary>
public static class AppDataPaths
{
    /// <summary>命令行开关：<c>--data &lt;目录&gt;</c>。</summary>
    public const string DataArgument = "--data";

    /// <summary>默认项目库文件名（与 <c>TinyLabelProjectCatalog.DefaultDatabaseFile</c> 一致）。</summary>
    public const string ProjectDatabaseFile = "annotrain.db";

    private static string? _override;

    /// <summary>数据根目录（绝对路径）。</summary>
    public static string DataDirectory => _override ?? Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>默认项目库：<c>data/annotrain.db</c>。</summary>
    public static string ProjectDatabasePath => Path.Combine(DataDirectory, ProjectDatabaseFile);

    /// <summary>配置库：<c>data/settings.db</c>（表 settings，键值对存 JSON）。</summary>
    public static string SettingsDatabasePath => Path.Combine(DataDirectory, "settings.db");

    /// <summary>日志目录：<c>data/logs</c>。</summary>
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>用命令行参数覆盖数据目录；必须在注册服务之前调用。</summary>
    public static void Initialize(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        _override = Path.GetFullPath(directory.Trim().Trim('"'));
    }

    /// <summary>从参数里取 <c>--data</c> 的值（没有返回 null）。</summary>
    public static string? ReadDataArgument(string[]? args)
    {
        if (args is null)
        {
            return null;
        }

        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], DataArgument, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    /// <summary>建目录；放在进程启动最早期调用。</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
