using System.Text.Json;
using FreeSql;
using FreeSql.DataAnnotations;

namespace Sevencat.Core.Infrastructure;

/// <summary>
/// 配置读写：存在 <c>data/settings.db</c> 的 <c>settings</c> 表里（键 = <c>app</c>，值 = 整份 JSON）。
/// <para>
/// 为什么进数据库而不是 JSON 文件：用户要求「配置也放数据库里」，而且绿色部署时
/// 数据和配置都在 <c>exe/data</c> 下，迁移只拷一个目录。
/// 读失败返回默认配置，不因配置损坏导致无法启动；首次启动会把旧的
/// <c>%APPDATA%\Sevencat.Annotrain\settings.json</c> 迁进来。
/// </para>
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private const string AppRowKey = "app";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IFreeSql _orm;

    public SettingsStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        FilePath = Path.GetFullPath(databasePath);

        string? directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _orm = new FreeSqlBuilder()
            .UseConnectionString(DataType.Sqlite, $"Data Source={FilePath};Default Timeout=5;Pooling=True;")
            .UseAutoSyncStructure(false)
            .Build();

        _orm.Ado.ExecuteNonQuery("PRAGMA journal_mode=WAL;");
        _orm.CodeFirst.SyncStructure<SettingEntity>();

        MigrateLegacyJson();
    }

    /// <summary>配置数据库文件绝对路径。</summary>
    public string FilePath { get; }

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

    public void Dispose() => _orm.Dispose();
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
