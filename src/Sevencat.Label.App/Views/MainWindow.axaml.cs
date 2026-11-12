using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Sevencat.Label.App.ViewModels;

namespace Sevencat.Label.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private async void OnOpenProject(object? sender, RoutedEventArgs e)
    {
        string? folder = await PickFolderAsync("选择项目目录（含 annotrain.db）");
        if (folder is not null && DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.OpenProjectAsync(folder);
        }
    }

    private async void OnPickSource(object? sender, RoutedEventArgs e)
    {
        string? folder = await PickFolderAsync("选择要导入的数据集目录");
        if (folder is not null && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.ImportSource = folder;
        }
    }

    private async void OnPickDestination(object? sender, RoutedEventArgs e)
    {
        string? folder = await PickFolderAsync("选择目标目录");
        if (folder is not null && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.ImportDestination = folder;
        }
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.ImportAsync();
        }
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = false });

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }
}
