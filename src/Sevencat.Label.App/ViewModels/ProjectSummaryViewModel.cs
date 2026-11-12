using Sevencat.Core.Dataset;
using Sevencat.Core.Dataset.TinyLabel;

namespace Sevencat.Label.App.ViewModels;

/// <summary>左侧项目列表里的一行。</summary>
public sealed class ProjectSummaryViewModel(TinyLabelProjectSummary summary)
{
    public TinyLabelProjectSummary Summary { get; } = summary;

    public long Id => Summary.Id;

    public string Name => Summary.Name;

    public string Description => Summary.Description;

    /// <summary>"6 张 · 已标 6"。</summary>
    public string Stats => Summary.Summary;

    public string Splits => Summary.Splits.Count == 0
        ? "未划分 split"
        : string.Join(" / ", Summary.Splits);

    public double Progress => Summary.Progress;
}

/// <summary>导入对话框的返回值：源目录 + 项目名 + 已识别形态。</summary>
public sealed record ImportRequest(string SourcePath, string? ProjectName, DatasetFormat Format);

/// <summary>新建项目对话框的返回值。</summary>
public sealed record ProjectDraft(string Name, string Description);
