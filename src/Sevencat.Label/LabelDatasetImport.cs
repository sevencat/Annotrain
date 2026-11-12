using Microsoft.Extensions.Logging;
using Sevencat.Core.Dataset;
using Sevencat.Core.Dataset.Import;
using Sevencat.Core.Storage;

namespace Sevencat.Label;

/// <summary>
/// 标注端的导入入口：把现成的 YOLO / RF-DETR 数据集整份导成可编辑的标注项目
/// （图片复制进 <c>projects/{Id}/</c>、标注转 labelme JSON、train/val 落到 <c>images.Split</c>）。
/// 与打开/导出共用同一套探测与 Provider，只是方向相反。
/// </summary>
public sealed class LabelDatasetImportService(
    DatasetImportService importer,
    ILogger<LabelDatasetImportService>? logger = null)
{
    /// <summary>可导入的来源形态；界面按这个列表给下拉框。</summary>
    public static IReadOnlyList<DatasetFormat> Sources => LabelDatasetPolicy.ImportSources;

    public Task<DatasetProbe> DetectAsync(string sourcePath, CancellationToken ct = default)
        => importer.DetectAsync(ToLocation(sourcePath), ct);

    public Task<DatasetProbe> DetectAsync(StorageLocation source, CancellationToken ct = default)
        => importer.DetectAsync(source, ct);

    /// <summary>
    /// 先探测再按标注端策略校验，最后交给 Core 导入。探测结果复用，不重复扫目录。
    /// </summary>
    public async Task<DatasetImportReport> ImportAsync(
        DatasetImportOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        DatasetProbe probe = await importer.DetectAsync(options.Source, ct).ConfigureAwait(false);
        DatasetFormat format = options.Format ?? probe.Format;

        if (format == DatasetFormat.Ambiguous)
        {
            throw new DatasetFormatAmbiguousException(probe);
        }

        LabelDatasetPolicy.EnsureImportable(format);

        DatasetImportReport report = await importer.ImportAsync(options, probe, ct).ConfigureAwait(false);

        logger?.LogInformation(
            "标注端导入完成：{Summary}，项目 {ProjectId}（源 {Source}）",
            report.Summary,
            report.ProjectId,
            report.Source);

        return report;
    }

    /// <summary>便捷重载：路径 + 项目名即可。</summary>
    public Task<DatasetImportReport> ImportAsync(
        string sourcePath,
        string destinationPath,
        string? projectName = null,
        DatasetFormat? format = null,
        CancellationToken ct = default)
        => ImportAsync(
            new DatasetImportOptions(
                ToLocation(sourcePath),
                ToLocation(destinationPath),
                projectName,
                format),
            ct);

    private static StorageLocation ToLocation(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return StorageLocation.Parse(path);
    }
}
