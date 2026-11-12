using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Model;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Simulation;

/// <summary>模拟"标注程序导入 + 人工标注"的参数。</summary>
/// <param name="DatasetRoot">数据集根目录（DB 与 projects/ 都放这里）。</param>
/// <param name="ProjectName">项目名。</param>
/// <param name="ValRatio">验证集比例（0 表示不划分）。</param>
/// <param name="JitterPixels">模拟人工描框的抖动幅度（像素，±N）。</param>
/// <param name="Seed">抖动用的随机种子。</param>
public sealed record SyntheticImportOptions(
    string DatasetRoot,
    string ProjectName = "模拟项目",
    double ValRatio = 0.25,
    int JitterPixels = 2,
    int? Seed = 20261112);

/// <summary>导入结果。</summary>
public sealed record SyntheticImportResult(
    string DatabasePath,
    long ProjectId,
    int ImageCount,
    int AnnotatedImages,
    int ShapeCount,
    IReadOnlyList<string> Splits);

/// <summary>
/// 把模拟图"导入成项目并标注完"：写图片 + 建 TinyLabel 库 + 建项目/类别/图片 + 写 labelme JSON 标注。
/// 相当于人把每张图里的正方形/三角形/圆形都框了一遍（可带 ±N 像素抖动）。
/// </summary>
public static class SyntheticLabelingSession
{
    public static async Task<SyntheticImportResult> ImportAsync(
        IReadOnlyList<SyntheticImage> images,
        BlobStoreRegistry registry,
        SyntheticImportOptions options,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);

        Directory.CreateDirectory(options.DatasetRoot);

        // 图片必须落在数据集根下，DB 里的 FilePath 才与 blob 路径一致（否则导出时读不到图）
        var location = StorageLocation.FromPath(options.DatasetRoot);
        IBlobStore store = registry.Get(location);

        string databasePath = Path.Combine(options.DatasetRoot, "annotrain.db");
        var random = new Random(options.Seed ?? Environment.TickCount);
        var splits = new List<string>();

        using TinyLabelDatabase database = string.Equals(
            databasePath,
            TinyLabelDatabase.CurrentPath,
            StringComparison.OrdinalIgnoreCase)
            ? TinyLabelDatabase.Current
            : TinyLabelDatabase.Open(databasePath);

        long projectId = (long)database.Orm.Insert(new ProjectEntity
        {
            Name = options.ProjectName,
            Description = "Annotrain 模拟流程生成",
            StorageMode = "import",
            CreatedBy = 0,
            CreatedAt = DateTime.UtcNow,
        }).ExecuteIdentity();

        for (int index = 0; index < SyntheticImageGenerator.ClassNames.Count; index++)
        {
            database.Orm.Insert(new ProjectClassEntity
            {
                ProjectId = projectId,
                Name = SyntheticImageGenerator.ClassNames[index],
                OrderIndex = index,
                Color = index switch { 0 => "#3B82F6", 1 => "#22C55E", _ => "#EF4444" },
            }).ExecuteAffrows();
        }

        int annotated = 0;
        int shapeCount = 0;

        for (int index = 0; index < images.Count; index++)
        {
            ct.ThrowIfCancellationRequested();

            SyntheticImage image = images[index];
            string relative = $"{TinyLabelWorkspace.ProjectPrefix(projectId)}/{image.FileName}";

            using (var payload = new MemoryStream(image.Png))
            {
                await store
                    .WriteAsync(new StorageKey(location, relative), payload, ct)
                    .ConfigureAwait(false);
            }

            string split = ResolveSplit(index, images.Count, options.ValRatio);
            if (!splits.Contains(split, StringComparer.OrdinalIgnoreCase))
            {
                splits.Add(split);
            }

            string filePath = Path.GetFullPath(Path.Combine(options.DatasetRoot, relative.Replace('/', Path.DirectorySeparatorChar)));

            long imageId = (long)database.Orm.Insert(new ImageEntity
            {
                ProjectId = projectId,
                FileName = image.FileName,
                RelPath = image.FileName,
                FilePath = filePath,
                Width = image.Width,
                Height = image.Height,
                FileSizeBytes = image.Png.Length,
                Sha256 = string.Empty,
                State = "labeled",
                Revision = 1,
                Split = split,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteIdentity();

            if (image.Shapes.Count > 0)
            {
                string docJson = BuildLabelmeJson(image, options.JitterPixels, random);

                database.Orm.Insert(new AnnotationEntity
                {
                    ImageId = imageId,
                    ProjectId = projectId,
                    Revision = 1,
                    DocJson = docJson,
                    UpdatedBy = 0,
                    UpdatedAt = DateTime.UtcNow,
                }).ExecuteAffrows();

                annotated++;
                shapeCount += image.Shapes.Count;
            }
        }

        logger?.LogInformation(
            "模拟导入完成：{Images} 张图、{Annotated} 张有标注、{Shapes} 个框，项目 {ProjectId}，split {Splits}",
            images.Count,
            annotated,
            shapeCount,
            projectId,
            string.Join(",", splits));

        return new SyntheticImportResult(databasePath, projectId, images.Count, annotated, shapeCount, splits);
    }

    /// <summary>按比例交错分配 train/val，并保证两边都有图（除非只有一张）。</summary>
    private static string ResolveSplit(int index, int total, double valRatio)
    {
        if (total <= 1 || valRatio is <= 0 or >= 1)
        {
            return "train";
        }

        int step = Math.Max(2, (int)Math.Round(1 / valRatio));
        bool isVal = (index + 1) % step == 0;

        // 最后一张兜底：全是 train 时把最后一张挪到 val
        if (!isVal && index == total - 1 && (index + 1) < step)
        {
            isVal = true;
        }

        return isVal ? "val" : "train";
    }

    private static string BuildLabelmeJson(SyntheticImage image, int jitterPixels, Random random)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("version", "5.0");
            writer.WriteString("imagePath", image.FileName);
            writer.WriteNumber("imageWidth", image.Width);
            writer.WriteNumber("imageHeight", image.Height);
            writer.WriteStartArray("shapes");

            foreach (SyntheticShape shape in image.Shapes)
            {
                RectD bounds = Jitter(shape.Bounds, image.Width, image.Height, jitterPixels, random);

                writer.WriteStartObject();
                writer.WriteString("label", shape.Label);
                writer.WriteStartArray("points");
                writer.WriteStartArray();
                writer.WriteNumberValue(Math.Round(bounds.X, 1));
                writer.WriteNumberValue(Math.Round(bounds.Y, 1));
                writer.WriteEndArray();
                writer.WriteStartArray();
                writer.WriteNumberValue(Math.Round(bounds.Right, 1));
                writer.WriteNumberValue(Math.Round(bounds.Bottom, 1));
                writer.WriteEndArray();
                writer.WriteEndArray();
                writer.WriteString("shape_type", "rectangle");
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>模拟手标：四条边各自抖 ±N 像素并夹回图内。</summary>
    private static RectD Jitter(RectD bounds, int width, int height, int jitterPixels, Random random)
    {
        if (jitterPixels <= 0)
        {
            return bounds;
        }

        int Offset() => random.Next(-jitterPixels, jitterPixels + 1);

        double left = Math.Clamp(bounds.X + Offset(), 0, width - 1);
        double top = Math.Clamp(bounds.Y + Offset(), 0, height - 1);
        double right = Math.Clamp(bounds.Right + Offset(), left + 1, width);
        double bottom = Math.Clamp(bounds.Bottom + Offset(), top + 1, height);

        return RectD.FromLtrb(left, top, right, bottom);
    }
}
