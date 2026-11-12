namespace Sevencat.Core.Infrastructure;

/// <summary>
/// 命令行启动参数：<c>--data &lt;目录&gt;</c> 定数据目录，<c>--open</c> / <c>--annotate</c> 给的是同一个目录，
/// 只是启动后自动打开它（<c>--annotate</c> 再进首图标注，演示 / 自测用）。
/// <para>
/// 它同时就是「库在哪」（<see cref="IDatabaseLocation"/>）：库路径按 <c>--db</c> &gt; <c>ANNOTRAIN_DB</c> &gt;
/// 数据目录里的 <c>annotrain.db</c> 算出来，和启动时真正打开的是同一个值。
/// </para>
/// <para>
/// 放在 Core 而不是某个 App 里，是因为这是各宿主共用的事实：标注端、训练端、命令行导入、
/// 测试都可以 <see cref="Parse"/> 一份自己的参数，容器里注册的就是它，不必再为「库在哪」造一个实现。
/// </para>
/// </summary>
public sealed class StartupOptions : IDatabaseLocation
{
    private readonly Lazy<string> _databasePath;

    private StartupOptions(string[]? args, string? dataDirectory, string? openFolder, string? annotateFolder)
    {
        DataDirectory = dataDirectory;
        OpenFolder = openFolder;
        AnnotateFolder = annotateFolder;

        // 库路径要等 AppDataPaths.Initialize(数据目录) 之后才算得准（--data 会影响默认库位置），
        // 所以不在这里求值，第一次被问到时再算，结果只算一次
        _databasePath = new Lazy<string>(() => AppDataPaths.ResolveDatabasePath(args));
    }

    /// <summary>数据目录（<c>--data</c>，不给就取 <c>--open</c> / <c>--annotate</c> 的值，都没有就是用默认目录）。</summary>
    public string? DataDirectory { get; }

    /// <summary><c>--open</c> 指定的目录：启动后直接打开它（标注端读项目列表，训练端读数据集）。</summary>
    public string? OpenFolder { get; }

    /// <summary><c>--annotate</c> 指定的目录：打开后直接进首图标注。</summary>
    public string? AnnotateFolder { get; }

    /// <summary>启动时要打开的库：<c>--db</c> &gt; 环境变量 <c>ANNOTRAIN_DB</c> &gt; 数据目录里的 <c>annotrain.db</c>。</summary>
    public string DatabasePath => _databasePath.Value;

    public static StartupOptions Parse(string[]? args)
    {
        string? open = AppDataPaths.ReadArgument(args, "--open");
        string? annotate = AppDataPaths.ReadArgument(args, "--annotate");

        return new StartupOptions(
            args,
            // 命令行 --data 优先，其次 --open / --annotate，最后是环境变量（含 .env 里写的）
            AppDataPaths.ReadArgument(args, AppDataPaths.DataArgument)
                ?? open
                ?? annotate
                ?? AppEnvironment.Get(AppDataPaths.DataVariable),
            open,
            annotate);
    }
}
