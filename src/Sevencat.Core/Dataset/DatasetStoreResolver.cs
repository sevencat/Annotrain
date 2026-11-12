using Microsoft.Extensions.Logging;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Dataset;

/// <summary>多种形态同时命中，必须先让用户选一种再打开。</summary>
public sealed class DatasetFormatAmbiguousException(DatasetProbe probe) : Exception(
    $"数据集同时命中 {probe.Candidates.Count} 种格式（{string.Join("、", probe.Candidates.Select(c => c.Format.ToDisplayName()))}），请先指定形态")
{
    public DatasetProbe Probe { get; } = probe;
}

/// <summary>没有 Provider 能处理该形态（VOC 只识别不解析、未识别等）。</summary>
public sealed class DatasetFormatNotSupportedException(DatasetFormat format, string? detail = null)
    : Exception($"暂不支持打开「{format.ToDisplayName()}」形态的数据集{(detail is null ? string.Empty : $"：{detail}")}")
{
    public DatasetFormat Format { get; } = format;
}

/// <summary>打开结果：探测结论 + 已就绪的数据集。</summary>
public sealed record DatasetOpenResult(DatasetProbe Probe, IDatasetStore Store);

/// <summary>
/// 探测 + 打开的统一入口：先探测，再按形态找 Provider，歧义时要求显式指定。
/// UI 与 Web 只依赖这个类，不直接认 Provider。
/// </summary>
public sealed class DatasetStoreResolver(
    IEnumerable<IDatasetStoreProvider> providers,
    IDatasetProbe probe,
    ILogger<DatasetStoreResolver>? logger = null)
{
    private readonly IDatasetStoreProvider[] _providers = [.. providers];

    public Task<DatasetProbe> DetectAsync(StorageLocation location, CancellationToken ct = default)
        => probe.DetectAsync(location, ct);

    public IDatasetStoreProvider? FindProvider(DatasetFormat format)
        => _providers.FirstOrDefault(provider => provider.Format == format);

    /// <summary>
    /// 探测并打开。<paramref name="format"/> 为空时用探测结论；
    /// 探测为歧义（<see cref="DatasetFormat.Ambiguous"/>）时必须显式传入用户的选择。
    /// </summary>
    public async Task<DatasetOpenResult> OpenAsync(
        StorageLocation location,
        DatasetFormat? format = null,
        string? datasetId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(location);

        DatasetProbe detected = await probe.DetectAsync(location, ct).ConfigureAwait(false);
        DatasetFormat target = format ?? detected.Format;

        if (target == DatasetFormat.Ambiguous)
        {
            throw new DatasetFormatAmbiguousException(detected);
        }

        if (target == DatasetFormat.Unknown)
        {
            throw new DatasetFormatNotSupportedException(target, detected.Message);
        }

        IDatasetStoreProvider provider = FindProvider(target)
            ?? throw new DatasetFormatNotSupportedException(target, detected.Message);

        // 歧义时 DescriptorPath 只是第一个候选的入口，必须换成用户所选形态自己的入口
        DatasetCandidate? candidate = detected.CandidateOf(target);
        DatasetProbe effective = detected with
        {
            Format = target,
            DescriptorPath = candidate?.DescriptorPath ?? detected.DescriptorPath,
        };

        IDatasetStore store = await provider.OpenAsync(effective, datasetId, ct).ConfigureAwait(false);

        logger?.LogInformation(
            "数据集已打开：{Format}，根目录 {Root}，split {Splits}",
            store.Format,
            store.Root,
            string.Join(",", store.Splits));

        return new DatasetOpenResult(effective, store);
    }
}
