using FreeSql;
using Sevencat.Core.Annotation;
using Sevencat.Core.Localization;
using Sevencat.Core.Model;

namespace Sevencat.Core.Dataset.TinyLabel;

/// <summary>
/// 「图片目录 + SQLite」形态的数据库入口：FreeSql + SQLite（WAL）。
/// 表结构、索引都写在实体上，打开旧库时由 <c>SyncStructure</c> 自动补齐缺的表/列/索引。
/// DB 必须落在本地可写路径（WAL 需要随机写），图片可以另外指向远端。
/// <para>
/// 当前工作库是启动时 <see cref="Initialize"/> 建好的唯一实例，全局用 <see cref="Current"/> 取，
/// 不要每次拼路径再开一遍：FreeSql 自带连接池，建表检查、PRAGMA、结构同步只该做一次。
/// 库路径在启动时就固定（参数 / 环境变量），运行期不换库。
/// </para>
/// </summary>
public sealed class TinyLabelDatabase : IDisposable
{
    private static readonly object Sync = new();

    private static TinyLabelDatabase? _current;
    private static string? _currentPath;

    /// <summary>独立打开的库（<see cref="Open"/>）要自己负责关；当前工作库不归调用方关。</summary>
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
    /// 当前工作库：启动时 <see cref="Connect"/> 一次，之后全局直接用。
    /// 还没连过（没打开数据目录）时抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    public static TinyLabelDatabase Current => _current
        ?? throw new InvalidOperationException(Text.Get("Error.DatabaseNotInitialized"));

    /// <summary>当前工作库的路径；还没连过时是空串。</summary>
    public static string CurrentPath => _currentPath ?? string.Empty;

    /// <summary>当前工作库是否已经初始化（启动时定过路径）。</summary>
    public static bool IsInitialized => _current is not null;

    /// <summary>
    /// 初始化进程内唯一的当前工作库。路径只在这里定一次（启动参数 <c>--db</c> / 环境变量
    /// <c>ANNOTRAIN_DB</c> / 数据目录）：同一路径重复调用直接返回；换成别的路径直接抛，
    /// 运行期不换库，所以这里不做关闭旧库这种事。
    /// </summary>
    public static TinyLabelDatabase Initialize(string path, bool createIfMissing = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string full = System.IO.Path.GetFullPath(path);

        lock (Sync)
        {
            if (_current is not null)
            {
                if (!string.Equals(_currentPath, full, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        Text.Format("Error.DatabasePathLocked", _currentPath, full));
                }

                return _current;
            }

            _current = Create(full, createIfMissing, owned: false);
            _currentPath = full;
            return _current;
        }
    }

    /// <summary>测试夹具专用：丢掉当前实例，让下一个用例用新的临时目录。生产代码不要调。</summary>
    internal static void Reset()
    {
        lock (Sync)
        {
            _current?.DisposeCore();
            _current = null;
            _currentPath = null;
        }
    }

    /// <summary>
    /// 打开一个**不是**当前工作库的库（导入到别的目录、外部数据集、造模拟数据）：
    /// 独立连接，用完自己 <see cref="Dispose"/>。
    /// </summary>
    public static TinyLabelDatabase Open(string path, bool createIfMissing = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Create(System.IO.Path.GetFullPath(path), createIfMissing, owned: true);
    }

    private static TinyLabelDatabase Create(string full, bool createIfMissing, bool owned)
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

        var database = new TinyLabelDatabase(orm, full, owned);
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

    /// <summary>按 <c>OrderIndex</c> 取类别表；中间空洞补空串，保证 classId 与下标一致。</summary>
    internal ClassCatalog LoadClasses(long projectId)
    {
        List<ProjectClassEntity> rows = Orm.Select<ProjectClassEntity>()
            .Where(row => row.ProjectId == projectId)
            .OrderBy(row => row.OrderIndex)
            .ToList();

        if (rows.Count == 0)
        {
            return ClassCatalog.Empty;
        }

        int count = rows.Max(row => row.OrderIndex) + 1;
        var names = new string[count];

        foreach (ProjectClassEntity row in rows)
        {
            if (row.OrderIndex >= 0 && row.OrderIndex < count)
            {
                names[row.OrderIndex] = row.Name;
            }
        }

        return ClassCatalog.FromNames(names);
    }

    /// <summary>按相对路径 → 绝对路径 → 文件名 依次匹配图片行（三种来源都可能当 RelativePath 传进来）。</summary>
    internal ImageEntity? FindImage(long projectId, ImageRef image)
    {
        string relative = image.RelativePath.Replace('\\', '/');

        ImageEntity? found = Orm.Select<ImageEntity>()
            .Where(row => row.ProjectId == projectId && row.RelPath == relative)
            .First();

        if (found is not null)
        {
            return found;
        }

        found = Orm.Select<ImageEntity>()
            .Where(row => row.ProjectId == projectId && row.FilePath == relative)
            .First();

        if (found is not null)
        {
            return found;
        }

        string fileName = System.IO.Path.GetFileName(relative);

        return Orm.Select<ImageEntity>()
            .Where(row => row.ProjectId == projectId && row.FileName == fileName)
            .OrderBy(row => row.RelPath)
            .First();
    }

    /// <summary>当前工作库不在这里关：连接要留给整个进程用，随进程退出一起结束。</summary>
    public void Dispose()
    {
        if (_owned)
        {
            DisposeCore();
        }
    }

    private void DisposeCore()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Orm.Dispose();
    }
}
