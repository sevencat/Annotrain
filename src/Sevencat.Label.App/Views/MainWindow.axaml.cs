using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Sevencat.Core.Infrastructure;
using Sevencat.Core.Storage;
using Sevencat.Label;
using Sevencat.Label.App.ViewModels;

namespace Sevencat.Label.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private async void OnOpenWorkspace(object? sender, RoutedEventArgs e)
    {
        string? folder = await PickFolderAsync("选择数据目录（放 annotrain.db 的目录）");
        if (folder is not null && DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.OpenWorkspaceAsync(folder);
        }
    }

    private async void OnRefresh(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.RefreshProjectsAsync();
        }
    }

    private async void OnCreateProject(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(viewModel.DataDirectory))
        {
            viewModel.Status = "先打开数据目录，新项目会建在那个数据库里";
            return;
        }

        var dialog = new ProjectEditorWindow();
        ProjectDraft? draft = await dialog.ShowDialog<ProjectDraft?>(this);

        if (draft is not null)
        {
            await viewModel.CreateProjectAsync(draft);
        }
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(viewModel.DataDirectory))
        {
            viewModel.Status = "先打开数据目录，导入的项目会放进那个数据库";
            return;
        }

        LabelDatasetImportService? importer = (Application.Current as App)?.Services
            .GetService<LabelDatasetImportService>();

        if (importer is null)
        {
            return;
        }

        var dialog = new ImportDialog(viewModel.DataDirectory, importer);
        ImportRequest? request = await dialog.ShowDialog<ImportRequest?>(this);

        if (request is not null)
        {
            await viewModel.ImportAsync(request);
        }
    }

    /// <summary>「开始标注」：标当前选中行，没有选中就标第一张。</summary>
    private async void OnStartAnnotation(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            await AnnotateAsync(viewModel.SelectedImage ?? viewModel.Images.FirstOrDefault());
        }
    }

    /// <summary>表格里每行的「标注」按钮。</summary>
    private async void OnAnnotateRow(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ProjectImageViewModel item)
        {
            await AnnotateAsync(item);
        }
    }

    /// <summary>供命令行入口使用：对当前选中行（没有就第一张）打开标注对话框。</summary>
    public Task AnnotateSelectedAsync()
        => DataContext is MainWindowViewModel viewModel
            ? AnnotateAsync(viewModel.SelectedImage ?? viewModel.Images.FirstOrDefault())
            : Task.CompletedTask;

    private async Task AnnotateAsync(ProjectImageViewModel? item)
    {
        if (DataContext is not MainWindowViewModel viewModel || item is null)
        {
            return;
        }

        if (viewModel.SelectedProject is null || string.IsNullOrWhiteSpace(viewModel.DatabasePath))
        {
            viewModel.Status = "先选择一个项目";
            return;
        }

        IServiceProvider? services = (Application.Current as App)?.Services;
        BlobStoreRegistry? registry = services?.GetService<BlobStoreRegistry>();

        if (services is null || registry is null)
        {
            return;
        }

        AppSettings settings = services.GetRequiredService<AppSettings>();
        SettingsStore settingsStore = services.GetRequiredService<SettingsStore>();
        long projectId = viewModel.SelectedProject.Id;
        string databasePath = viewModel.DatabasePath;

        ProjectImageViewModel? current = item;

        while (current is not null)
        {
            var window = new AnnotationWindow(
                databasePath,
                projectId,
                current.Image,
                viewModel.Classes,
                registry.Get(current.Sample.Image.Location),
                settings,
                settingsStore);

            AnnotationOutcome outcome = await window.ShowDialog<AnnotationOutcome>(this);

            if (outcome == AnnotationOutcome.Cancelled)
            {
                return;
            }

            await viewModel.RefreshCurrentProjectAsync(current.RelativePath);

            if (outcome == AnnotationOutcome.Saved)
            {
                return;
            }

            // 保存并继续：领下一张还没框的图
            current = viewModel.Images.FirstOrDefault(image => image.BoxCount == 0 && image != current);

            if (current is null)
            {
                viewModel.Status = "没有待标注的图片了";
                return;
            }
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
