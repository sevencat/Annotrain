using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Sevencat.Core;
using Sevencat.Core.Dataset;
using Sevencat.Core.Dataset.Export;
using Sevencat.Core.Dataset.Import;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Infrastructure;
using Sevencat.Core.Infrastructure.Logging;
using Sevencat.Core.Localization;
using Sevencat.Core.Storage;
using Sevencat.Label;

namespace Sevencat.Label.App.ViewModels;

/// <summary>
/// 标注主窗口的 ViewModel。布局参照 TinyLabel 服务端首页：
/// 左边「项目管理」（新建 / 编辑 / 删除 / 结束 + 项目列表），右边「项目概览 + 图片清单」。
/// <para>
/// 图片清单走 <see cref="TinyLabelImageService"/> 分页查询（状态 / split / 关键词过滤都在库里做），
/// 不再整库装载 labelme JSON；标注窗口按行里的图片引用单独打开。
/// </para>
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    /// <summary>图片清单每页条数。</summary>
    public const int DefaultPageSize = 50;

    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly LabelDatasetImportService _importer;
    private readonly TinyLabelProjectCatalog _catalog;
    private readonly TinyLabelProjectService _projects;
    private readonly TinyLabelImageService _images;
    private readonly TinyLabelClassService _classService;
    private readonly DatasetExportService _exporter;
    private readonly DatasetStoreResolver _resolver;
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;

    /// <summary>切项目时的代次：异步加载回来发现已经切走就丢弃结果。</summary>
    private int _loadVersion;

    /// <summary>当前项目的类别（标注对话框的标签候选项）。</summary>
    private IReadOnlyList<string> _classes = [];

    [ObservableProperty]
    private string _title = Localizer.Instance.Get("Label.Title");

    [ObservableProperty]
    private string _status = Localizer.Instance.Get("Label.Ready");

    [ObservableProperty]
    private string _logText = string.Empty;

    /// <summary>当前数据目录（<c>annotrain.db</c> 所在目录）。</summary>
    [ObservableProperty]
    private string _dataDirectory = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>当前界面语言；切换后写入设置并刷新文案。</summary>
    [ObservableProperty]
    private LanguageOption? _selectedLanguage;

    // ── 左侧：项目管理 ────────────────────────────────────────────────────────

    public ObservableCollection<ProjectSummaryViewModel> Projects { get; } = [];

    [ObservableProperty]
    private ProjectSummaryViewModel? _selectedProject;

    [ObservableProperty]
    private string _projectsSummary = string.Empty;

    // ── 右侧：项目概览 ────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _projectTitle = Localizer.Instance.Get("Label.NoProject");

    [ObservableProperty]
    private string _projectDescription = string.Empty;

    [ObservableProperty]
    private string _projectStats = "—";

    [ObservableProperty]
    private double _projectProgress;

    [ObservableProperty]
    private bool _isProjectClosed;

    /// <summary>「类别管理 (4)」的括号数字。</summary>
    [ObservableProperty]
    private string _categoryText = string.Empty;

    // ── 右侧：图片清单 ────────────────────────────────────────────────────────

    public ObservableCollection<ProjectImageViewModel> Images { get; } = [];

    [ObservableProperty]
    private ProjectImageViewModel? _selectedImage;

    [ObservableProperty]
    private int _pageIndex;

    [ObservableProperty]
    private string _pageText = string.Empty;

    [ObservableProperty]
    private string _totalText = string.Empty;

    [ObservableProperty]
    private bool _canPrevPage;

    [ObservableProperty]
    private bool _canNextPage;

    /// <summary>当前数据目录下的库文件路径；没打开目录时为空。</summary>
    public string DatabasePath => string.IsNullOrWhiteSpace(DataDirectory)
        ? string.Empty
        : Path.Combine(DataDirectory, TinyLabelProjectCatalog.DefaultDatabaseFile);

    public IReadOnlyList<string> Classes => _classes;

    /// <summary>语言下拉的候选。</summary>
    public IReadOnlyList<LanguageOption> Languages => Localizer.Supported;

    private static Localizer Loc => Localizer.Instance;

    public MainWindowViewModel(
        ILogger<MainWindowViewModel> logger,
        LogBuffer logBuffer,
        LabelDatasetImportService importer,
        TinyLabelProjectCatalog catalog,
        TinyLabelProjectService projects,
        TinyLabelImageService images,
        TinyLabelClassService classService,
        DatasetExportService exporter,
        DatasetStoreResolver resolver,
        AppSettings settings,
        SettingsStore settingsStore)
    {
        _logger = logger;
        _importer = importer;
        _catalog = catalog;
        _projects = projects;
        _images = images;
        _classService = classService;
        _exporter = exporter;
        _resolver = resolver;
        _settings = settings;
        _settingsStore = settingsStore;

        DataDirectory = AppDataPaths.DataDirectory;
        _selectedLanguage = Localizer.Supported.FirstOrDefault(option => option.Code == Loc.Language);
        _categoryText = Loc.Format("Label.ManageClasses", 0);
        _pageText = Loc.Format("Label.PageOf", 1, 1);
        _totalText = Loc.Format("Label.TotalImages", 0);
        _projectsSummary = Loc.Format("Label.ProjectsSummary", 0, 0);

        logger.LogInformation("主窗口已创建，数据目录：{Directory}", DataDirectory);

        LogText = string.Join(Environment.NewLine, logBuffer.Snapshot().Select(entry => entry.ToString()));
    }

    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (value is null || string.Equals(value.Code, Loc.Language, StringComparison.Ordinal))
        {
            return;
        }

        Loc.SetLanguage(value.Code);

        _settings.Language = value.Code;
        SaveSettings();

        RefreshStaticTexts();

        // 列表里的状态/统计是拼出来的，切语言后重建一次
        _ = RefreshProjectsAsync(SelectedProject?.Id);
    }

    /// <summary>切换语言后重算不依赖数据的固定文案。</summary>
    private void RefreshStaticTexts()
    {
        Title = Loc.Get("Label.Title");
        Status = Loc.Get("Label.Ready");
        CategoryText = Loc.Format("Label.ManageClasses", _classes.Count);
        PageText = Loc.Format("Label.PageOf", PageIndex + 1, 1);
        TotalText = Loc.Format("Label.TotalImages", 0);
        ProjectsSummary = Loc.Format("Label.ProjectsSummary", Projects.Count, Projects.Count(project => !project.IsClosed));

        if (SelectedProject is null)
        {
            ProjectTitle = Loc.Get("Label.NoProject");
            ProjectStats = "—";
        }
    }

    // ── 数据目录与项目列表 ────────────────────────────────────────────────────

    /// <summary>启动时打开上次的数据目录；没有记录就用 <c>exe/data</c>。</summary>
    public async Task OpenLastWorkspaceAsync(CancellationToken ct = default)
    {
        string folder = string.IsNullOrWhiteSpace(_settings.LastDataDirectory)
            ? AppDataPaths.DataDirectory
            : _settings.LastDataDirectory;

        await OpenWorkspaceAsync(folder, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 打开数据目录：只读库里的项目列表，**不会创建数据库**。
    /// 目录里没有 <c>annotrain.db</c> 时给出提示；需要建库由「新建项目」或「导入数据集」显式触发。
    /// </summary>
    public async Task OpenWorkspaceAsync(string folder, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        DataDirectory = folder;
        _settings.LastDataDirectory = folder;
        SaveSettings();

        Status = Loc.Get("Label.LoadingProjects");

        await RefreshProjectsAsync(selectProjectId: null, ct).ConfigureAwait(true);
    }

    /// <summary>数据目录里是否已有 <c>annotrain.db</c>。</summary>
    public bool HasDatabase => !string.IsNullOrWhiteSpace(DataDirectory)
                              && TinyLabelWorkspace.DatabaseExists(StorageLocation.FromPath(DataDirectory));

    /// <summary>在当前数据目录初始化空库（只建表，不建项目）；由「新建项目」在用户确认后调用。</summary>
    public void EnsureDatabase()
    {
        if (!string.IsNullOrWhiteSpace(DataDirectory))
        {
            TinyLabelWorkspace.EnsureDatabase(StorageLocation.FromPath(DataDirectory));
        }
    }

    /// <summary>重新读项目列表；<paramref name="selectProjectId"/> 为空时尽量保持当前选中。</summary>
    public async Task RefreshProjectsAsync(long? selectProjectId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(DataDirectory))
        {
            return;
        }

        long? keep = selectProjectId ?? SelectedProject?.Id;
        StorageLocation root = StorageLocation.FromPath(DataDirectory);

        try
        {
            IReadOnlyList<TinyLabelProjectSummary> summaries = await _catalog
                .ListAsync(root, ct)
                .ConfigureAwait(true);

            Projects.Clear();

            foreach (TinyLabelProjectSummary summary in summaries)
            {
                Projects.Add(new ProjectSummaryViewModel(summary));
            }

            ProjectSummaryViewModel? target = keep is { } id
                ? Projects.FirstOrDefault(project => project.Id == id)
                : null;

            SelectedProject = target ?? Projects.FirstOrDefault();

            int open = summaries.Count(summary => !summary.IsClosed);
            ProjectsSummary = Loc.Format("Label.ProjectsSummary", Projects.Count, open);

            Status = Projects.Count == 0
                ? (TinyLabelWorkspace.DatabaseExists(root)
                    ? Loc.Get("Label.NoProjectHint")
                    : Loc.Get("Label.NoDatabase"))
                : Loc.Format("Label.DataDir", DataDirectory);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "读取项目列表失败");
            Report(Loc.Format("Label.ProjectsLoadFailed", error.Message));
        }
    }

    partial void OnSelectedProjectChanged(ProjectSummaryViewModel? value)
        => _ = LoadProjectAsync(value);

    /// <summary>加载选中项目的图片（第一页）与类别。</summary>
    private async Task LoadProjectAsync(ProjectSummaryViewModel? project, long? keepImageId = null)
    {
        int version = ++_loadVersion;

        Images.Clear();
        SelectedImage = null;
        _classes = [];
        PageIndex = 0;

        if (project is null)
        {
            ProjectTitle = Loc.Get("Label.NoProject");
            ProjectDescription = string.Empty;
            ProjectStats = "—";
            ProjectProgress = 0;
            IsProjectClosed = false;
            CategoryText = Loc.Format("Label.ManageClasses", 0);
            PageText = Loc.Format("Label.PageOf", 1, 1);
            TotalText = Loc.Format("Label.TotalImages", 0);
            CanPrevPage = false;
            CanNextPage = false;
            return;
        }

        ProjectTitle = project.Name;
        ProjectDescription = project.Description;
        ProjectStats = project.Stats;
        ProjectProgress = project.Progress * 100;
        IsProjectClosed = project.IsClosed;

        try
        {
            IReadOnlyList<TinyLabelClassRow> classes = await _classService
                .ListAsync(StorageLocation.FromPath(DataDirectory), project.Id)
                .ConfigureAwait(true);

            if (version != _loadVersion)
            {
                return;
            }

            _classes = [.. classes.Select(row => row.Name)];
            CategoryText = Loc.Format("Label.ManageClasses", classes.Count);

            await LoadImagePageAsync(version, keepImageId).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "加载项目 {Project} 失败", project.Name);
            Report(Loc.Format("Label.OpenProjectFailed", error.Message));
        }
    }

    private async Task LoadImagePageAsync(int version, long? keepImageId = null)
    {
        if (SelectedProject is not { } project)
        {
            return;
        }

        TinyLabelImagePage page = await _images
            .QueryAsync(
                StorageLocation.FromPath(DataDirectory),
                project.Id,
                PageIndex,
                DefaultPageSize)
            .ConfigureAwait(true);

        if (version != _loadVersion)
        {
            return;
        }

        Images.Clear();

        foreach (TinyLabelImageRow row in page.Items)
        {
            Images.Add(new ProjectImageViewModel(row));
        }

        SelectedImage = (keepImageId is { } id
                            ? Images.FirstOrDefault(image => image.Id == id)
                            : null)
                        ?? Images.FirstOrDefault();

        PageText = Loc.Format("Label.PageOf", page.DisplayPage, page.PageCount);
        TotalText = Loc.Format("Label.TotalImages", page.Total);
        CanPrevPage = PageIndex > 0;
        CanNextPage = PageIndex + 1 < page.PageCount;

        Status = page.Total == 0
            ? Loc.Get("Label.NoImages")
            : Loc.Format("Label.ProjectOpened", project.Name, page.Total, page.DisplayPage, page.PageCount);
    }

    /// <summary>翻页：<paramref name="delta"/> 为 +1 / -1。</summary>
    public async Task TurnPageAsync(int delta)
    {
        if (SelectedProject is null)
        {
            return;
        }

        int next = PageIndex + delta;

        if (next < 0 || (!CanNextPage && delta > 0))
        {
            return;
        }

        PageIndex = next;

        int version = _loadVersion;
        await LoadImagePageAsync(version).ConfigureAwait(true);
    }

    /// <summary>标完一张回来：重载当前页，尽量还选中刚才那张。</summary>
    public Task RefreshCurrentProjectAsync(long? keepImageId = null)
        => LoadImagePageAsync(_loadVersion, keepImageId);

    // ── 项目写操作 ────────────────────────────────────────────────────────────

    /// <summary>在当前数据目录的库（不新建库）里新建一个空项目，建完刷新列表并选中它。</summary>
    public async Task CreateProjectAsync(ProjectDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            long projectId = await _projects
                .CreateAsync(StorageLocation.FromPath(DataDirectory), draft.Name, draft.Description, ct)
                .ConfigureAwait(true);

            Report(Loc.Format("Label.ProjectCreated", draft.Name));

            await RefreshProjectsAsync(projectId, ct).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "新建项目失败");
            Report(Loc.Format("Label.ProjectCreateFailed", error.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>编辑当前项目的名称与备注。</summary>
    public async Task UpdateProjectAsync(ProjectDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (SelectedProject is not { } project || IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            await _projects
                .UpdateAsync(StorageLocation.FromPath(DataDirectory), project.Id, draft.Name, draft.Description, ct)
                .ConfigureAwait(true);

            Report(Loc.Format("Label.ProjectSaved", draft.Name));

            await RefreshProjectsAsync(project.Id, ct).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "编辑项目失败");
            Report(Loc.Format("Label.ProjectSaveFailed", error.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>删除当前项目（图片 + 标注 + 类别 + 项目目录）。</summary>
    public async Task DeleteProjectAsync(bool deleteImages = true, CancellationToken ct = default)
    {
        if (SelectedProject is not { } project || IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            await _projects
                .DeleteAsync(StorageLocation.FromPath(DataDirectory), project.Id, deleteImages, ct)
                .ConfigureAwait(true);

            Report(Loc.Format("Label.ProjectDeleted", project.Name));

            await RefreshProjectsAsync(selectProjectId: null, ct).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "删除项目失败");
            Report(Loc.Format("Label.ProjectDeleteFailed", error.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>结束 / 重新打开当前项目（只动归档时间，不动图片）。</summary>
    public async Task ToggleProjectClosedAsync(CancellationToken ct = default)
    {
        if (SelectedProject is not { } project || IsBusy)
        {
            return;
        }

        bool closed = !project.IsClosed;
        IsBusy = true;

        try
        {
            await _projects
                .SetClosedAsync(StorageLocation.FromPath(DataDirectory), project.Id, closed, ct)
                .ConfigureAwait(true);

            Report(Loc.Format(closed ? "Label.ProjectClosed" : "Label.ProjectReopened", project.Name));

            await RefreshProjectsAsync(project.Id, ct).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "归档项目失败");
            Report(Loc.Format("Label.ProjectToggleFailed", error.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── 图片入库 / 导出 ───────────────────────────────────────────────────────

    /// <summary>导入图片文件（复制进项目目录）。</summary>
    public async Task ImportImagesAsync(IReadOnlyList<string> files, CancellationToken ct = default)
    {
        if (SelectedProject is not { } project || files.Count == 0)
        {
            return;
        }

        await RunBusyAsync(
            Loc.Get("Label.ImportingImages"),
            async () =>
            {
                TinyLabelImageImportReport report = await _images
                    .ImportFilesAsync(StorageLocation.FromPath(DataDirectory), project.Id, files, split: null, copy: true, ct)
                    .ConfigureAwait(true);

                await AfterImagesChangedAsync(report).ConfigureAwait(true);
            },
            Loc.Get("Label.ImportImagesAction")).ConfigureAwait(true);
    }

    /// <summary>导入文件夹（<paramref name="copy"/>=true 复制进项目；false 即「扫描目录」，只登记原图）。</summary>
    public async Task ImportFolderAsync(string folder, bool copy, CancellationToken ct = default)
    {
        if (SelectedProject is not { } project || string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        await RunBusyAsync(
            Loc.Get(copy ? "Label.ImportingFolder" : "Label.ScanningFolder"),
            async () =>
            {
                TinyLabelImageImportReport report = await _images
                    .ImportFolderAsync(StorageLocation.FromPath(DataDirectory), project.Id, folder, split: null, copy, recursive: true, ct)
                    .ConfigureAwait(true);

                await AfterImagesChangedAsync(report).ConfigureAwait(true);
            },
            Loc.Get(copy ? "Label.ImportFolderAction" : "Label.ScanFolderAction")).ConfigureAwait(true);
    }

    private async Task AfterImagesChangedAsync(TinyLabelImageImportReport report)
    {
        Report(Loc.Format("Label.ImportResult", report.Imported, report.Skipped));

        if (report.Warnings.Count > 0)
        {
            AppendLog(string.Join(Environment.NewLine, report.Warnings.Take(10)));
        }

        await RefreshProjectsAsync(SelectedProject?.Id).ConfigureAwait(true);
    }

    /// <summary>把当前项目导出成 YOLO / COCO 数据集。</summary>
    public async Task ExportAsync(DatasetFormat format, string destination, CancellationToken ct = default)
    {
        if (SelectedProject is not { } project)
        {
            return;
        }

        await RunBusyAsync(
            Loc.Get("Label.Exporting"),
            async () =>
            {
                DatasetOpenResult opened = await _resolver
                    .OpenAsync(
                        StorageLocation.FromPath(DataDirectory),
                        DatasetFormat.TinyLabelDb,
                        project.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ct)
                    .ConfigureAwait(true);

                DatasetExportReport report = await _exporter
                    .ExportAsync(
                        opened.Store,
                        new DatasetExportOptions(format, StorageLocation.FromPath(destination), CopyImages: true),
                        ct)
                    .ConfigureAwait(true);

                string text = Loc.Format("Label.ExportResult", report.ImagesExported, report.BoxesExported, destination);
                Report(text);

                foreach (string warning in report.Warnings.Take(10))
                {
                    AppendLog(warning);
                }
            },
            Loc.Get("Label.ExportAction")).ConfigureAwait(true);
    }

    /// <summary>导入 YOLO / RF-DETR 数据集作为新项目（这里会建库）。</summary>
    public async Task ImportAsync(ImportRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = Loc.Get("Label.Importing");

        try
        {
            LabelDatasetPolicy.EnsureImportable(request.Format);

            DatasetImportReport report = await _importer
                .ImportAsync(
                    new DatasetImportOptions(
                        StorageLocation.FromPath(request.SourcePath),
                        StorageLocation.FromPath(DataDirectory),
                        string.IsNullOrWhiteSpace(request.ProjectName) ? null : request.ProjectName.Trim(),
                        request.Format),
                    ct)
                .ConfigureAwait(true);

            Report(Loc.Format("Label.ImportDone", report.ImagesImported, report.Classes.Count));
            AppendLog(report);

            await RefreshProjectsAsync(report.ProjectId, ct).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "导入数据集失败");
            Report(Loc.Format("Label.ImportFailed", error.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── 内部 ──────────────────────────────────────────────────────────────────

    private async Task RunBusyAsync(string status, Func<Task> action, string operation)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = status;

        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "{Operation} failed", operation);
            Report($"{operation}: {error.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>界面层报错用：同时写状态栏与日志面板。</summary>
    public void Report(string message)
    {
        Status = message;
        AppendLog(message);
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(error, "保存设置失败");
        }
    }

    private void AppendLog(DatasetImportReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Loc.Format("Label.ImportDone", report.ImagesImported, report.Classes.Count));
        builder.AppendLine($"db: {report.DatabasePath} (project {report.ProjectId})");

        foreach ((string split, int count) in report.ImagesPerSplit)
        {
            builder.AppendLine($"  {split}: {count}");
        }

        builder.AppendLine($"classes: {string.Join(", ", report.Classes)}");

        foreach (string warning in report.Warnings.Take(20))
        {
            builder.AppendLine($"  ! {warning}");
        }

        AppendLog(builder.ToString());
    }

    private void AppendLog(string text)
        => LogText = string.IsNullOrEmpty(LogText) ? text : $"{LogText}{Environment.NewLine}{text}";
}
