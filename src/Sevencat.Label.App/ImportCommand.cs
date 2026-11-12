using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sevencat.Core.Dataset;
using Sevencat.Core.Dataset.Import;
using Sevencat.Core.Infrastructure;
using Sevencat.Core.Storage;
using Sevencat.Label;

namespace Sevencat.Label.App;

/// <summary>
/// <c>Sevencat.Label.App --import &lt;源目录&gt; --output &lt;目标目录&gt;</c>：
/// 不开窗口，把现成的 YOLO / RF-DETR 数据集导成标注项目。用于脚本化与 CI。
/// </summary>
internal sealed record ImportCommand(
    string SourcePath,
    string DestinationPath,
    string? ProjectName = null,
    DatasetFormat? Format = null,
    IReadOnlyList<string>? Splits = null)
{
    public static bool TryParse(string[] args, out ImportCommand command)
    {
        command = null!;

        if (!args.Contains("--import", StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        string? source = null;
        string? destination = null;
        string? name = null;
        DatasetFormat? format = null;
        IReadOnlyList<string>? splits = null;

        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];

            switch (arg.ToLowerInvariant())
            {
                case "--import":
                    source = TakeValue(ref index, args);
                    break;

                case "--output" or "-o":
                    destination = TakeValue(ref index, args);
                    break;

                case "--name":
                    name = TakeValue(ref index, args);
                    break;

                case "--format":
                    format = DatasetFormats.ParseId(TakeValue(ref index, args));
                    break;

                case "--split" or "--splits":
                    splits = (TakeValue(ref index, args) ?? string.Empty)
                        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.Error.WriteLine("--import 需要一个源数据集目录");
            return false;
        }

        destination ??= Path.Combine(AppPaths.ConfigDirectory, "imported");
        command = new ImportCommand(
            source,
            destination,
            name,
            format,
            splits is { Count: > 0 } ? splits : null);

        return true;
    }

    /// <summary>取下一个参数；下一个是 <c>--xxx</c> 时视为没有值（避免把开关当成路径）。</summary>
    private static string? TakeValue(ref int index, string[] args)
    {
        if (index + 1 >= args.Length)
        {
            return null;
        }

        string value = args[index + 1];
        if (value.StartsWith("--", StringComparison.Ordinal))
        {
            return null;
        }

        index++;
        return value;
    }
}

internal static class ImportCommandRunner
{
    public static async Task<int> RunAsync(ImportCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        AttachConsole(-1);

        var services = new ServiceCollection();
        services.AddAnnotrainCore("label");
        services.AddAnnotrainLabel();

        using ServiceProvider provider = services.BuildServiceProvider();

        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Import");
        var importer = provider.GetRequiredService<LabelDatasetImportService>();

        try
        {
            DatasetProbe probe = await importer.DetectAsync(command.SourcePath).ConfigureAwait(false);
            Console.WriteLine($"源数据集：{probe.Format.ToDisplayName()}（{probe.Message}）");

            if (probe.Format == DatasetFormat.Ambiguous && command.Format is null)
            {
                foreach (DatasetCandidate candidate in probe.Candidates)
                {
                    Console.WriteLine($"  - {candidate.Format.ToId()}: {candidate.DescriptorPath ?? "(无描述文件)"}");
                }

                Console.Error.WriteLine("同时命中多种格式，请用 --format yolo|coco 指定");
                return 1;
            }

            DatasetImportReport report = await importer
                .ImportAsync(
                    new DatasetImportOptions(
                        StorageLocation.Parse(command.SourcePath),
                        StorageLocation.Parse(command.DestinationPath),
                        command.ProjectName,
                        command.Format,
                        command.Splits))
                .ConfigureAwait(false);

            string text = BuildReport(report);
            Console.WriteLine(text);

            string reportPath = Path.Combine(command.DestinationPath, "import-report.txt");
            await File.WriteAllTextAsync(reportPath, text, new UTF8Encoding(false)).ConfigureAwait(false);

            logger.LogInformation("导入{Result}，报告：{Report}", report.HasWarnings ? "完成（有告警）" : "完成", reportPath);

            return 0;
        }
        catch (Exception error)
        {
            logger.LogError(error, "导入失败");
            Console.Error.WriteLine($"导入失败：{error.Message}");
            return 2;
        }
    }

    private static string BuildReport(DatasetImportReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine(report.Summary);
        builder.AppendLine();
        builder.AppendLine($"源：{report.Source}（{report.SourceFormat.ToDisplayName()}）");
        builder.AppendLine($"库：{report.DatabasePath}（项目 {report.ProjectId}）");

        foreach ((string split, int count) in report.ImagesPerSplit)
        {
            builder.AppendLine($"  {split}: {count} 张");
        }

        builder.AppendLine($"类别：{string.Join("、", report.Classes)}");
        builder.AppendLine($"有标注：{report.AnnotatedImages} 张；跳过：{report.ImagesSkipped} 张；丢弃框：{report.BoxesDropped} 个");

        foreach (string warning in report.Warnings.Take(20))
        {
            builder.AppendLine($"  ! {warning}");
        }

        return builder.ToString();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
