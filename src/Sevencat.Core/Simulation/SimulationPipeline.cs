using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sevencat.Core.Dataset;
using Sevencat.Core.Dataset.Export;
using Sevencat.Core.Model;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Simulation;

/// <summary>模拟流程参数：工作目录下会生成 flat/ dataset/ export-yolo/ 等子目录。</summary>
public sealed record SimulationOptions(
    string WorkspaceRoot,
    SyntheticOptions? Synthetic = null,
    SyntheticImportOptions? Import = null,
    bool ExportToYolo = true,
    bool TrainOnExported = true);

/// <summary>真值与读回数据的比对结论。</summary>
public sealed record SimulationVerification(
    bool Passed,
    int ExpectedBoxes,
    int ActualBoxes,
    int Missing,
    int Extra,
    double MaxCoordinateError,
    double Tolerance,
    IReadOnlyList<string> Details);

/// <summary>一次模拟流程的完整结果。</summary>
public sealed record SimulationReport(
    string WorkspaceRoot,
    string FlatDirectory,
    int GeneratedImages,
    int GeneratedShapes,
    string DatabasePath,
    long ProjectId,
    int ImportedImages,
    int ImportedBoxes,
    TrainingRunSummary DatabaseTraining,
    DatasetExportReport? YoloExport,
    TrainingRunSummary? ExportedTraining,
    SimulationVerification Verification,
    IReadOnlyList<string> Steps,
    TimeSpan Duration)
{
    public bool Passed => Verification.Passed
                          && DatabaseTraining.Warnings.Count == 0
                          && (ExportedTraining is null || ExportedTraining.Warnings.Count == 0);

    public string Summary =>
        $"生成 {GeneratedImages} 张/{GeneratedShapes} 个形状 → 导入 {ImportedImages} 张/{ImportedBoxes} 个框 → " +
        $"{(YoloExport is null ? "不导出" : $"导出 YOLO {YoloExport.ImagesExported} 张")} → " +
        $"验证 {(Passed ? "通过" : "失败")}（缺 {Verification.Missing}、多 {Verification.Extra}、最大偏差 {Verification.MaxCoordinateError:0.#}px），耗时 {Duration.TotalSeconds:0.0}s";
}

