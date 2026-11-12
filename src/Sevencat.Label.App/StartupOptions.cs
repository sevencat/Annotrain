using Sevencat.Core.Infrastructure;

namespace Sevencat.Label.App;

/// <summary>
/// 命令行启动参数：<c>--data &lt;目录&gt;</c> 定数据目录，<c>--open</c> / <c>--annotate</c> 给的是同一个目录，
/// 只是启动后自动打开它（<c>--annotate</c> 再进首图标注，演示 / 自测用）。
/// </summary>
public sealed class StartupOptions
{
    private StartupOptions(string? dataDirectory, string? openFolder, string? annotateFolder)
    {
        DataDirectory = dataDirectory;
        OpenFolder = openFolder;
        AnnotateFolder = annotateFolder;
    }

    /// <summary>数据目录（<c>--data</c>，不给就取 <c>--open</c> / <c>--annotate</c> 的值，都没有就是用默认目录）。</summary>
    public string? DataDirectory { get; }

    /// <summary><c>--open</c> 指定的目录：启动后直接读这里的项目列表。</summary>
    public string? OpenFolder { get; }

    /// <summary><c>--annotate</c> 指定的目录：打开后直接进首图标注。</summary>
    public string? AnnotateFolder { get; }

    public static StartupOptions Parse(string[]? args)
    {
        string? open = AppDataPaths.ReadArgument(args, "--open");
        string? annotate = AppDataPaths.ReadArgument(args, "--annotate");

        return new StartupOptions(
            AppDataPaths.ReadArgument(args, AppDataPaths.DataArgument) ?? open ?? annotate,
            open,
            annotate);
    }
}
