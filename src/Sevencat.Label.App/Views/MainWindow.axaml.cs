using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FreeSql;
using Injectio.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Sevencat.Core.Dataset;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Infrastructure;
using Sevencat.Core.Localization;
using Sevencat.Core.Storage;
using Sevencat.Label;
using Sevencat.Label.App.ViewModels;
using Sevencat.Label.Server;
using Sevencat.Label.Server.Data;

namespace Sevencat.Label.App.Views;

/// <summary>主窗口：注册由 Injectio 生成（容器里单例，见 <see cref="App"/> 的注册链）。</summary>
[RegisterSingleton<MainWindow>]
public partial class MainWindow : Window
{
    // 由容器注入；XAML 预览用的无参构造下都是 null（预览里点按钮不做事）
    private readonly WebUserStore? _users;
    private readonly LabelDatasetImportService? _importer;
    private readonly TinyLabelClassService? _classService;
    private readonly AppSettings? _settings;
    private readonly SettingsStore? _settingsStore;
    private readonly BlobStoreRegistry? _registry;
    private readonly IFreeSql? _orm;

    /// <summary>XAML 预览用。</summary>
    public MainWindow() => InitializeComponent();

    public MainWindow(
        MainWindowViewModel viewModel,
        StartupOptions startup,
        WebUserStore users,
        LabelDatasetImportService importer,
        TinyLabelClassService classService,
        AppSettings settings,
        SettingsStore settingsStore,
        BlobStoreRegistry registry,
        IFreeSql orm) : this()
    {
        _users = users;
        _importer = importer;
        _classService = classService;
        _settings = settings;
        _settingsStore = settingsStore;
        _registry = registry;
        _orm = orm;

        DataContext = viewModel;

        // 命令行指定了目录就打开它（--annotate 再进首图标注），否则回到上次的数据目录
        Opened += async (_, _) => await OpenStartupWorkspaceAsync(viewModel, startup);
    }

    private static Localizer Loc => Localizer.Instance;

    private async Task OpenStartupWorkspaceAsync(MainWindowViewModel viewModel, StartupOptions startup)
    {
        if (startup.AnnotateFolder is { } annotateFolder)
        {
            await viewModel.OpenWorkspaceAsync(annotateFolder);
            await AnnotateSelectedAsync();
        }
        else if (startup.OpenFolder is { } folder)
        {
            await viewModel.OpenWorkspaceAsync(folder);
        }
        else
        {
            await viewModel.OpenLastWorkspaceAsync();
        }
    }

    // ── 顶栏：Web 入口 / 数据目录 / 用户 / 关于 / 日志 ──────────────────────────

    private LogWindow? _logWindow;

    /// <summary>「日志」：弹独立窗口（同一次只开一个），跟着日志实时滚到底。</summary>
    private void OnOpenLog(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (_logWindow is { IsVisible: true })
        {
            _logWindow.Activate();
            return;
        }

        _logWindow = new LogWindow(viewModel);
        _logWindow.Closed += (_, _) => _logWindow = null;
        _logWindow.Show(this);
    }

