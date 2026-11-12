using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sevencat.Core.Dataset;
using Sevencat.Core.Infrastructure;
using Sevencat.Core.Simulation;

namespace Sevencat.Train.App;

/// <summary>
/// <c>Sevencat.Train.App --simulate &lt;目录&gt;</c>：不开窗口，直接跑一遍端到端模拟流程，
/// 把报告写进工作目录。用于自测 / CI / 演示这条链路是活的。
/// </summary>
internal sealed record SimulationCommand(
    string WorkspaceRoot,
    int Count = 8,
    int Width = 320,
    int Height = 240,
    bool ExportYolo = true)
{
    public static bool TryParse(string[] args, out SimulationCommand command)
    {
        command = null!;

        if (!args.Contains("--simulate", StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        string? workspace = null;
        int count = 8;
        int width = 320;
        int height = 240;
        bool export = true;

        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            string? Next() => index + 1 < args.Length ? args[index + 1] : null;

            switch (arg.ToLowerInvariant())
            {
                case "--simulate":
                    workspace = Next() ?? workspace;
                    if (workspace is not null && !workspace.StartsWith("--", StringComparison.Ordinal))
                    {
                        index++;
                    }

                    break;

                case "--count" when int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedCount):
                    count = Math.Max(1, parsedCount);
                    index++;
                    break;

                case "--width" when int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedWidth):
                    width = Math.Max(64, parsedWidth);
                    index++;
                    break;

                case "--height" when int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedHeight):
                    height = Math.Max(64, parsedHeight);
                    index++;
                    break;

                case "--no-export":
                    export = false;
                    break;
            }
        }

        workspace ??= Path.Combine(AppPaths.ConfigDirectory, "simulation");

        command = new SimulationCommand(workspace, count, width, height, export);
        return true;
    }
}

internal static class SimulationCommandRunner
{
    public static async Task<int> RunAsync(SimulationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        AttachConsole(-1);

        var services = new ServiceCollection();
        services.AddAnnotrainCore("train");
        using ServiceProvider provider = services.BuildServiceProvider();

        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Simulation");
        var pipeline = provider.GetRequiredService<SimulationPipeline>();

        Console.WriteLine($"模拟流程开始：{command.WorkspaceRoot}");

        SimulationReport report = await pipeline.RunAsync(new SimulationOptions(
            command.WorkspaceRoot,
            new SyntheticOptions(command.Count, command.Width, command.Height),
            new SyntheticImportOptions(Path.Combine(command.WorkspaceRoot, SimulationPipeline.DatasetFolder)),
            command.ExportYolo));

        string text = BuildReport(report);
        Console.WriteLine(text);

        string reportPath = Path.Combine(report.WorkspaceRoot, "simulation-report.txt");
        await File.WriteAllTextAsync(reportPath, text, new UTF8Encoding(false)).ConfigureAwait(false);

        logger.LogInformation("模拟流程{Result}，报告：{Report}", report.Passed ? "通过" : "失败", reportPath);

        return report.Passed ? 0 : 2;
    }

    private static string BuildReport(SimulationReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine(report.Summary);
        builder.AppendLine();

        foreach (string step in report.Steps)
        {
            builder.AppendLine($"- {step}");
        }

        builder.AppendLine();
        builder.AppendLine($"数据库：{report.DatabasePath}（项目 {report.ProjectId}）");
        builder.AppendLine($"验证：期望 {report.Verification.ExpectedBoxes} 框 / 实际 {report.Verification.ActualBoxes} 框，" +
                           $"缺失 {report.Verification.Missing}，多余 {report.Verification.Extra}，" +
                           $"最大偏差 {report.Verification.MaxCoordinateError:0.##}px（容差 {report.Verification.Tolerance:0.##}px）");

        foreach (string detail in report.Verification.Details.Take(20))
        {
            builder.AppendLine($"  ! {detail}");
        }

        return builder.ToString();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
