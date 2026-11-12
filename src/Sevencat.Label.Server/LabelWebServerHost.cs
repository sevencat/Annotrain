using Microsoft.Extensions.Logging;
using Sevencat.Core.Infrastructure;

namespace Sevencat.Label.Server;

/// <summary>
/// Web 服务的生命周期外壳，容器里只注册它：<see cref="Start"/> 时建服务并起监听，
/// 端口被占等失败只记日志（桌面端照常用）；容器退出时统一 Stop + Dispose。
/// </summary>
public sealed class LabelWebServerHost(
    IServiceProvider services,
    ServerOptions options,
    ILoggerFactory loggerFactory) : IAsyncDisposable
{
    private ILogger Logger => loggerFactory.CreateLogger("Sevencat.Label.Server");

    private LabelWebServer? _server;

    /// <summary>监听地址；没起来也照样能显示（界面上的 Web 地址不依赖启动结果）。</summary>
    public string Url => options.Url;

    /// <summary>启动服务；已经失败过一次就什么都不做，不再反复试。</summary>
    public void Start()
    {
        if (_server is not null)
        {
            return;
        }

        try
        {
            _server = LabelWebServer.Create(options, services, loggerFactory);
            _server.StartAsync().GetAwaiter().GetResult();
            Logger.LogInformation("标注 Web 端已启动：{Url}", _server.Url);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "标注 Web 端启动失败（{Url} 可能被占用），桌面端继续", options.Url);
            _server = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is null)
        {
            return;
        }

        try
        {
            await _server.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "标注 Web 端停止时报错");
        }

        await _server.DisposeAsync().ConfigureAwait(false);
        _server = null;
    }
}
