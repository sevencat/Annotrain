using Sevencat.Core.Dataset;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Storage;

namespace Sevencat.Label;

/// <summary>项目里的一张图：所属 split + 内存样本（尺寸与框）。</summary>
public sealed record ProjectImage(string Split, DatasetSample Sample)
{
    public string FileName => Sample.Image.FileName;

    public int BoxCount => Sample.Boxes.Count;
}

/// <summary>打开后的项目快照：图片清单与类别，纯数据，不持有数据库连接。</summary>
public sealed record OpenedProject(
    StorageLocation Folder,
    string ProjectName,
    IReadOnlyList<string> Splits,
    IReadOnlyList<ProjectImage> Images)
{
    public int BoxCount => Images.Sum(image => image.BoxCount);

    public IReadOnlyList<ProjectImage> OfSplit(string split)
        => [.. Images.Where(image => string.Equals(image.Split, split, StringComparison.OrdinalIgnoreCase))];
}

/// <summary>
/// 打开「图片目录 + SQLite」项目并列出图片与标注。
/// 只做数据装载：读完即关库，图片字节由界面通过 <c>ImageRef</c> 自行按需读取。
/// </summary>
public sealed class LabelProjectBrowser(DatasetStoreResolver resolver)
{
    /// <summary><paramref name="folder"/> 是数据集根（<c>annotrain.db</c> 所在目录）。</summary>
    public async Task<OpenedProject> OpenAsync(string folder, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        DatasetOpenResult opened = await resolver
            .OpenAsync(StorageLocation.Parse(folder), DatasetFormat.TinyLabelDb, datasetId: null, ct)
            .ConfigureAwait(false);

        try
        {
            var images = new List<ProjectImage>();

            foreach (string split in opened.Store.Splits)
            {
                IReadOnlyList<DatasetSample> samples = await opened.Store
                    .OpenSplit(split)
                    .LoadSamplesAsync(ct)
                    .ConfigureAwait(false);

                foreach (DatasetSample sample in samples)
                {
                    images.Add(new ProjectImage(split, sample));
                }
            }

            string name = (opened.Store as TinyLabelDatasetStore)?.ProjectName ?? opened.Store.Id;

            return new OpenedProject(opened.Store.Root, name, [.. opened.Store.Splits], images);
        }
        finally
        {
            (opened.Store as IDisposable)?.Dispose();
        }
    }
}
