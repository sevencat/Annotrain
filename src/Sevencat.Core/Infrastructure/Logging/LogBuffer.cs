namespace Sevencat.Core.Infrastructure.Logging;

/// <summary>内存环形日志缓冲：供界面面板展示，容量固定，超出丢最旧。</summary>
public sealed class LogBuffer(int capacity = 2000)
{
    private readonly Queue<LogEntry> _entries = new();
    private readonly object _gate = new();

    /// <summary>任意线程添加日志后触发；订阅方需自行切回 UI 线程。</summary>
    public event Action<LogEntry>? EntryAdded;

    public void Add(LogEntry entry)
    {
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > capacity)
            {
                _entries.Dequeue();
            }
        }

        EntryAdded?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
        {
            return [.. _entries];
        }
    }
}
