using Avalonia.Controls;
using Avalonia.Interactivity;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Label.App.ViewModels;

namespace Sevencat.Label.App.Views;

/// <summary>新建项目对话框：只收名称与备注，创建动作由主窗口执行。</summary>
public partial class ProjectEditorWindow : Window
{
    public ProjectEditorWindow() => InitializeComponent();

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        string name = NameBox.Text?.Trim() ?? string.Empty;

        if (name.Length == 0)
        {
            ErrorText.Text = "项目名不能为空";
            return;
        }

        if (name.Length > TinyLabelProjectService.MaxNameLength)
        {
            ErrorText.Text = $"项目名不能超过 {TinyLabelProjectService.MaxNameLength} 个字符";
            return;
        }

        Close(new ProjectDraft(name, DescriptionBox.Text?.Trim() ?? string.Empty));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
