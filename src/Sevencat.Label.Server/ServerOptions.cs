using Sevencat.Core.Infrastructure;

namespace Sevencat.Label.Server;

/// <summary>标注 Web 服务器的启动参数；桌面端只覆盖 <see cref="Port"/> 与 <see cref="DataDirectory"/>。</summary>
public sealed class ServerOptions
{
    /// <summary>
    /// 环境变量名：监听端口。<c>--data</c> 这类进程级设置都可以手工写进 exe 同级的 <c>.env</c>。
    /// 只配端口不配地址：地址恒为 <c>http://127.0.0.1:{端口}</c>（见 <see cref="LabelWebModule.BuildUrl"/>）。
    /// </summary>
    public const string PortVariable = "ANNOTRAIN_LABEL_PORT";

    /// <summary>监听端口，默认 <see cref="LabelWebModule.DefaultPort"/>。</summary>
    public int Port { get; init; } = LabelWebModule.DefaultPort;

    /// <summary>监听地址，由 <see cref="Port"/> 拼出来，默认只绑本机回环。</summary>
    public string Url => LabelWebModule.BuildUrl(Port);

    /// <summary>数据目录：项目库、配置库、令牌密钥都放这里。</summary>
    public string DataDirectory { get; init; } = AppDataPaths.DataDirectory;

    /// <summary>
    /// 调试用：指到前端的 <c>dist/spa</c> 目录就直接读磁盘上的 index.html，
    /// 不读内嵌页面（改前端不用重新编译 C#）。
    /// </summary>
    public string? WebPageDirectory { get; init; }

    /// <summary>登录令牌的有效期。</summary>
    public TimeSpan TokenLifetime { get; init; } = TimeSpan.FromDays(7);

    /// <summary>令牌签名密钥文件（首次启动生成）。</summary>
    public string TokenKeyPath => Path.Combine(DataDirectory, "web-token.key");

    /// <summary>首次启动生成的初始密码落在这里，便于找回来。</summary>
    public string BootstrapPasswordPath => Path.Combine(DataDirectory, "web-admin.txt");
}
