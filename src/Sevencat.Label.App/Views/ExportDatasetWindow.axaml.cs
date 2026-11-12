using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Sevencat.Core.Dataset;
using Sevencat.Core.Localization;
using Sevencat.Label.App.ViewModels;

namespace Sevencat.Label.App.Views;

/// <summary>导出数据集对话框：选格式与目标目录，导出动作由主窗口执行。</summary>
public partial class ExportDatasetWindow : Window
{
    /// <summary>XAML 预览用。</summary>
    public ExportDatasetWindow() => InitializeComponent();

    public ExportDatasetWindow(string dataDirectory) : this()
        => DestinationBox.Text = Path.Combine(dataDirectory, "export");

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;

        if (storage is null)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = Localizer.Instance.Get("Export.PickTitle"),
                AllowMultiple = false,
            });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            DestinationBox.Text = path;
        }
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        string destination = DestinationBox.Text?.Trim() ?? string.Empty;

        if (destination.Length == 0)
        {
            ErrorText.Text = Localizer.Instance.Get("Export.PickDestination");
            return;
        }

        DatasetFormat format = FormatBox.SelectedIndex == 1 ? DatasetFormat.Coco : DatasetFormat.Yolo;

        Close(new ExportRequest(format, destination));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
