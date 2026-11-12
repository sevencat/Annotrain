using Microsoft.Extensions.Logging;

namespace Sevencat.Core.Infrastructure.Logging;

/// <summary>一条日志记录；界面日志面板直接消费这个结构。</summary>
public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message)
{
    public override string ToString()
        => $"{Timestamp:HH:mm:ss} [{Level}] {Category}: {Message}";
}
