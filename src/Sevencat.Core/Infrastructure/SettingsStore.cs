using System.Text.Json;
using FreeSql;
using FreeSql.DataAnnotations;
using Sevencat.Core.Dataset.TinyLabel;

namespace Sevencat.Core.Infrastructure;

/// <summary>
/// 配置读写：存在 <c>annotrain.db</c>（与项目库同一个文件）的 <c>settings</c> 表里
/// （键 = <c>app</c>，值 = 整份 JSON）。
/// <para>
/// ORM 从容器注入（<see cref="TinyLabelDatabase"/> 的 <c>IFreeSql</c> 单例），这里不建连接、
/// 也不负责关：配置、账号、项目共用同一个连接池。
/// 读失败返回默认配置，不因配置损坏导致无法启动；首次启动会把旧的
/// <c>%APPDATA%\Sevencat.Annotrain\settings.json</c> 与旧版独立的 <c>data/settings.db</c> 迁进来。
/// </para>
/// </summary>
public sealed class SettingsStore
{
    private const string AppRowKey = "app";

    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IFreeSql _orm;

    public SettingsStore(IFreeSql orm)
    {
        ArgumentNullException.ThrowIfNull(orm);

        _orm = orm;
        _orm.CodeFirst.SyncStructure<SettingEntity>();

        // 旧版数据目录里的 settings.db 比 %APPDATA% 的 settings.json 新，先迁库；两边都有时以库为准
        MigrateLegacyDatabase();
        MigrateLegacyJson();
    }

    /// <summary>配置数据库文件绝对路径（就是当前工作库）。</summary>
    public string FilePath => TinyLabelDatabase.CurrentPath;

    public AppSettings Load()
    {
        try
        {
            string? json = _orm.Select<SettingEntity>()
                .Where(row => row.Key == AppRowKey)
                .First()?.Value;

            if (string.IsNullOrWhiteSpace(json))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string json = JsonSerializer.Serialize(settings, Options);

        // SQLite 的 UPSERT：一条语句搞定"有则更新、无则插入"。
        _orm.Ado.ExecuteNonQuery(
            "INSERT INTO settings (Key, Value) VALUES (@key, @value) "
            + "ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;",
            new { key = AppRowKey, value = json });
    }

    /// <summary>首次启动把旧 JSON 配置搬进数据库（DB 里已有配置就不动）。</summary>
    private void MigrateLegacyJson()
    {
        bool exists = _orm.Select<SettingEntity>().Where(row => row.Key == AppRowKey).Any();

        if (exists || !File.Exists(AppPaths.SettingsFile))
        {
            return;
        }

        try
        {
            AppSettings? legacy = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(AppPaths.SettingsFile),
                Options);

            if (legacy is not null)
            {
                Save(legacy);
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            // 旧配置坏了就忽略：默认配置照样能用
        }
    }

    /// <summary>从旧版独立的 <c>data/settings.db</c> 搬配置（新库里已有配置就不动）。</summary>
    private void MigrateLegacyDatabase()
    {
        string? directory = Path.GetDirectoryName(FilePath);
        string legacy = Path.Combine(directory ?? AppDataPaths.DataDirectory, AppDataPaths.LegacySettingsDatabaseFile);

        if (string.Equals(legacy, FilePath, StringComparison.OrdinalIgnoreCase) || !File.Exists(legacy))
        {
            return;
        }

        if (_orm.Select<SettingEntity>().Where(row => row.Key == AppRowKey).Any())
        {
            return;
        }

        try
        {
            using IFreeSql legacyOrm = new FreeSqlBuilder()
                .UseConnectionString(DataType.Sqlite, $"Data Source={legacy};Default Timeout=5;Pooling=False;")
                .UseAutoSyncStructure(false)
                .Build();

            string? json = legacyOrm.Select<SettingEntity>()
                .Where(row => row.Key == AppRowKey)
                .First()?.Value;

            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            _orm.Ado.ExecuteNonQuery(
                "INSERT INTO settings (Key, Value) VALUES (@key, @value) "
                + "ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;",
                new { key = AppRowKey, value = json });

            Log.Info("已从旧库 {0} 迁移界面配置", legacy);
        }
        catch (Exception error)
        {
            // 旧库读不动就跳过：默认配置照样能用
            Log.Warn(error, "迁移旧界面配置失败，跳过");
        }
    }
}

/// <summary>配置表：一行一个键，Value 是 JSON 文本。</summary>
[Table(Name = "settings")]
public sealed class SettingEntity
{
    [Column(IsPrimary = true, StringLength = 64, IsNullable = false)]
    public string Key { get; set; } = string.Empty;

    [Column(StringLength = -1, IsNullable = false)]
    public string Value { get; set; } = string.Empty;
}
