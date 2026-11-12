namespace Sevencat.Core.Infrastructure;

/// <summary>
/// 启动期的环境变量。<c>.env</c> 只做一件事：把里面的键值填进环境变量，
/// 之后代码里一律只认环境变量（<see cref="Get"/>），业务代码不需要知道有没有这个文件。
/// <para>
/// 用户要改端口、库位置这类「进程级、改完要重启」的设置，就手工在 exe 同级写 <c>.env</c>：
/// <code>
/// ANNOTRAIN_DATA=D:\annotrain
/// ANNOTRAIN_DB=D:\annotrain\custom.db
/// ANNOTRAIN_LABEL_URL=http://127.0.0.1:5280
/// </code>
/// 已经存在的环境变量优先，文件不会把它们改掉；命令行参数仍然压过环境变量。
/// </para>
/// </summary>
public static class AppEnvironment
{
    /// <summary>文件名：只看 exe 同级，翻父目录会让人猜不到配置从哪来。</summary>
    public const string FileName = ".env";

    /// <summary>
    /// 读 exe 同级的 <c>.env</c> 并填进环境变量；文件不存在就什么都不做。
    /// 只在进程最开头调一次（早于任何读环境变量的代码）。
    /// </summary>
    public static void LoadDotEnv(string? directory = null)
    {
        string path = Path.Combine(directory ?? AppContext.BaseDirectory, FileName);

        if (!File.Exists(path))
        {
            return;
        }

        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            int split = line.IndexOf('=');

            if (split <= 0)
            {
                continue;
            }

            string key = line[..split].Trim();

            // 允许写 export KEY=VALUE（从 shell 脚本里复制过来不用改）
            if (key.StartsWith("export ", StringComparison.Ordinal))
            {
                key = key["export ".Length..].Trim();
            }

            if (key.Length == 0 || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
            {
                continue;
            }

            Environment.SetEnvironmentVariable(key, Unquote(line[(split + 1)..]));
        }
    }

    /// <summary>读一个环境变量：没设或只有空白就当没设（顺手去掉包着的引号）。</summary>
    public static string? Get(string name) =>
        Unquote(Environment.GetEnvironmentVariable(name)) is { Length: > 0 } value ? value : null;

    private static string Unquote(string? value) =>
        (value ?? string.Empty).Trim().Trim('"').Trim('\'').Trim();
}
