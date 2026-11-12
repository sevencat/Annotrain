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

    public bool IsClosed => Summary.IsClosed;

    public string Splits => Summary.Splits.Count == 0
        ? Loc.Get("State.NoSplit")
        : string.Join(" / ", Summary.Splits);

    public double Progress => Summary.Progress;
}

/// <summary>导入对话框的返回值：源目录 + 项目名 + 已识别形态。</summary>
public sealed record ImportRequest(string SourcePath, string? ProjectName, DatasetFormat Format);

/// <summary>新建 / 编辑项目对话框的返回值。</summary>
public sealed record ProjectDraft(string Name, string Description);

/// <summary>导出数据集对话框的返回值。</summary>
public sealed record ExportRequest(DatasetFormat Format, string Destination);
