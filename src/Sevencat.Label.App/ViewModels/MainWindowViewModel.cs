using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Injectio.Attributes;
using Microsoft.Extensions.Logging;
using Sevencat.Core;
using Sevencat.Core.Dataset;
using Sevencat.Core.Dataset.Export;
using Sevencat.Core.Dataset.Import;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Imaging;
using Sevencat.Core.Infrastructure;
using Sevencat.Core.Infrastructure.Logging;
using Sevencat.Core.Localization;
using Sevencat.Core.Storage;
using Sevencat.Label;
using Sevencat.Label.Server;

namespace Sevencat.Label.App.ViewModels;

/// <summary>
/// 标注主窗口的 ViewModel。布局参照 TinyLabel 服务端首页：
/// 左边「项目管理」（新建 / 编辑 / 删除 / 结束 + 项目列表），右边「项目概览 + 图片清单」。
/// <para>
/// 图片清单走 <see cref="TinyLabelImageService"/> 分页查询（状态 / split / 关键词过滤都在库里做），
/// 不再整库装载 labelme JSON；标注窗口按行里的图片引用单独打开。
/// </para>
/// </summary>
[RegisterSingleton<MainWindowViewModel>]
public partial class MainWindowViewModel : ObservableObject
{
    /// <summary>图片清单每页条数。</summary>
    public const int DefaultPageSize = 20;

    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly ServerOptions _serverOptions;
    private readonly LabelDatasetImportService _importer;
    private readonly TinyLabelProjectCatalog _catalog;
    private readonly TinyLabelProjectService _projects;
    private readonly TinyLabelImageService _images;
    private readonly TinyLabelClassService _classService;
    private readonly DatasetExportService _exporter;
    private readonly DatasetStoreResolver _resolver;
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly IDatabaseLocation _location;

    /// <summary>切项目时的代次：异步加载回来发现已经切走就丢弃结果。</summary>
    private int _loadVersion;

    /// <summary>正在重建项目列表：这期间 ListBox 回写选中项不算用户操作，不触发右侧加载。</summary>
    private bool _refreshingProjects;

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

    // ── 顶栏：标注 Web 端地址 ────────────────────────────────────────────────

    /// <summary>标注 Web 端的监听地址：与真正监听的是同一个值（由端口拼出，见 <see cref="ServerOptions.Url"/>）。</summary>
    public string WebUrl => _serverOptions.Url;

    /// <summary>监听端口（唯一来源，地址由它拼）。</summary>
    public int WebPort => _serverOptions.Port;

    /// <summary>「端口 5180」（切语言后重算）。</summary>
    [ObservableProperty]
    private string _webPortText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>当前界面语言；切换后写入设置并刷新文案。</summary>
    [ObservableProperty]
    private LanguageOption? _selectedLanguage;

    // ── 左侧：项目管理 ────────────────────────────────────────────────────────

    public ObservableCollection<ProjectSummaryViewModel> Projects { get; } = [];

    /// <summary>界面绑定的列表：项目多时按搜索词筛过一遍（默认与 <see cref="Projects"/> 一致）。</summary>
    public ObservableCollection<ProjectSummaryViewModel> VisibleProjects { get; } = [];

    /// <summary>搜索词，空格分词、全部命中才显示。</summary>
    [ObservableProperty]
    private string _projectFilter = string.Empty;

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

    /// <summary>是否选中了项目：概览卡与清单表头按它显隐（照 TinyLabel 的 <c>HasProject</c>）。</summary>
    [ObservableProperty]
    private bool _hasProject;

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

    /// <summary>当前页码（从 1 起）：分页条按「第 N 页 / 共 M 页」分三段显示。</summary>
    [ObservableProperty]
    private int _page = 1;

    [ObservableProperty]
    private int _pageCount = 1;

    [ObservableProperty]
    private int _total;

    /// <summary>当前库文件路径：启动时由容器开好（<c>--db</c> / 环境变量 / 数据目录），运行期不变。</summary>
    public string DatabasePath => _location.DatabasePath;

    /// <summary>底栏左侧：数据存放位置（照 TinyLabel 的 FooterText）。</summary>
    public string FooterText => Loc.Format("Label.FooterDatabase", DatabasePath);

    /// <summary>底栏右侧：软件信息。</summary>
    public string ProductInfoText => Loc.Get("Label.ProductInfo");

    public IReadOnlyList<string> Classes => _classes;

    /// <summary>语言下拉的候选。</summary>
    public IReadOnlyList<LanguageOption> Languages => Localizer.Supported;

    private static Localizer Loc => Localizer.Instance;

    public MainWindowViewModel(
        IDatabaseLocation location,
        ServerOptions serverOptions,
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
        _location = location;
        _serverOptions = serverOptions;
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
        _webPortText = Loc.Format("Label.WebPort", serverOptions.Port);
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
        WebPortText = Loc.Format("Label.WebPort", WebPort);
        PageText = Loc.Format("Label.PageOf", PageIndex + 1, 1);
        TotalText = Loc.Format("Label.TotalImages", 0);
        ApplyProjectFilter();

        if (SelectedProject is null)
        {
            ProjectTitle = Loc.Get("Label.NoProject");
            ProjectStats = "—";
        }
    }

