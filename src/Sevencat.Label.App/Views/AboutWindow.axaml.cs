using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sevencat.Label.App.Views;

/// <summary>「关于」：版本、运行时、Web 地址与当前数据目录 / 库文件位置。</summary>
public partial class AboutWindow : Window
{
    /// <summary>XAML 预览用。</summary>
    public AboutWindow() => InitializeComponent();

    public AboutWindow(string webUrl, string dataDirectory, string databasePath) : this()
    {
        Assembly assembly = typeof(AboutWindow).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        VersionText.Text = string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString(3) ?? "—"
            : informational.Split('+')[0];

        RuntimeText.Text = RuntimeInformation.FrameworkDescription;
        WebText.Text = webUrl;
        DataDirText.Text = string.IsNullOrWhiteSpace(dataDirectory) ? "—" : dataDirectory;
        DatabaseText.Text = string.IsNullOrWhiteSpace(databasePath) ? "—" : databasePath;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
