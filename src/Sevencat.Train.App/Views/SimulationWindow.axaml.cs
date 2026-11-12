using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Sevencat.Core.Localization;
using Sevencat.Train.App.ViewModels;

namespace Sevencat.Train.App.Views;

/// <summary>「模拟数据」对话框：只管挑路径和触发生成，逻辑都在 <see cref="SimulationViewModel"/> 里。</summary>
public partial class SimulationWindow : Window
{
    public SimulationWindow() => InitializeComponent();

    public SimulationWindow(SimulationViewModel viewModel) : this() => DataContext = viewModel;

    private SimulationViewModel? ViewModel => DataContext as SimulationViewModel;

    private async void OnGenerate(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        string? report = await viewModel.GenerateAsync();

        if (report is null)
        {
            return;
        }

        // 成功信息单独弹窗：张数、训练/验证划分、每类框数、产物路径
        var dialog = new MessageDialog(Localizer.Instance.Get("Sim.ResultTitle"), report);

        await dialog.ShowDialog(this);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private async void OnBrowseOutput(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        string? folder = await PickFolderAsync(viewModel.OutputDirectory);
        if (folder is not null)
        {
            viewModel.OutputDirectory = folder;
        }
    }

    private async void OnBrowseDataDirectory(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        string? folder = await PickFolderAsync(viewModel.DataDirectory);
        if (folder is not null)
        {
            viewModel.DataDirectory = folder;
        }
    }

    private async void OnBrowseDatabase(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || StorageProvider is null)
        {
            return;
        }

        IStorageFolder? start = null;
        if (!string.IsNullOrWhiteSpace(viewModel.DatabasePath))
        {
            string? folder = Path.GetDirectoryName(viewModel.DatabasePath);
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                start = await StorageProvider.TryGetFolderFromPathAsync(Path.GetFullPath(folder));
            }
        }

        // 选已有的库（不提供"新建"：建库是标注端启动时的事）
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Instance.Get("Sim.Database"),
            AllowMultiple = false,
            SuggestedStartLocation = start,
            FileTypeFilter =
            [
                new FilePickerFileType("SQLite") { Patterns = ["*.db"] },
            ],
        });

        string? path = files.Count == 0 ? null : files[0].TryGetLocalPath();
        if (path is not null)
        {
            viewModel.DatabasePath = path;
        }
    }

    private async Task<string?> PickFolderAsync(string? startAt)
    {
        if (StorageProvider is null)
        {
            return null;
        }

        IStorageFolder? start = null;
        if (!string.IsNullOrWhiteSpace(startAt) && Directory.Exists(startAt))
        {
            start = await StorageProvider.TryGetFolderFromPathAsync(Path.GetFullPath(startAt));
        }

        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }
}
