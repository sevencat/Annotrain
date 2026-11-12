using Avalonia.Controls;
using Avalonia.Interactivity;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Localization;

namespace Sevencat.Train.App.Views;

/// <summary>选择框的返回值：<see cref="Project"/> 为 <c>null</c> 表示用户要求换一个库。</summary>
public sealed record ProjectPickResult(TinyLabelProjectSummary? Project)
{
    public static ProjectPickResult ChangeLibrary { get; } = new((TinyLabelProjectSummary?)null);

    public bool IsChangeLibrary => Project is null;
}

/// <summary>
/// 从 TinyLabel 库里挑一个项目来训练：显示所属库、默认选中上次那个，
/// 也能就地换成别的库文件。
/// </summary>
public partial class ProjectPickerDialog : Window
{
    private readonly IReadOnlyList<TinyLabelProjectSummary> _projects;

    public ProjectPickerDialog()
        : this([], string.Empty, 0)
    {
    }

    public ProjectPickerDialog(
        IReadOnlyList<TinyLabelProjectSummary> projects,
        string databaseFile,
        long lastProjectId)
    {
        InitializeComponent();

        _projects = projects;

        LibraryText.Text = Localizer.Instance.Format("Train.LibraryFile", Path.GetFileName(databaseFile));

        ProjectList.ItemsSource = projects
            .Select(project => Localizer.Instance.Format(
                "Train.ProjectRow", project.Name, project.Id, project.ImageCount, project.LabeledCount))
            .ToList();

        // 默认选中上次训练的那个项目；找不到就选第一个
        int index = projects.ToList().FindIndex(project => project.Id == lastProjectId);
        ProjectList.SelectedIndex = index >= 0 ? index : 0;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (Selected is { } project)
        {
            Close(new ProjectPickResult(project));
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnChangeLibrary(object? sender, RoutedEventArgs e) => Close(ProjectPickResult.ChangeLibrary);

    private TinyLabelProjectSummary? Selected
    {
        get
        {
            int index = ProjectList.SelectedIndex;
            return index >= 0 && index < _projects.Count ? _projects[index] : null;
        }
    }
}