    /// <summary>
    /// 「打开数据目录」：在资源管理器里打开**库文件所在目录**，只是看一眼，
    /// 不选目录、不改库位置（库路径启动时就定了）。
    /// </summary>
    private void OnOpenDataFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        string folder = Path.GetDirectoryName(viewModel.DatabasePath) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            viewModel.Report(Loc.Get("Label.NoDataDir"));
            return;
        }

        if (!OpenInExplorer(folder))
        {
            viewModel.Report(Loc.Format("Label.OpenFolderFailed", folder));
        }
    }

    /// <summary>Web 地址后面的「打开」：交给系统默认浏览器。</summary>
    private void OnOpenWeb(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (!OpenInBrowser(viewModel.WebUrl))
        {
            viewModel.Report(Loc.Format("Label.OpenWebFailed", viewModel.WebUrl));
        }
    }

    private async void OnUserManagement(object? sender, RoutedEventArgs e)
    {
        if (_users is not { } users)
        {
            return;
        }

        // 账号归 Web 服务端管（annotrain.db 的 web_users），窗口里直接增改与重置密码
        var dialog = new UserManagementWindow(users);

        await dialog.ShowDialog(this);
    }

    private async void OnAbout(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var dialog = new AboutWindow(viewModel.WebUrl, viewModel.DataDirectory, viewModel.DatabasePath);
        await dialog.ShowDialog(this);
    }

    // ── 刷新 ─────────────────────────────────────────────────────────────────

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

        // 库在启动时就建好了（空目录也有一个只有表的库），这里直接建项目
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

        if (_importer is not { } importer)
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

        string folder = TinyLabelWorkspace.ProjectDirectory(
            StorageLocation.FromPath(viewModel.DataDirectory),
            project.Id);

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

        if (_classService is not { } service)
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

    /// <summary>行尾 ⋮ 菜单：Flyout 里的 MenuItem 拿不到行的 DataContext，点按钮时先记下这一行。</summary>
    private ProjectImageViewModel? _actionRow;

    private void OnImageActionMenu(object? sender, RoutedEventArgs e)
    {
        _actionRow = (sender as Control)?.Tag as ProjectImageViewModel;
    }

    private async void OnAnnotateImageAction(object? sender, RoutedEventArgs e)
        => await AnnotateAsync(_actionRow ?? (DataContext as MainWindowViewModel)?.SelectedImage);

    /// <summary>菜单「重置为未标注」：只改状态，标注数据留着（与 TinyLabel 一致）。</summary>
    private async void OnResetImageState(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || (_actionRow ?? viewModel.SelectedImage) is not { } row)
        {
            return;
        }

        await viewModel.ResetImageStateAsync(row);
    }

    /// <summary>菜单「清除标注」：删标注数据，先确认。</summary>
    private async void OnClearAnnotation(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || (_actionRow ?? viewModel.SelectedImage) is not { } row)
        {
            return;
        }

        var confirm = new ConfirmWindow(
            Loc.Get("Confirm.ClearAnnotationTitle"),
            Loc.Format("Label.ConfirmClearAnnotation", row.DisplayPath),
            Loc.Get("Common.Clear"));

        if (await confirm.ShowDialog<bool>(this))
        {
            await viewModel.ClearAnnotationForAsync(row);
        }
    }

    /// <summary>菜单「删除图片」：库里的行 + 导入模式下的原图，先确认。</summary>
    private async void OnDeleteImage(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || (_actionRow ?? viewModel.SelectedImage) is not { } row)
        {
            return;
        }

        var confirm = new ConfirmWindow(
            Loc.Get("Confirm.DeleteImageTitle"),
            Loc.Format("Label.ConfirmDeleteImage", row.DisplayPath),
            Loc.Get("Common.Delete"));

        if (await confirm.ShowDialog<bool>(this))
        {
            await viewModel.DeleteImageForAsync(row);
        }
    }

    /// <summary>双击清单行直接进标注，跟 TinyLabel 的行单击选中、双击打开一致。</summary>
    private async void OnRowDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            await AnnotateAsync(viewModel.SelectedImage);
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

        if (_settings is null || _settingsStore is null || _registry is null || _orm is null)
        {
            viewModel.Report("服务容器不可用，标注窗口打不开");
            return;
        }

        try
        {
            long projectId = viewModel.SelectedProject.Id;

            // 一次开窗：当前这张 + 本页后面还没标注的（保存并继续在同窗口换图，不关窗重开）
            List<AnnotationTarget> targets = BuildAnnotationQueue(viewModel, item, _registry);

            var window = new AnnotationWindow(
                _orm,
                projectId,
                targets,
                viewModel.Classes,
                _settings,
                _settingsStore);

            await window.ShowDialog<AnnotationOutcome>(this);

            // 关窗后刷新当前页，并落回最后标的那张
            await viewModel.RefreshCurrentProjectAsync(window.LastImageId ?? item.Id);
        }
        catch (Exception error)
        {
            viewModel.Report($"打开标注窗口失败：{error.Message}");
        }
    }

    /// <summary>连续标注队列：起点这张在前，其后是同页还没标注的图（不绕回已标注的）。</summary>
    private static List<AnnotationTarget> BuildAnnotationQueue(
        MainWindowViewModel viewModel,
        ProjectImageViewModel start,
        BlobStoreRegistry registry)
    {
        var order = new List<ProjectImageViewModel> { start };

        order.AddRange(viewModel.Images.Where(image => !ReferenceEquals(image, start) && image.IsPending));

        return
        [
            .. order.Select(image => new AnnotationTarget(
                image.Row,
                image.Image,
                registry.Get(image.Image.Location))),
        ];
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

    private static bool OpenInExplorer(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folder}\"",
                UseShellExecute = true,
            });

            return true;
        }
        catch (Exception)
        {
            // 打不开资源管理器不影响程序
            return false;
        }
    }

    private static bool OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