    // ── 数据目录与项目列表 ────────────────────────────────────────────────────

    /// <summary>启动时打开上次的数据目录；没有记录、或那个目录已经不在了，就用 <c>exe/data</c>。</summary>
    public async Task OpenLastWorkspaceAsync(CancellationToken ct = default)
    {
        string folder = _settings.LastDataDirectory;

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            folder = AppDataPaths.DataDirectory;
        }

        await OpenWorkspaceAsync(folder, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 打开数据目录：读库里的项目列表。库在进程启动时已经建好（空目录也会有一个只有表的库），
    /// 这里只切界面上的数据目录，运行期不换库。
    /// </summary>
    public async Task OpenWorkspaceAsync(string folder, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        // 数据目录跟着当前库走（库路径在 Program/App 启动时就定死了）
        folder = _location.DatabaseDirectory;

        DataDirectory = folder;
        _settings.LastDataDirectory = folder;
        SaveSettings();

        Status = Loc.Get("Label.LoadingProjects");

        await RefreshProjectsAsync(selectProjectId: null, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 重新读项目列表；<paramref name="selectProjectId"/> 为空时尽量保持当前选中。
    /// 传 <paramref name="keepImageId"/> / <paramref name="keepPage"/> 可以刷新后仍落回同一张图、同一页
    /// （导入图片、改类别回来时用，不然选中会被清掉）。
    /// </summary>
    public async Task RefreshProjectsAsync(
        long? selectProjectId = null,
        CancellationToken ct = default,
        long? keepImageId = null,
        int keepPage = 0)
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

            // 重建列表期间 ListBox 会把 SelectedItem 清成 null，别让它反过来触发图片清空
            _refreshingProjects = true;

            try
            {
                Projects.Clear();

                foreach (TinyLabelProjectSummary summary in summaries)
                {
                    Projects.Add(new ProjectSummaryViewModel(summary));
                }

                ProjectSummaryViewModel? target = keep is { } id
                    ? Projects.FirstOrDefault(project => project.Id == id)
                    : null;

                // 有搜索词时优先落到看得见的那条上，别选中一条被筛掉的
                SelectedProject = target ?? VisibleProjects.FirstOrDefault() ?? Projects.FirstOrDefault();

                ApplyProjectFilter();
            }
            finally
            {
                _refreshingProjects = false;
            }

            Status = Projects.Count == 0
                ? Loc.Get("Label.NoProjectHint")
                : Loc.Format("Label.DataDir", DataDirectory);

            // 列表重建后统一加载一次右侧（保留原来的图片与页码）
            await LoadProjectAsync(SelectedProject, keepImageId, keepPage).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "读取项目列表失败");
            Report(Loc.Format("Label.ProjectsLoadFailed", error.Message));
        }
    }

