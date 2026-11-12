using FreeSql;
using Sevencat.Core.Annotation;
using Sevencat.Core.Model;

namespace Sevencat.Core.Dataset.TinyLabel;

/// <summary>
/// TinyLabel 库上几个被多处复用的读逻辑：类别表摊平成 classId 索引、图片行按
/// 「相对路径 → 绝对路径 → 文件名」依次匹配。
/// <para>
/// 放在这里而不是 <see cref="TinyLabelDatabase"/> 上：数据库对象只管打开、结构与生命周期，
/// 这些是标注读写与数据集存储都要用的查询，跟「库开在哪」无关。
/// </para>
/// </summary>
internal static class TinyLabelQueries
{
    /// <summary>按 <c>OrderIndex</c> 取类别表；中间空洞补空串，保证 classId 与下标一致。</summary>
    internal static ClassCatalog LoadClasses(IFreeSql orm, long projectId)
    {
        List<ProjectClassEntity> rows = orm.Select<ProjectClassEntity>()
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
    internal static ImageEntity? FindImage(IFreeSql orm, long projectId, ImageRef image)
    {
        string relative = image.RelativePath.Replace('\\', '/');

        ImageEntity? found = orm.Select<ImageEntity>()
            .Where(row => row.ProjectId == projectId && row.RelPath == relative)
            .First();

        if (found is not null)
        {
            return found;
        }

        found = orm.Select<ImageEntity>()
            .Where(row => row.ProjectId == projectId && row.FilePath == relative)
            .First();

        if (found is not null)
        {
            return found;
        }

        string fileName = Path.GetFileName(relative);

        return orm.Select<ImageEntity>()
            .Where(row => row.ProjectId == projectId && row.FileName == fileName)
            .OrderBy(row => row.RelPath)
            .First();
    }
}