/// <summary>
/// 端到端模拟流程：
/// 1) 生成平铺模拟图（正方形 / 三角形 / 圆形，不带标注）；
/// 2) "标注程序导入"——建数据库项目、写图、把每个形状框出来并落库；
/// 3) 训练程序直接读数据库（干跑：完整装载 + 校验 + 摘要）；
/// 4)（可选）先导出 YOLO，再从导出目录训练，验证两条路径等价；
/// 5) 用真值反查标注与导出结果。
/// </summary>
public sealed class SimulationPipeline(
    BlobStoreRegistry registry,
    DatasetStoreResolver resolver,
    DatasetExportService exporter,
    IDatasetTrainingRunner trainer,
    ILogger<SimulationPipeline>? logger = null)
{
    public const string FlatFolder = "flat";
    public const string DatasetFolder = "dataset";
    public const string YoloFolder = "export-yolo";
    public const string DatabaseTrainingFolder = "training-db";
    public const string ExportedTrainingFolder = "training-yolo";

    public async Task<SimulationReport> RunAsync(SimulationOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkspaceRoot);

        var started = Stopwatch.StartNew();
        var steps = new List<string>();
        string workspace = Path.GetFullPath(options.WorkspaceRoot);
        Directory.CreateDirectory(workspace);

        var root = StorageLocation.FromPath(workspace);
        IBlobStore store = registry.Get(root);

        // 1. 生成模拟图（平铺、无标注）
        SyntheticOptions synthetic = options.Synthetic ?? new SyntheticOptions();
        IReadOnlyList<SyntheticImage> images = new SyntheticImageGenerator(synthetic).Generate();

        int shapeCount = 0;
        foreach (SyntheticImage image in images)
        {
            ct.ThrowIfCancellationRequested();

            using var payload = new MemoryStream(image.Png);
            await store
                .WriteAsync(new StorageKey(root, $"{FlatFolder}/{image.FileName}"), payload, ct)
                .ConfigureAwait(false);

            shapeCount += image.ShapeCount;
        }

        steps.Add($"生成 {images.Count} 张图 / {shapeCount} 个形状 → {Path.Combine(workspace, FlatFolder)}");
        logger?.LogInformation("模拟流程 1/5：生成 {Images} 张图，{Shapes} 个形状", images.Count, shapeCount);

        // 2. 标注程序导入 + 标注
        SyntheticImportOptions import = options.Import
                                        ?? new SyntheticImportOptions(Path.Combine(workspace, DatasetFolder));

        SyntheticImportResult imported = await SyntheticLabelingSession
            .ImportAsync(images, registry, import, logger, ct)
            .ConfigureAwait(false);

        steps.Add($"导入项目 {imported.ProjectId}：{imported.ImageCount} 张图 / {imported.ShapeCount} 个框（split {string.Join(",", imported.Splits)}）");

        var datasetLocation = StorageLocation.FromPath(import.DatasetRoot);
        DatasetOpenResult opened = await resolver.OpenAsync(datasetLocation, ct: ct).ConfigureAwait(false);

        try
        {
            steps.Add($"数据集已打开：{opened.Store.Id}，{opened.Store.Classes.Count} 类，split {string.Join(",", opened.Store.Splits)}");

            // 3. 训练程序直接读数据库
            TrainingRunSummary databaseTraining = await trainer
                .TrainAsync(opened.Store, new TrainingRunOptions(StorageLocation.FromPath(Path.Combine(workspace, DatabaseTrainingFolder))), ct)
                .ConfigureAwait(false);

            steps.Add($"训练（直接读数据库）：{databaseTraining.Summary}");

            // 4. 导出 YOLO 再训练
            DatasetExportReport? yoloExport = null;
            TrainingRunSummary? exportedTraining = null;

            if (options.ExportToYolo)
            {
                var yoloLocation = StorageLocation.FromPath(Path.Combine(workspace, YoloFolder));

                yoloExport = await exporter
                    .ExportAsync(opened.Store, new DatasetExportOptions(DatasetFormat.Yolo, yoloLocation, imported.Splits), ct)
                    .ConfigureAwait(false);

                steps.Add($"导出 YOLO：{yoloExport.Summary}");

                if (options.TrainOnExported)
                {
                    DatasetOpenResult yolo = await resolver.OpenAsync(yoloLocation, ct: ct).ConfigureAwait(false);

                    try
                    {
                        exportedTraining = await trainer
                            .TrainAsync(yolo.Store, new TrainingRunOptions(StorageLocation.FromPath(Path.Combine(workspace, ExportedTrainingFolder))), ct)
                            .ConfigureAwait(false);

                        steps.Add($"训练（读导出目录）：{exportedTraining.Summary}");
                    }
                    finally
                    {
                        (yolo.Store as IDisposable)?.Dispose();
                    }
                }
            }

            // 5. 真值反查
            SimulationVerification verification = await VerifyAsync(images, opened.Store, import.JitterPixels, ct)
                .ConfigureAwait(false);

            steps.Add($"验证：{verification.Details.Count} 条明细（容差 {verification.Tolerance:0.#}px）");

            var report = new SimulationReport(
                workspace,
                Path.Combine(workspace, FlatFolder),
                images.Count,
                shapeCount,
                imported.DatabasePath,
                imported.ProjectId,
                imported.ImageCount,
                imported.ShapeCount,
                databaseTraining,
                yoloExport,
                exportedTraining,
                verification,
                steps,
                started.Elapsed);

            logger?.LogInformation("模拟流程完成：{Summary}", report.Summary);

            return report;
        }
        finally
        {
            (opened.Store as IDisposable)?.Dispose();
        }
    }

    /// <summary>真值 vs 数据集读回的框：按类别分组、按 x 排序后逐个比，误差 = 四条边最大偏差。</summary>
    private static async Task<SimulationVerification> VerifyAsync(
        IReadOnlyList<SyntheticImage> images,
        IDatasetStore store,
        int jitterPixels,
        CancellationToken ct)
    {
        double tolerance = Math.Max(1, jitterPixels + 1);

        var samples = new List<DatasetSample>();
        foreach (string split in store.Splits)
        {
            samples.AddRange(await store.OpenSplit(split).LoadSamplesAsync(ct).ConfigureAwait(false));
        }

        Dictionary<string, DatasetSample> byName = samples
            .GroupBy(sample => Path.GetFileName(sample.Image.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var details = new List<string>();
        int expected = 0;
        int actual = samples.Sum(sample => sample.Boxes.Count);
        int missing = 0;
        int extra = 0;
        double maxError = 0;

        foreach (SyntheticImage image in images)
        {
            expected += image.ShapeCount;

            if (!byName.TryGetValue(image.FileName, out DatasetSample? sample))
            {
                missing += image.ShapeCount;
                details.Add($"缺图：{image.FileName}（真值 {image.ShapeCount} 个框）");
                continue;
            }

            var actualByLabel = sample.Boxes
                .GroupBy(box => NameOf(store, box.ClassId))
                .ToDictionary(group => group.Key, group => group.OrderBy(box => box.X1).ToList(), StringComparer.Ordinal);

            foreach (IGrouping<string, SyntheticShape> expectedGroup in image.Shapes.GroupBy(shape => shape.Label))
            {
                List<SyntheticShape> truth = [.. expectedGroup.OrderBy(shape => shape.Bounds.X)];
                List<BoxAnnotation> found = actualByLabel.TryGetValue(expectedGroup.Key, out List<BoxAnnotation>? list) ? list : [];

                int count = Math.Max(truth.Count, found.Count);
                for (int index = 0; index < count; index++)
                {
                    if (index >= found.Count)
                    {
                        missing++;
                        details.Add($"缺框：{image.FileName} {expectedGroup.Key}#{index}");
                        continue;
                    }

                    if (index >= truth.Count)
                    {
                        extra++;
                        details.Add($"多框：{image.FileName} {expectedGroup.Key}#{index}");
                        continue;
                    }

                    RectD bounds = truth[index].Bounds;
                    BoxAnnotation box = found[index];

                    double error = Math.Max(
                        Math.Max(Math.Abs(box.X1 - bounds.X), Math.Abs(box.Y1 - bounds.Y)),
                        Math.Max(Math.Abs(box.X2 - bounds.Right), Math.Abs(box.Y2 - bounds.Bottom)));

                    maxError = Math.Max(maxError, error);

                    if (error > tolerance)
                    {
                        details.Add($"坐标偏差 {error:0.#}px > {tolerance:0.#}px：{image.FileName} {expectedGroup.Key}");
                    }
                }
            }
        }

        bool passed = missing == 0 && extra == 0 && maxError <= tolerance;

        return new SimulationVerification(passed, expected, actual, missing, extra, maxError, tolerance, details);
    }

    private static string NameOf(IDatasetStore store, int classId)
        => classId >= 0 && classId < store.Classes.Count ? store.Classes.NameOf(classId) : "?";
}
