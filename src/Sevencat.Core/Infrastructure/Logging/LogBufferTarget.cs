using NLog;
using NLog.Targets;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Sevencat.Core.Infrastructure.Logging;

/// <summary>把 NLog 事件写进界面日志面板用的内存缓冲，同时保留结构化字段。</summary>
public sealed class LogBufferTarget : TargetWithLayout
{
    private readonly LogBuffer _buffer;

    public LogBufferTarget(LogBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        _buffer = buffer;
        Layout = "${message}${onexception:${newline}${exception:format=ToString}}";
    }

    protected override void Write(LogEventInfo logEvent)
    {
        string message = RenderLogEvent(Layout, logEvent) ?? string.Empty;

        _buffer.Add(new LogEntry(
            new DateTimeOffset(logEvent.TimeStamp),
            Map(logEvent.Level),
            logEvent.LoggerName ?? "nlog",
            message));
    }

    private static MsLogLevel Map(LogLevel level)
    {
        if (level == LogLevel.Fatal)
        {
            return MsLogLevel.Critical;
        }

        if (level == LogLevel.Error)
        {
            return MsLogLevel.Error;
        }

        if (level == LogLevel.Warn)
        {
            return MsLogLevel.Warning;
        }

        if (level == LogLevel.Info)
        {
            return MsLogLevel.Information;
        }

        return level == LogLevel.Debug ? MsLogLevel.Debug : MsLogLevel.Trace;
    }
}
