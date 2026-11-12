using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sevencat.Label.App.Views;

/// <summary>一个够用的确认框：Avalonia 没有内置 MessageBox，删除这类不可逆操作自己弹一个。</summary>
public partial class ConfirmWindow : Window
{
    /// <summary>XAML 预览用。</summary>
    public ConfirmWindow() => InitializeComponent();

    public ConfirmWindow(string title, string message, string confirmText = "确定") : this()
    {
        Title = title;
        HeaderText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
