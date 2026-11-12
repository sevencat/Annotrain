namespace Sevencat.Core.Infrastructure;

/// <summary>
/// 数据根目录：默认在 <c>exe 同级/data</c>，可用命令行 <c>--data &lt;目录&gt;</c> 覆盖。
/// <para>
/// 只有一个库文件 <c>annotrain.db</c>（项目 + 配置 + Web 账号）和日志，
/// 这样整个程序是"绿色"的：拷走 exe 目录就把数据一起带走。
/// 用户配置目录 <see cref="AppPaths.ConfigDirectory"/> 只留给旧配置迁移与 NLog 外部配置。
/// </para>
/// </summary>
public static class AppDataPaths
{
    /// <summary>命令行开关：<c>--data &lt;目录&gt;</c>。</summary>
    public const string DataArgument = "--data";

    /// <summary>命令行开关：<c>--db &lt;文件&gt;</c>；不给就跟着数据目录走。</summary>
    public const string DatabaseArgument = "--db";

    /// <summary>库文件名（项目、配置、账号都在这个库里；与 <c>TinyLabelProjectCatalog.DefaultDatabaseFile</c> 一致）。</summary>
    public const string ProjectDatabaseFile = "annotrain.db";

    /// <summary>旧版独立配置库文件名；只在迁移旧数据时读取，不再新建。</summary>
    public const string LegacySettingsDatabaseFile = "settings.db";

    private static string? _override;

    /// <summary>数据根目录（绝对路径）。</summary>
    public static string DataDirectory => _override ?? Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>项目库：<c>data/annotrain.db</c>。</summary>
    public static string ProjectDatabasePath => Path.Combine(DataDirectory, ProjectDatabaseFile);

    /// <summary>
    /// 启动时要打开的库文件：<c>--db &lt;文件&gt;</c> 优先，其次环境变量 <c>ANNOTRAIN_DB</c>，
    /// 都没给就是数据目录里的 <c>annotrain.db</c>。整个进程只定这一次。
    /// </summary>
    public static string ResolveDatabasePath(string[]? args)
    {
        if (ReadArgument(args, DatabaseArgument) is { Length: > 0 } file)
        {
            return Path.GetFullPath(file.Trim().Trim('"'));
        }

        return Environment.GetEnvironmentVariable("ANNOTRAIN_DB") is { Length: > 0 } fromEnvironment
            ? Path.GetFullPath(fromEnvironment.Trim().Trim('"'))
            : ProjectDatabasePath;
    }

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
    public static string? ReadDataArgument(string[]? args) => ReadArgument(args, DataArgument);

    /// <summary>从参数里取 <c>--xxx &lt;值&gt;</c> 的值（没有返回 null）。</summary>
    public static string? ReadArgument(string[]? args, string name)
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

    /// <summary>建目录；放在进程启动最早期调用。</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
