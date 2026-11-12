using Microsoft.Extensions.Logging;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Dataset.Export;

/// <summary>导出参数。</summary>
/// <param name="TargetFormat">目标形态：<see cref="DatasetFormat.Yolo"/> 或 <see cref="DatasetFormat.Coco"/>。</param>
/// <param name="Destination">目标根目录（必须与源不同）。</param>
/// <param name="Splits">要导出的 split；为空时用源数据集自带的。</param>
/// <param name="CopyImages">是否把图片字节复制过去（默认复制，导出即可直接训练）。</param>
public sealed record DatasetExportOptions(
    DatasetFormat TargetFormat,
    StorageLocation Destination,
    IReadOnlyList<string>? Splits = null,
    bool CopyImages = true);

/// <summary>导出结果：计数 + 丢弃原因，界面直接展示。</summary>
public sealed record DatasetExportReport(
    DatasetFormat TargetFormat,
    StorageLocation Destination,
    IReadOnlyList<string> Splits,
    int ImagesExported,
    int ImagesSkipped,
    int BoxesExported,
    int BoxesDropped,
    IReadOnlyList<string> Warnings,
    TimeSpan Duration)
{
    public bool HasWarnings => Warnings.Count > 0;

    public string Summary =>
        $"导出 {TargetFormat.ToDisplayName()}：{ImagesExported} 张图 / {BoxesExported} 个框" +
        (ImagesSkipped > 0 ? $"，跳过 {ImagesSkipped} 张" : string.Empty) +
        (BoxesDropped > 0 ? $"，丢弃 {BoxesDropped} 个框" : string.Empty);
}

/// <summary>一种目标形态的导出器。</summary>
public interface IDatasetExporter
{
    DatasetFormat TargetFormat { get; }

    Task<DatasetExportReport> ExportAsync(
        IDatasetStore source,
        DatasetExportOptions options,
        CancellationToken ct = default);
}

/// <summary>
/// 导出门面：按目标形态分派到具体导出器。源形态不重要（DB / YOLO / COCO 都能导），
/// 因为一切从 <see cref="IDatasetStore"/> 读，格式差异在各自的 Provider 里消化。
/// </summary>
public sealed class DatasetExportService(
    IEnumerable<IDatasetExporter> exporters,
    ILogger<DatasetExportService>? logger = null)
{
    private readonly IDatasetExporter[] _exporters = [.. exporters];

    public IReadOnlyList<DatasetFormat> Targets => [.. _exporters.Select(exporter => exporter.TargetFormat)];

    public async Task<DatasetExportReport> ExportAsync(
        IDatasetStore source,
        DatasetExportOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        IDatasetExporter exporter = _exporters.FirstOrDefault(item => item.TargetFormat == options.TargetFormat)
            ?? throw new NotSupportedException($"没有可用的导出器：{options.TargetFormat.ToDisplayName()}");

        DatasetExportReport report = await exporter.ExportAsync(source, options, ct).ConfigureAwait(false);

        logger?.LogInformation(
            "数据集导出完成：{Summary}，目标 {Destination}，耗时 {Duration}",
            report.Summary,
            report.Destination,
            report.Duration);

        return report;
    }
}
