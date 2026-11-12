using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Localization;
using Sevencat.Core.Model;
using Sevencat.Core.Storage;

namespace Sevencat.Label.App.ViewModels;

/// <summary>
/// 右侧图片清单的一行：路径 / 标注 / 状态 / 版本 / 备注 / 操作。
/// 数据直接来自库里的 <c>images</c> 行，不再经过 Data 层的样本装载。
/// </summary>
public sealed class ProjectImageViewModel(TinyLabelImageRow row)
{
    private static Localizer Loc => Localizer.Instance;

    public TinyLabelImageRow Row { get; } = row;

    public long Id => Row.Id;

    /// <summary>路径列：项目内相对路径（扫描模式是相对扫描根）。</summary>
    public string DisplayPath => Row.RelPath;

    public string FileName => Row.FileName;

    public string RelativePath => Row.RelPath;

    /// <summary>标注列：有标注显示「已标注」，否则「未标注」。</summary>
    public string BoxText => Loc.Get(Row.State == "pending" ? "State.Pending" : "State.Labeled");

    /// <summary>状态列：库里 <c>images.State</c> 的本地化名字。</summary>
    public string StateText => Loc.Get(Row.State switch
    {
        "pending" => "State.Pending",
        "labeled" => "State.Labeled",
        "reviewed" => "State.Reviewed",
        "excluded" => "State.Excluded",
        "skipped" => "State.Skipped",
        _ => "State.Pending",
    });

    public string RevisionText => $"rev {Row.Revision}";

    public string RemarkText => string.IsNullOrWhiteSpace(Row.Remark) ? Loc.Get("State.Empty") : Row.Remark;

    public string SplitText => string.IsNullOrWhiteSpace(Row.Split) ? Loc.Get("State.NoSplit") : Row.Split;

    public string SizeText => Row.SizeText;

    /// <summary>标注窗口用：图片引用（本地绝对路径 → 目录 + 文件名）。</summary>
    public ImageRef Image
    {
        get
        {
            string? directory = Path.GetDirectoryName(Row.FilePath);

            return new ImageRef(
                Path.GetFileName(Row.FilePath),
                StorageLocation.FromPath(string.IsNullOrEmpty(directory) ? "." : directory));
        }
    }

    /// <summary>标注窗口用：尺寸（读不到时退回 0，保存时以库里的宽高为准）。</summary>
    public int Width => Row.Width;

    public int Height => Row.Height;
}
