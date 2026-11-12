using Sevencat.Core.Dataset;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Localization;

namespace Sevencat.Label.App.ViewModels;

/// <summary>左侧项目列表里的一行。</summary>
public sealed class ProjectSummaryViewModel(TinyLabelProjectSummary summary)
{
    private static Localizer Loc => Localizer.Instance;

    public TinyLabelProjectSummary Summary { get; } = summary;

    public long Id => Summary.Id;

    public string Name => Summary.Name;

    public string Description => string.IsNullOrWhiteSpace(Summary.Description)
        ? "—"
        : Summary.Description;

    /// <summary>「图片 14 张 · 已处理 9 张 · 待处理 5 张」。</summary>
    public string Stats => Loc.Format(
        "Label.ProjectStats",
        Summary.ImageCount,
        Summary.LabeledCount,
        Summary.PendingCount);

    public string CreatedText => Summary.CreatedText;

    /// <summary>「创建 2026-11-12」。</summary>
    public string CreatedLabel => Loc.Format("Label.ProjectCreatedAt", CreatedText);

    /// <summary>列表第二行的「图片 N」，与「已标注 M」分两列显示。</summary>
    public int ImageCount => Summary.ImageCount;

    public int LabeledCount => Summary.LabeledCount;

    /// <summary>「图片 14」。</summary>
    public string ImageCountText => Loc.Format("Label.ProjectImageCount", ImageCount);

    /// <summary>「已标注 9」。</summary>
    public string LabeledCountText => Loc.Format("Label.ProjectLabeledCount", LabeledCount);

    public bool IsClosed => Summary.IsClosed;

    public string Splits => Summary.Splits.Count == 0
        ? Loc.Get("State.NoSplit")
        : string.Join(" / ", Summary.Splits);

    public double Progress => Summary.Progress;

    /// <summary>搜索用：名称 / 备注 / Id 任一命中即可（大小写不敏感）。</summary>
    public bool Matches(string token)
        => Name.Contains(token, StringComparison.OrdinalIgnoreCase)
           || (Summary.Description?.Contains(token, StringComparison.OrdinalIgnoreCase) ?? false)
           || Id.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(token, StringComparison.Ordinal);
}

/// <summary>导入对话框的返回值：源目录 + 项目名 + 已识别形态。</summary>
public sealed record ImportRequest(string SourcePath, string? ProjectName, DatasetFormat Format);

/// <summary>新建 / 编辑项目对话框的返回值。</summary>
public sealed record ProjectDraft(string Name, string Description);

/// <summary>导出数据集对话框的返回值。</summary>
public sealed record ExportRequest(DatasetFormat Format, string Destination);
