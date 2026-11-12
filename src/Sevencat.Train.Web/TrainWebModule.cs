namespace Sevencat.Train.Web;

/// <summary>训练 Web 服务入口标记；M7 实现任务 API、SSE 日志与内嵌页面。</summary>
public static class TrainWebModule
{
    public const string Name = "Train.Web";

    /// <summary>默认监听地址：仅本机回环。</summary>
    public const string DefaultUrl = "http://127.0.0.1:5181";
}
