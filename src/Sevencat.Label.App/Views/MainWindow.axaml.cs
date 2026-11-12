using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Sevencat.Core.Dataset;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Infrastructure;
using Sevencat.Core.Localization;
using Sevencat.Core.Storage;
using Sevencat.Label;
using Sevencat.Label.App.ViewModels;

namespace Sevencat.Label.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private static Localizer Loc => Localizer.Instance;

    private IServiceProvider? Services => (Application.Current as App)?.Services;

    // ── 数据目录 / 刷新 ──────────────────────────────────────────────────────

    private async void OnOpenWorkspace(object? sender, RoutedEventArgs e)
    {
        string? folder = await PickFolderAsync(Loc.Get("Label.PickDataDir"));

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

    // ── 项目管理 ─────────────────────────────────────────────────────────────

    private async void OnCreateProject(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        // 「新建项目」不建数据库：目录里还没有 annotrain.db 时先问用户要不要建空库
        if (!viewModel.HasDatabase)
        {
            var ask = new ConfirmWindow(
                Loc.Get("Confirm.NewDatabaseTitle"),
                Loc.Format("Confirm.NewDatabaseMessage", viewModel.DatabasePath),
                Loc.Get("Common.Create"));

            if (!await ask.ShowDialog<bool>(this))
            {
                return;
            }

            try
            {
                viewModel.EnsureDatabase();
                await viewModel.RefreshProjectsAsync();
            }
            catch (Exception error)
            {
                viewModel.Report(Loc.Format("Label.ProjectCreateFailed", error.Message));
                return;
            }
        }

        var dialog = new ProjectEditorWindow();
        ProjectDraft? draft = await dialog.ShowDialog<ProjectDraft?>(this);

        if (draft is not null)
        {
            await viewModel.CreateProjectAsync(draft);
        }
    }

    private async void OnEditProject(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (viewModel.SelectedProject is not { } project)
        {
            viewModel.Status = Loc.Get("Label.PickProjectFirst");
            return;
        }

        var dialog = new ProjectEditorWindow(project.Name, project.Summary.Description);
        ProjectDraft? draft = await dialog.ShowDialog<ProjectDraft?>(this);

        if (draft is not null)
        {
            await viewModel.UpdateProjectAsync(draft);
        }
    }

    private async void OnDeleteProject(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (viewModel.SelectedProject is not { } project)
        {
            viewModel.Status = Loc.Get("Label.PickProjectFirst");
            return;
        }

        var confirm = new ConfirmWindow(
            Loc.Get("Confirm.DeleteTitle"),
            Loc.Format("Confirm.DeleteMessage", project.Name),
            Loc.Get("Common.Delete"));

        if (await confirm.ShowDialog<bool>(this))
        {
            await viewModel.DeleteProjectAsync();
        }
    }

    private async void OnToggleClosed(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && viewModel.SelectedProject is not null)
        {
            await viewModel.ToggleProjectClosedAsync();
        }
    }

    // ── 图片入库 ─────────────────────────────────────────────────────────────

    private async void OnImportImages(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;

        if (storage is null)
        {
            return;
        }

        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.Get("Label.ImportImagesTitle"),
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType(Loc.Get("Label.ImagesFileType"))
                {
                    Patterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp", "*.webp", "*.tif", "*.tiff"],
                },
            ],
        });

        string[] paths = [.. files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)];

        if (paths.Length > 0)
        {
            await viewModel.ImportImagesAsync(paths);
        }
    }

    private async void OnImportDataset(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        LabelDatasetImportService? importer = Services?.GetService<LabelDatasetImportService>();

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

    private async void OnImportFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        string? folder = await PickFolderAsync(Loc.Get("Label.ImportFolderTitle"));

        if (folder is not null)
        {
            await viewModel.ImportFolderAsync(folder, copy: true);
        }
    }

    private async void OnScanFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        string? folder = await PickFolderAsync(Loc.Get("Label.ScanFolderTitle"));

        if (folder is not null)
        {
            await viewModel.ImportFolderAsync(folder, copy: false);
        }
    }

    // ── 产出 ─────────────────────────────────────────────────────────────────

    private async void OnExportDataset(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (viewModel.SelectedProject is null)
        {
            viewModel.Status = Loc.Get("Label.PickProjectFirst");
            return;
        }

        var dialog = new ExportDatasetWindow(viewModel.DataDirectory);
        ExportRequest? request = await dialog.ShowDialog<ExportRequest?>(this);

        if (request is not null)
        {
            await viewModel.ExportAsync(request.Format, request.Destination);
        }
    }

    private void OnOpenProjectFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (viewModel.SelectedProject is not { } project)
        {
            viewModel.Status = Loc.Get("Label.PickProjectFirst");
            return;
        }

        string folder = Path.Combine(viewModel.DataDirectory, "projects", project.Id.ToString());

        if (!Directory.Exists(folder))
        {
            folder = viewModel.DataDirectory;
        }

        OpenInExplorer(folder);
    }

    private async void OnManageClasses(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (viewModel.SelectedProject is not { } project)
        {
            viewModel.Status = Loc.Get("Label.PickProjectFirst");
            return;
        }

        TinyLabelClassService? service = Services?.GetService<TinyLabelClassService>();

        if (service is null)
        {
            return;
        }

        var dialog = new ClassEditorWindow(service, StorageLocation.FromPath(viewModel.DataDirectory), project.Id);
        bool changed = await dialog.ShowDialog<bool>(this);

        if (changed)
        {
            await viewModel.RefreshCurrentProjectAsync(viewModel.SelectedImage?.Id);
        }
    }

    // ── 标注 ─────────────────────────────────────────────────────────────────

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
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (item is null)
        {
            viewModel.Report("先选一行图片再标注");
            return;
        }

        if (viewModel.SelectedProject is null || string.IsNullOrWhiteSpace(viewModel.DatabasePath))
        {
            viewModel.Report(Loc.Get("Label.PickProjectFirst"));
            return;
        }

        IServiceProvider? services = Services;

        if (services is null)
        {
            viewModel.Report("服务容器不可用，标注窗口打不开");
            return;
        }

        try
        {
            AppSettings settings = services.GetRequiredService<AppSettings>();
            SettingsStore settingsStore = services.GetRequiredService<SettingsStore>();
            BlobStoreRegistry registry = services.GetRequiredService<BlobStoreRegistry>();

            long projectId = viewModel.SelectedProject.Id;
            string databasePath = viewModel.DatabasePath;

            ProjectImageViewModel? current = item;

            while (current is not null)
            {
                var window = new AnnotationWindow(
                    databasePath,
                    projectId,
                    current.Row,
                    viewModel.Classes,
                    registry.Get(current.Image.Location),
                    settings,
                    settingsStore);

                AnnotationOutcome outcome = await window.ShowDialog<AnnotationOutcome>(this);

                if (outcome == AnnotationOutcome.Cancelled)
                {
                    return;
                }

                await viewModel.RefreshCurrentProjectAsync(current.Id);

                if (outcome == AnnotationOutcome.Saved)
                {
                    return;
                }

                // 保存并继续：领下一张还没标的图
                current = viewModel.Images.FirstOrDefault(image => image.StateText == "未标注" && image != current);

                if (current is null)
                {
                    viewModel.Report("这一页没有待标注的图片了，可以翻下一页");
                    return;
                }
            }
        }
        catch (Exception error)
        {
            viewModel.Report($"打开标注窗口失败：{error.Message}");
        }
    }

    // ── 分页 ─────────────────────────────────────────────────────────────────

    private async void OnPrevPage(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.TurnPageAsync(-1);
        }
    }

    private async void OnNextPage(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.TurnPageAsync(1);
        }
    }

    // ── 内部 ─────────────────────────────────────────────────────────────────

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

    private static void OpenInExplorer(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folder}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // 打不开资源管理器不影响程序
        }
    }
}
