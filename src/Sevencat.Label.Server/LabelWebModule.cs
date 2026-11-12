namespace Sevencat.Label.Server;

/// <summary>
/// 标注 Web 服务入口标记：M3 在这里落 ServerHost / Endpoints / 内嵌页面（前端源码在
/// <c>src/Sevencat.Label.Web</c>，构建产物压成 <c>WebUI/index.html.gz</c> 编进本程序集）。
/// </summary>
public static class LabelWebModule
{
    public const string Name = "Label.Server";

    /// <summary>只绑本机回环：改端口不改它，想暴露到局域网得先想清楚鉴权怎么办。</summary>
    public const string Host = "127.0.0.1";

    /// <summary>默认端口。</summary>
    public const int DefaultPort = 5180;

    /// <summary>默认监听地址（由默认端口拼出）。</summary>
    public static readonly string DefaultUrl = BuildUrl(DefaultPort);

    private const string Scheme = "http://";

    /// <summary>监听地址由端口拼出来（<c>http://127.0.0.1:{端口}</c>），外面只需要配端口。</summary>
    public static string BuildUrl(int port) => $"{Scheme}{Host}:{port}";
}
