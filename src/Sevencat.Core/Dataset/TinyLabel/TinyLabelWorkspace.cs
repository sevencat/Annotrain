using System.Globalization;
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
    /// <summary>项目图片目录的文件夹名：数据目录下的 <c>projects/</c>。</summary>
    public const string ProjectsFolderName = "projects";

    /// <summary>项目内原图的子目录名：<c>projects/{id}/images/</c>。</summary>
    public const string ImagesFolderName = "images";

    /// <summary>项目内缩略图的子目录名：<c>projects/{id}/thumbs/</c>（结构与原图镜像，扩展名统一 jpg）。</summary>
    public const string ThumbsFolderName = "thumbs";

    /// <summary>
    /// 项目目录的相对前缀 <c>projects/{id}</c>：入库的 <c>RelPath</c> / 写盘用的 key 都从它开始，
    /// 别在别处再拼一遍字符串。
    /// </summary>
    public static string ProjectPrefix(long projectId)
        => $"{ProjectsFolderName}/{projectId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>项目内原图目录的相对前缀 <c>projects/{id}/images</c>。</summary>
    public static string ImagePrefix(long projectId)
        => $"{ProjectPrefix(projectId)}/{ImagesFolderName}";

    /// <summary>项目内缩略图目录的相对前缀 <c>projects/{id}/thumbs</c>。</summary>
    public static string ThumbPrefix(long projectId)
        => $"{ProjectPrefix(projectId)}/{ThumbsFolderName}";

    /// <summary>项目目录的绝对路径：<c>数据目录/projects/{id}</c>。</summary>
    public static string ProjectDirectory(StorageLocation root, long projectId)
    {
        ArgumentNullException.ThrowIfNull(root);

        return Path.Combine(root.Root, ProjectsFolderName, projectId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>项目原图目录的绝对路径：<c>数据目录/projects/{id}/images</c>。</summary>
    public static string ImageDirectory(StorageLocation root, long projectId)
        => Path.Combine(ProjectDirectory(root, projectId), ImagesFolderName);

    /// <summary>项目缩略图目录的绝对路径：<c>数据目录/projects/{id}/thumbs</c>。</summary>
    public static string ThumbDirectory(StorageLocation root, long projectId)
        => Path.Combine(ProjectDirectory(root, projectId), ThumbsFolderName);

    /// <summary>
    /// 建项目目录（已存在则跳过），返回绝对路径；新建项目时顺手调用。
    /// <c>images/</c> 与 <c>thumbs/</c> 一并建好，用户打开目录时结构一眼能看懂。
    /// </summary>
    public static string EnsureProjectDirectory(StorageLocation root, long projectId)
    {
        string directory = ProjectDirectory(root, projectId);
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, ImagesFolderName));
        Directory.CreateDirectory(Path.Combine(directory, ThumbsFolderName));

        return directory;
    }

    /// <summary>数据目录里是否已经有库文件。</summary>
    public static bool DatabaseExists(StorageLocation root)
    {
        ArgumentNullException.ThrowIfNull(root);

        return root.IsFile && File.Exists(DatabasePath(root));
    }

    /// <summary>数据目录里的库文件路径（唯一拼路径的地方）。</summary>
    public static string DatabasePath(StorageLocation root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (!root.IsFile)
        {
            throw new NotSupportedException(Text.Get("Error.DatabaseRequiresLocalFolder"));
        }

        return Path.Combine(root.Root, TinyLabelProjectCatalog.DefaultDatabaseFile);
    }

    /// <summary>
    /// 要求当前工作库可用：启动时已经定过库路径就直接用；还没定就按数据目录初始化（库必须存在）。
    /// 库不存在时抛异常，绝不顺手建库。
    /// </summary>
    public static void RequireDatabase(StorageLocation root)
    {
        if (TinyLabelDatabase.IsInitialized)
        {
            return;
        }

        string databasePath = DatabasePath(root);

        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException(
                Text.Format("Error.DatabaseMissing", TinyLabelProjectCatalog.DefaultDatabaseFile),
                databasePath);
        }

        TinyLabelDatabase.Initialize(databasePath, createIfMissing: false);
    }

    /// <summary>
    /// 让数据目录可用并返回它的库路径：没有当前库就在这里建一个（只建表，不建项目）；
    /// 已经有当前库时，<paramref name="root"/> 必须就是库所在目录——库路径启动时定死，不能借这个入口写第二个库。
    /// </summary>
    public static string EnsureDatabase(StorageLocation root)
    {
        ArgumentNullException.ThrowIfNull(root);

        string databasePath = DatabasePath(root);

        if (TinyLabelDatabase.IsInitialized)
        {
            string folder = Path.GetDirectoryName(TinyLabelDatabase.CurrentPath) ?? string.Empty;

            if (!string.Equals(
                    Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(root.Root).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    Text.Format("Error.DatabasePathLocked", TinyLabelDatabase.CurrentPath, databasePath));
            }

            return TinyLabelDatabase.CurrentPath;
        }

        Directory.CreateDirectory(root.Root);
        TinyLabelDatabase.Initialize(databasePath);

        return databasePath;
    }
}
