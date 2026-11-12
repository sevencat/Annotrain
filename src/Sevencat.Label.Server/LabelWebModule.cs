namespace Sevencat.Label.Server;

/// <summary>
/// 标注 Web 服务入口标记：M3 在这里落 ServerHost / Endpoints / 内嵌页面（前端源码在
/// <c>src/Sevencat.Label.Web</c>，构建产物压成 <c>WebUI/index.html.gz</c> 编进本程序集）。
/// </summary>
public static class LabelWebModule
{
    public const string Name = "Label.Server";

    /// <summary>默认监听地址：仅本机回环。</summary>
    public const string DefaultUrl = "http://127.0.0.1:5180";
}
