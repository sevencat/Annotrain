using Avalonia.Controls;
using Avalonia.Interactivity;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Localization;
using Sevencat.Label.App.ViewModels;

namespace Sevencat.Label.App.Views;

/// <summary>新建 / 编辑项目对话框：只收名称与备注，写库由主窗口执行。</summary>
public partial class ProjectEditorWindow : Window
{
    /// <summary>XAML 预览用。</summary>
    public ProjectEditorWindow() => InitializeComponent();

    /// <summary>编辑模式：标题与输入框预填当前项目。</summary>
    public ProjectEditorWindow(string name, string description) : this()
    {
        Localizer loc = Localizer.Instance;

        Title = loc.Get("Project.EditTitle");
        HeaderText.Text = loc.Get("Project.EditTitle");
        HintText.Text = loc.Get("Project.EditHint");
        ConfirmButton.Content = loc.Get("Common.Save");
        NameBox.Text = name;
        DescriptionBox.Text = description;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        Localizer loc = Localizer.Instance;
        string name = NameBox.Text?.Trim() ?? string.Empty;

        if (name.Length == 0)
        {
            ErrorText.Text = loc.Get("Project.NameRequired");
            return;
        }

        if (name.Length > TinyLabelProjectService.MaxNameLength)
        {
            ErrorText.Text = loc.Format("Project.NameTooLong", TinyLabelProjectService.MaxNameLength);
            return;
        }

        Close(new ProjectDraft(name, DescriptionBox.Text?.Trim() ?? string.Empty));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
