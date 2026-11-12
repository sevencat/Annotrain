using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sevencat.Train.App.Views;

/// <summary>轻量消息框：一行标题 + 一段可选中复制的正文 + 「知道了」。</summary>
public partial class MessageDialog : Window
{
    public MessageDialog() => InitializeComponent();

    public MessageDialog(string title, string body) : this()
    {
        Title = title;
        Headline.Text = title;
        Body.Text = body;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close();
}
