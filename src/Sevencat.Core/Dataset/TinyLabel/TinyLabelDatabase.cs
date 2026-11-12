using FreeSql;
using Sevencat.Core.Annotation;
using Sevencat.Core.Localization;
using Sevencat.Core.Model;

namespace Sevencat.Core.Dataset.TinyLabel;

/// <summary>
/// 「图片目录 + SQLite」形态的数据库入口：FreeSql + SQLite（WAL）。
/// 表结构沿用 TinyLabel，打开旧库时只补缺少的列（<c>images.Split</c>），不改动其它表。
/// DB 必须落在本地可写路径（WAL 需要随机写），图片可以另外指向远端。
/// </summary>
public sealed class TinyLabelDatabase : IDisposable
{
    /// <summary>多出来的训练划分列。</summary>
    public const string SplitColumn = "Split";

    private TinyLabelDatabase(IFreeSql orm, string path)
    {
        Orm = orm;
        Path = path;
    }

    public IFreeSql Orm { get; }

    /// <summary>数据库文件绝对路径。</summary>
    public string Path { get; }

    /// <summary>打开（必要时创建）数据库文件。</summary>
    public static TinyLabelDatabase Open(string path, bool createIfMissing = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string full = System.IO.Path.GetFullPath(path);

        if (!createIfMissing && !File.Exists(full))
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

        var database = new TinyLabelDatabase(orm, full);
        database.EnsureSchema(createIfMissing);
        return database;
    }

    /// <summary>建表（幂等）并补齐本项目新增的列。</summary>
    private void EnsureSchema(bool createIfMissing)
    {
        bool hasImages = TableExists("images");

        if (!hasImages && !createIfMissing)
        {
            throw new InvalidDataException(Text.Format("Error.InvalidDatabase", Path));
        }

        if (!hasImages)
        {
            Orm.Ado.ExecuteNonQuery("PRAGMA journal_mode=WAL;");
            Orm.CodeFirst.SyncStructure<ProjectEntity>();
            Orm.CodeFirst.SyncStructure<ProjectClassEntity>();
            Orm.CodeFirst.SyncStructure<ImageEntity>();
            Orm.CodeFirst.SyncStructure<AnnotationEntity>();

            Orm.Ado.ExecuteNonQuery(
                "CREATE UNIQUE INDEX IF NOT EXISTS uk_images_project_path ON images(ProjectId, FilePath);");
            Orm.Ado.ExecuteNonQuery(
                "CREATE INDEX IF NOT EXISTS idx_images_project_state ON images(ProjectId, State);");
            Orm.Ado.ExecuteNonQuery(
                "CREATE UNIQUE INDEX IF NOT EXISTS uk_project_classes_name ON project_classes(ProjectId, Name);");

            return;
        }

        if (!ColumnExists("images", SplitColumn))
        {
            Orm.Ado.ExecuteNonQuery($"ALTER TABLE images ADD COLUMN {SplitColumn} TEXT NULL;");
        }
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

    private bool TableExists(string table)
        => Orm.Ado.Query<int>(
            $"SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = '{table}';")[0] > 0;

    private bool ColumnExists(string table, string column)
        => Orm.Ado.Query<int>(
            $"SELECT COUNT(1) FROM pragma_table_info('{table}') WHERE name = '{column}';")[0] > 0;

    public void Dispose() => Orm.Dispose();
}
