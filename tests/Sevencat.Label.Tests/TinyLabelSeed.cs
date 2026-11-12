using Sevencat.Core.Dataset.TinyLabel;

namespace Sevencat.Label.Tests;

/// <summary>造一个 TinyLabel 结构的库（主键是自增 long）：项目 + 类别 cat/dog + train/val 两张图。</summary>
internal static class TinyLabelSeed
{
    public const string ProjectImageName = "a.jpg";

    public const string AnnotationJson =
        """
        {"version":"5.0","imagePath":"a.jpg","imageWidth":640,"imageHeight":480,
         "shapes":[{"label":"cat","shape_type":"rectangle","points":[[10,20],[110,120]]}]}
        """;

    public static (string DatabasePath, long ProjectId) Create(
        string root,
        int projectCount = 1,
        string? annotationJson = AnnotationJson)
    {
        string dbPath = Path.Combine(root, "annotrain.db");
        long firstProjectId = 0;

        using var db = TinyLabelDatabase.Open(dbPath);

        for (int index = 1; index <= projectCount; index++)
        {
            long projectId = (long)db.Orm.Insert(new ProjectEntity
            {
                Name = $"项目{index}",
                CreatedAt = new DateTime(2026, 1, index, 0, 0, 0, DateTimeKind.Utc),
            }).ExecuteIdentity();

            db.Orm.Insert(new ProjectClassEntity { ProjectId = projectId, Name = "cat", OrderIndex = 0 }).ExecuteAffrows();
            db.Orm.Insert(new ProjectClassEntity { ProjectId = projectId, Name = "dog", OrderIndex = 1 }).ExecuteAffrows();

            if (index == 1)
            {
                firstProjectId = projectId;
                AddImage(db, projectId, root, ProjectImageName, "train", annotationJson);
                AddImage(db, projectId, root, "b.jpg", "val", null);
            }
        }

        return (dbPath, firstProjectId);
    }

    private static void AddImage(
        TinyLabelDatabase db,
        long projectId,
        string root,
        string relative,
        string split,
        string? annotationJson)
    {
        string full = Path.Combine(
                root,
                "projects",
                projectId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                relative)
            .Replace('\\', '/');

        // 导出会真的去读图片字节，这里造一个最小文件即可（不参与解码）
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, [0xFF, 0xD8, 0xFF, 0xD9]);

        long imageId = (long)db.Orm.Insert(new ImageEntity
        {
            ProjectId = projectId,
            FileName = relative,
            RelPath = relative,
            FilePath = full,
            Width = 640,
            Height = 480,
            Split = split,
        }).ExecuteIdentity();

        if (annotationJson is not null)
        {
            db.Orm.Insert(new AnnotationEntity
            {
                ImageId = imageId,
                ProjectId = projectId,
                Revision = 0,
                DocJson = annotationJson,
            }).ExecuteAffrows();
        }
    }
}
