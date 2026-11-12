namespace Sevencat.Label.Web;

/// <summary>标注 Web 服务入口标记；M3 迁入 ServerHost、Endpoints 与内嵌页面。</summary>
public static class LabelWebModule
{
    public const string Name = "Label.Web";

    /// <summary>默认监听地址：仅本机回环。</summary>
    public const string DefaultUrl = "http://127.0.0.1:5180";
}
