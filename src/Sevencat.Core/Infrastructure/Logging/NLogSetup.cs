using System.Text;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Targets.Wrappers;

namespace Sevencat.Core.Infrastructure.Logging;

/// <summary>
/// NLog 统一配置：默认按天滚动落盘到用户配置目录，DEBUG 追加调试器输出，并把事件同时送进
/// <see cref="LogBuffer"/> 供界面日志面板展示。
/// 允许用 <c>%APPDATA%/Annotrain/nlog.config</c> 覆盖默认规则；覆盖时仍会补上缓冲 target。
/// </summary>
public static class NLogSetup
{
    /// <summary>落盘布局：时间|级别|logger|消息|异常。</summary>
    public const string FileLayout =
        "${longdate}|${level:uppercase=true:padding=-5}|${logger}|${message}${onexception:${newline}${exception:format=ToString}}";

    /// <summary>外部配置文件名（放在 <see cref="AppPaths.ConfigDirectory"/> 下）。</summary>
    public const string ConfigFileName = "nlog.config";

    private const string BufferTargetName = "ui-buffer";

    /// <summary>日志保留天数（按天归档）。</summary>
    private const int MaxArchiveDays = 14;

    public static void Configure(LogBuffer buffer, string logDirectory, string applicationName)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);

        LogManager.ThrowConfigExceptions = false;

        try
        {
            Directory.CreateDirectory(logDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 目录建不出来也别炸：缓冲 target 仍然可用
        }

        LoggingConfiguration config = TryLoadExternal() ?? BuildDefault(logDirectory, applicationName);

        if (config.FindTargetByName(BufferTargetName) is null)
        {
            var bufferTarget = new LogBufferTarget(buffer);
            config.AddTarget(BufferTargetName, bufferTarget);
            config.AddRule(LogLevel.Trace, LogLevel.Fatal, bufferTarget);
        }

        LogManager.Configuration = config;
    }

    private static LoggingConfiguration? TryLoadExternal()
    {
        string path = Path.Combine(AppPaths.ConfigDirectory, ConfigFileName);

        try
        {
            return File.Exists(path) ? new XmlLoggingConfiguration(path) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NLogConfigurationException)
        {
            return null;
        }
    }

    private static LoggingConfiguration BuildDefault(string logDirectory, string applicationName)
    {
        var config = new LoggingConfiguration();

        var file = new FileTarget("file")
        {
            FileName = Path.Combine(logDirectory, $"{applicationName}.log"),
            Layout = FileLayout,
            Encoding = Encoding.UTF8,
            CreateDirs = true,
            KeepFileOpen = false,
            ArchiveEvery = FileArchivePeriod.Day,
            MaxArchiveDays = MaxArchiveDays,
        };

        // 写盘放后台队列，避免磁盘抖动卡住 UI 线程
        var asyncFile = new AsyncTargetWrapper("async-file", file)
        {
            QueueLimit = 10_000,
            OverflowAction = AsyncTargetWrapperOverflowAction.Discard,
            TimeToSleepBetweenBatches = 50,
        };

        config.AddTarget(asyncFile);
        config.AddRule(LogLevel.Debug, LogLevel.Fatal, asyncFile);

#if DEBUG
        var debugger = new DebuggerTarget("debugger") { Layout = FileLayout };
        config.AddTarget(debugger);
        config.AddRule(LogLevel.Trace, LogLevel.Fatal, debugger);
#endif

        return config;
    }
}
