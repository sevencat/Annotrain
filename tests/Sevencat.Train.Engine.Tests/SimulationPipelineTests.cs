using Microsoft.Extensions.DependencyInjection;
using Sevencat.Core.Dataset;
using Sevencat.Core.Simulation;
using Xunit;

namespace Sevencat.Train.Engine.Tests;

/// <summary>
/// 端到端模拟：生成图 → 导入并标注 → 直接训练（干跑）→ 导出 YOLO → 再训练 → 用真值验证。
/// </summary>
public class SimulationPipelineTests
{
    [Fact]
    public async Task EndToEnd_Simulation_Passes()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "annotrain-simulation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        try
        {
            var services = new ServiceCollection();
            services.AddAnnotrainDatasets();
            using ServiceProvider provider = services.BuildServiceProvider();

            var pipeline = provider.GetRequiredService<SimulationPipeline>();

            SimulationReport report = await pipeline.RunAsync(new SimulationOptions(
                workspace,
                new SyntheticOptions(ImageCount: 8, Width: 320, Height: 240, MinShapes: 1, MaxShapes: 3, Seed: 42),
                new SyntheticImportOptions(Path.Combine(workspace, "dataset"), JitterPixels: 2, Seed: 7)));

            Assert.Equal(8, report.GeneratedImages);
            Assert.True(report.GeneratedShapes >= 8, $"生成形状太少：{report.GeneratedShapes}");
            Assert.Equal(8, report.ImportedImages);
            Assert.Equal(report.GeneratedShapes, report.ImportedBoxes);

            // 直接读数据库训练
            Assert.True(
                report.DatabaseTraining.Images == 8,
                $"训练装载图片数={report.DatabaseTraining.Images}；步骤：{string.Join(" | ", report.Steps)}");
            Assert.True(
                report.DatabaseTraining.Boxes == report.GeneratedShapes,
                $"训练框数={report.DatabaseTraining.Boxes}，真值={report.GeneratedShapes}；步骤：{string.Join(" | ", report.Steps)}");
            Assert.Empty(report.DatabaseTraining.Warnings);

            // 导出 YOLO 再训练
            Assert.NotNull(report.YoloExport);
            Assert.Equal(8, report.YoloExport!.ImagesExported);
            Assert.Empty(report.YoloExport.Warnings);

            Assert.NotNull(report.ExportedTraining);
            Assert.Equal(8, report.ExportedTraining!.Images);
            Assert.Equal(report.GeneratedShapes, report.ExportedTraining.Boxes);

            // 真值反查：不丢不多，坐标误差在容差内
            SimulationVerification verification = report.Verification;
            Assert.True(verification.Passed, string.Join("；", verification.Details));
            Assert.Equal(report.GeneratedShapes, verification.ExpectedBoxes);
            Assert.Equal(report.GeneratedShapes, verification.ActualBoxes);
            Assert.Equal(0, verification.Missing);
            Assert.Equal(0, verification.Extra);
            Assert.True(verification.MaxCoordinateError <= verification.Tolerance);

            Assert.True(report.Passed, report.Summary);

            // 第 1 步确实是"平铺 + 无标注"：只有 png
            string[] flatFiles = Directory.GetFiles(Path.Combine(workspace, SimulationPipeline.FlatFolder));
            Assert.Equal(8, flatFiles.Length);
            Assert.All(flatFiles, file => Assert.EndsWith(".png", file, StringComparison.OrdinalIgnoreCase));

            byte[] png = File.ReadAllBytes(flatFiles[0]);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);

            // 导出产物可用：data.yaml / labels / images
            string yoloRoot = Path.Combine(workspace, SimulationPipeline.YoloFolder);
            Assert.True(File.Exists(Path.Combine(yoloRoot, "data.yaml")));
            Assert.True(Directory.GetFiles(Path.Combine(yoloRoot, "labels", "train"), "*.txt").Length > 0);

            // 训练摘要落盘
            Assert.True(File.Exists(Path.Combine(workspace, SimulationPipeline.DatabaseTrainingFolder, "dataset-summary.json")));
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // SQLite 连接还没完全释放时忽略：临时目录留给系统清理
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
