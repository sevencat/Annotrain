using FreeSql;
using Sevencat.Core.Localization;
using Sevencat.Core.Model;

namespace Sevencat.Core.Dataset.TinyLabel;

/// <summary>
/// 「图片目录 + SQLite」形态的数据库入口：FreeSql + SQLite（WAL）。
/// 表结构、索引都写在实体上，打开旧库时由 <c>SyncStructure</c> 自动补齐缺的表/列/索引。
/// DB 必须落在本地可写路径（WAL 需要随机写），图片可以另外指向远端。
/// <para>
/// 职责只有三件：开库（含 WAL 与结构同步）、拿着 <see cref="Path"/> 与 <see cref="Orm"/>、负责 <see cref="Dispose"/>。
/// 查询请在 <see cref="IFreeSql"/> 上直接写，或复用 <see cref="TinyLabelQueries"/>，不要往这个类上加业务方法。
/// </para>
/// <para>
/// 打开一次就是一个实例，进程里只该有一个：启动时由容器（<c>AddAnnotrainDatabase</c>）建好注册成单例，
/// 需要库的类注入 <see cref="IFreeSql"/>（或这个实例本身），别再每次拼路径开一遍——
/// FreeSql 自带连接池，建表检查、PRAGMA、结构同步只该做一次。库路径启动时定死，运行期不换库。
/// </para>
/// </summary>
public sealed class TinyLabelDatabase : IDisposable
{
    /// <summary>由 <see cref="Open"/> 打开的实例要负责关；不是本实例打开的（理论上没有）就不动它。</summary>
    private readonly bool _owned;

    private bool _disposed;

    private TinyLabelDatabase(IFreeSql orm, string path, bool owned)
    {
        Orm = orm;
        Path = path;
        _owned = owned;
    }

    public IFreeSql Orm { get; }

    /// <summary>数据库文件绝对路径。</summary>
    public string Path { get; }

    /// <summary>
    /// 打开库文件：文件不存在且 <paramref name="createIfMissing"/> 为真就建空库并同步结构。
    /// 返回值自己负责 <see cref="Dispose"/>（容器里的那份由容器释放）。
    /// </summary>
    public static TinyLabelDatabase Open(string path, bool createIfMissing = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Create(System.IO.Path.GetFullPath(path), createIfMissing);
    }

    /// <summary>
    /// 只读打开别人的库（例如训练端选了标注端那份 <c>annotrain.db</c>）：
    /// 不建库、不 <c>SyncStructure</c>，只拿连接查数据。调用方负责 <see cref="Dispose"/>。
    /// </summary>
    public static TinyLabelDatabase OpenReadOnly(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string full = System.IO.Path.GetFullPath(path);

        if (!File.Exists(full))
        {
            throw new FileNotFoundException(Text.Format("Error.DatabaseFileMissing", full), full);
        }

        var orm = new FreeSqlBuilder()
            .UseConnectionString(DataType.Sqlite, $"Data Source={full};Default Timeout=5;Pooling=True;")
            .UseAutoSyncStructure(false)
            .Build();

        return new TinyLabelDatabase(orm, full, owned: true);
    }

    private static TinyLabelDatabase Create(string full, bool createIfMissing)
    {
        bool isNew = !File.Exists(full);

        if (isNew && !createIfMissing)
        {
            throw new FileNotFoundException(Text.Format("Error.DatabaseFileMissing", full), full);
        }

        string? directory = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var orm = new FreeSqlBuilder()
            .UseConnectionString(DataType.Sqlite, $"Data Source={full};Default Timeout=5;Pooling=True;")
            // 结构显式同步，避免运行期隐式改表
            .UseAutoSyncStructure(false)
            .Build();

        var database = new TinyLabelDatabase(orm, full, owned: true);
        database.EnsureSchema(isNew);
        return database;
    }

    /// <summary>
    /// 结构全部由实体定义（<c>[Table]</c> / <c>[Column]</c> / <c>[Index]</c>）推导：新库先开 WAL，
    /// 然后四个实体一律走 <c>SyncStructure</c>——新库建表建索引，旧库缺的表/列/索引由 FreeSql 比对后自动补齐。
    /// 库文件本身不存在时才由 <paramref name="createIfMissing"/> 决定，这里不再探测表。
    /// </summary>
    private void EnsureSchema(bool isNew)
    {
        if (isNew)
        {
            Orm.Ado.ExecuteNonQuery("PRAGMA journal_mode=WAL;");
        }

        Orm.CodeFirst.SyncStructure<ProjectEntity>();
        Orm.CodeFirst.SyncStructure<ProjectClassEntity>();
        Orm.CodeFirst.SyncStructure<ImageEntity>();
        Orm.CodeFirst.SyncStructure<AnnotationEntity>();
    }

    public void Dispose()
    {
        if (!_owned || _disposed)
        {
            return;
        }

        _disposed = true;
        Orm.Dispose();
    }
}
