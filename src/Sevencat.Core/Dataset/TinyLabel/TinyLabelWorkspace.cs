using Sevencat.Core.Localization;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Dataset.TinyLabel;

/// <summary>
/// 数据目录（<c>annotrain.db</c> 所在目录）的初始化与检查。
/// <para>
/// 语义要分清：**数据目录**是库的容器，**项目**是库里的一行。
/// 「新建项目」只往已有的库里插一行；库里没有项目 ≠ 需要新建库。
/// </para>
/// </summary>
public static class TinyLabelWorkspace
{
    /// <summary>数据目录里是否已经有库文件。</summary>
    public static bool DatabaseExists(StorageLocation root)
    {
        ArgumentNullException.ThrowIfNull(root);

        return root.IsFile
               && File.Exists(Path.Combine(root.Root, TinyLabelProjectCatalog.DefaultDatabaseFile));
    }

    /// <summary>取库文件路径；库不存在时抛异常，绝不顺手建库。</summary>
    public static string RequireDatabase(StorageLocation root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (!root.IsFile)
        {
            throw new NotSupportedException(Text.Get("Error.DatabaseRequiresLocalFolder"));
        }

        string databasePath = Path.Combine(root.Root, TinyLabelProjectCatalog.DefaultDatabaseFile);

        return File.Exists(databasePath)
            ? databasePath
            : throw new FileNotFoundException(
                Text.Format("Error.DatabaseMissing", TinyLabelProjectCatalog.DefaultDatabaseFile),
                databasePath);
    }

    /// <summary>
    /// 打开数据目录时初始化空库（仅建表，不建项目）。
    /// 这是"数据目录初始化"，与「新建项目」是两件事。
    /// </summary>
    public static string EnsureDatabase(StorageLocation root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (!root.IsFile)
        {
            throw new NotSupportedException(Text.Get("Error.DatabaseRequiresLocalFolder"));
        }

        Directory.CreateDirectory(root.Root);

        string databasePath = Path.Combine(root.Root, TinyLabelProjectCatalog.DefaultDatabaseFile);

        if (!File.Exists(databasePath))
        {
            using TinyLabelDatabase database = TinyLabelDatabase.Open(databasePath);
        }

        return databasePath;
    }
}