    /// <summary>
    /// 重建 <see cref="VisibleProjects"/>：项目多的时候列表靠搜索收窄，
    /// 空格分词、每个词都要在名称/备注/Id 里命中（不改动选中项）。
    /// </summary>
    private void ApplyProjectFilter()
    {
        string[] tokens = (ProjectFilter ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        IEnumerable<ProjectSummaryViewModel> matches = tokens.Length == 0
            ? Projects
            : Projects.Where(project => tokens.All(project.Matches));

        VisibleProjects.Clear();

        foreach (ProjectSummaryViewModel project in matches)
        {
            VisibleProjects.Add(project);
        }

        ProjectsSummary = tokens.Length == 0
            ? Loc.Format("Label.ProjectsSummary", Projects.Count, Projects.Count(project => !project.IsClosed))
            : Loc.Format("Label.ProjectsFiltered", VisibleProjects.Count, Projects.Count);
    }

    partial void OnProjectFilterChanged(string value)
    {
        ApplyProjectFilter();

        // 选中的项目被筛掉了：跳到第一条可见的，右侧面板别停在看不见的项目上
        if (SelectedProject is { } selected && !VisibleProjects.Contains(selected))
        {
            SelectedProject = VisibleProjects.FirstOrDefault();
        }
    }

    partial void OnSelectedProjectChanged(ProjectSummaryViewModel? value)
    {
        if (_refreshingProjects)
        {
            return;
        }

        _ = LoadProjectAsync(value);
    }

    /// <summary>加载选中项目的图片（<paramref name="pageIndex"/> 页）与类别。</summary>
    private async Task LoadProjectAsync(
        ProjectSummaryViewModel? project,
        long? keepImageId = null,
        int pageIndex = 0)
    {
        int version = ++_loadVersion;

        ClearImages();
        SelectedImage = null;
        _classes = [];
        PageIndex = Math.Max(0, pageIndex);

        if (project is null)
        {
            PageIndex = 0;
            HasProject = false;
            Page = 1;
            PageCount = 1;
            Total = 0;
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
        HasProject = true;

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

        StorageLocation root = StorageLocation.FromPath(DataDirectory);

        TinyLabelImagePage page = await _images
            .QueryAsync(root, project.Id, PageIndex, DefaultPageSize)
            .ConfigureAwait(true);

        // 页码越界（导入变多 / 删图变少都会遇到）：落到最后一页，别给一张空清单
        if (page.Total > 0 && page.Items.Count == 0 && PageIndex > 0)
        {
            PageIndex = Math.Max(0, page.PageCount - 1);
            page = await _images
                .QueryAsync(root, project.Id, PageIndex, DefaultPageSize)
                .ConfigureAwait(true);
        }

        if (version != _loadVersion)
        {
            return;
        }

        ClearImages();

        var added = new List<ProjectImageViewModel>(page.Items.Count);

        foreach (TinyLabelImageRow row in page.Items)
        {
            var item = new ProjectImageViewModel(row);

            // 缩略图直接读 thumbs/ 里的文件；缺的下面在后台补
            item.SetThumbnail(ThumbnailCache.PathOf(root, project.Id, row.RelPath));
            Images.Add(item);
            added.Add(item);
        }

        SelectedImage = (keepImageId is { } id
                            ? Images.FirstOrDefault(image => image.Id == id)
                            : null)
                        ?? Images.FirstOrDefault();

        Page = page.DisplayPage;
        PageCount = page.PageCount;
        Total = page.Total;
        PageText = Loc.Format("Label.PageOf", page.DisplayPage, page.PageCount);
        TotalText = Loc.Format("Label.TotalImages", page.Total);
        CanPrevPage = PageIndex > 0;
        CanNextPage = PageIndex + 1 < page.PageCount;

        Status = page.Total == 0
            ? Loc.Get("Label.NoImages")
            : Loc.Format("Label.ProjectOpened", project.Name, page.Total, page.DisplayPage, page.PageCount);

        _ = EnsureThumbnailsAsync(root, project.Id, added);
    }

    /// <summary>补齐这一页缺的缩略图（老项目 / 刚导入的图）；生成失败就留空，不影响清单。</summary>
    private async Task EnsureThumbnailsAsync(
        StorageLocation root,
        long projectId,
        IReadOnlyList<ProjectImageViewModel> items)
    {
        foreach (ProjectImageViewModel item in items)
        {
            if (item.Thumbnail is not null)
            {
                continue;
            }

            string? generated = await ThumbnailCache
                .EnsureAsync(root, projectId, item.Row.RelPath, item.Row.FilePath)
                .ConfigureAwait(true);

            if (generated is not null && Images.Contains(item))
            {
                item.SetThumbnail(generated);
            }
        }
    }

    private void ClearImages()
    {
        foreach (ProjectImageViewModel item in Images)
        {
            item.Dispose();
        }

        Images.Clear();
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

    // ── 清单行菜单 ────────────────────────────────────────────────────────────

    /// <summary>清单行菜单：重置为未标注（只改状态，已存的标注数据不删）。</summary>
    public Task ResetImageStateAsync(ProjectImageViewModel row) => RunBusyAsync(
        Loc.Get("Label.ResettingImage"),
        async () =>
        {
            await _images
                .SetStateAsync(StorageLocation.FromPath(DataDirectory), row.Id, "pending")
                .ConfigureAwait(true);

            Report(Loc.Format("Label.ImageReset", row.DisplayPath));

            await RefreshCurrentProjectAsync().ConfigureAwait(true);
        },
        "reset image state");

    /// <summary>清单行菜单：清除标注（删标注行，状态退回待标注，rev +1）。</summary>
    public Task ClearAnnotationForAsync(ProjectImageViewModel row) => RunBusyAsync(
        Loc.Get("Label.ClearingAnnotation"),
        async () =>
        {
            await _images
                .ClearAnnotationAsync(StorageLocation.FromPath(DataDirectory), row.Id)
                .ConfigureAwait(true);

            Report(Loc.Format("Label.ImageCleared", row.DisplayPath));

            await RefreshCurrentProjectAsync().ConfigureAwait(true);
        },
        "clear annotation");

    /// <summary>清单行菜单：删除图片（库里的行 + 导入模式下项目目录里的原图与缩略图）。</summary>
    public Task DeleteImageForAsync(ProjectImageViewModel row) => RunBusyAsync(
        Loc.Get("Label.DeletingImage"),
        async () =>
        {
            await _images
                .DeleteImageAsync(StorageLocation.FromPath(DataDirectory), row.Id)
                .ConfigureAwait(true);

            Report(Loc.Format("Label.ImageDeleted", row.DisplayPath));

            await RefreshCurrentProjectAsync().ConfigureAwait(true);
        },
        "delete image");

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

        // 导入完刷新：右侧仍停在原来那张图、那一页，不把用户的选中冲掉
        await RefreshProjectsAsync(
            SelectedProject?.Id,
            keepImageId: SelectedImage?.Id,
            keepPage: PageIndex).ConfigureAwait(true);
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

    /// <summary>日志窗口的「清空」。</summary>
    public void ClearLog() => LogText = string.Empty;
}
