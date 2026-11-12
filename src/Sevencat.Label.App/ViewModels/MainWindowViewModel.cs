using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Sevencat.Core;
using Sevencat.Core.Dataset.Import;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Infrastructure;
using Sevencat.Core.Infrastructure.Logging;
using Sevencat.Core.Storage;
using Sevencat.Label;

namespace Sevencat.Label.App.ViewModels;

/// <summary>
/// 主窗口：左边项目管理（数据目录 + 项目列表），右边项目明细（图片表格）。
/// 标注在独立对话框里做（参考 TinyLabel：列表 → 点进一张 → 标完关闭）。
/// 标注端只认数据库形态；YOLO / RF-DETR 通过「导入」进来、通过「导出」出去。
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly LabelDatasetImportService _importer;
    private readonly LabelProjectBrowser _browser;
    private readonly TinyLabelProjectCatalog _catalog;
    private readonly TinyLabelProjectService _projects;
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;

    /// <summary>切项目时的代次：异步加载回来发现已经切走就丢弃结果。</summary>
    private int _loadVersion;

    /// <summary>当前项目的类别（标注对话框的标签候选项）。</summary>
    private IReadOnlyList<string> _classes = [];

    [ObservableProperty]
    private string _title = $"{AnnotrainInfo.ProductName} · 标注";

    [ObservableProperty]
    private string _status = "就绪";

    [ObservableProperty]
    private string _logText = string.Empty;

    /// <summary>当前数据目录（<c>annotrain.db</c> 所在目录）。为空表示还没打开。</summary>
    [ObservableProperty]
    private string _dataDirectory = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    // ── 左侧：项目管理 ────────────────────────────────────────────────────────

    public ObservableCollection<ProjectSummaryViewModel> Projects { get; } = [];

    [ObservableProperty]
    private ProjectSummaryViewModel? _selectedProject;

    // ── 右侧：项目明细 ────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _projectTitle = "未选择项目";

    public ObservableCollection<ProjectImageViewModel> Images { get; } = [];

    [ObservableProperty]
    private ProjectImageViewModel? _selectedImage;

    /// <summary>当前数据目录下的库文件路径；没打开目录时为空。</summary>
    public string DatabasePath => string.IsNullOrWhiteSpace(DataDirectory)
        ? string.Empty
        : Path.Combine(DataDirectory, TinyLabelProjectCatalog.DefaultDatabaseFile);

    public IReadOnlyList<string> Classes => _classes;

    public MainWindowViewModel(
        ILogger<MainWindowViewModel> logger,
        LogBuffer logBuffer,
        LabelDatasetImportService importer,
        LabelProjectBrowser browser,
        TinyLabelProjectCatalog catalog,
        TinyLabelProjectService projects,
        AppSettings settings,
        SettingsStore settingsStore)
    {
        _logger = logger;
        _importer = importer;
        _browser = browser;
        _catalog = catalog;
        _projects = projects;
        _settings = settings;
        _settingsStore = settingsStore;

        logger.LogInformation("主窗口已创建，等待打开数据目录");

        LogText = string.Join(Environment.NewLine, logBuffer.Snapshot().Select(entry => entry.ToString()));
    }

    // ── 数据目录与项目列表 ────────────────────────────────────────────────────

    /// <summary>启动时自动打开上次的数据目录（不存在就跳过）。</summary>
    public Task OpenLastWorkspaceAsync(CancellationToken ct = default)
        => !string.IsNullOrWhiteSpace(_settings.LastDataDirectory) && Directory.Exists(_settings.LastDataDirectory)
            ? OpenWorkspaceAsync(_settings.LastDataDirectory, ct)
            : Task.CompletedTask;

    /// <summary>打开数据目录：列出库里所有项目，默认选中第一个。</summary>
    public async Task OpenWorkspaceAsync(string folder, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        DataDirectory = folder;
        _settings.LastDataDirectory = folder;
        SaveSettings();

        Status = "正在读取项目列表…";

        await RefreshProjectsAsync(selectProjectId: null, ct).ConfigureAwait(true);
    }

    /// <summary>在当前数据目录里新建一个空项目，建完刷新列表并选中它。</summary>
    public async Task CreateProjectAsync(ProjectDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (string.IsNullOrWhiteSpace(DataDirectory))
        {
            Status = "请先打开数据目录，新项目会建在那个数据库里";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            long projectId = await _projects
                .CreateAsync(
                    StorageLocation.Parse(DataDirectory),
                    draft.Name,
                    draft.Description,
                    ct)
                .ConfigureAwait(true);

            Status = $"已新建项目「{draft.Name}」";
            AppendLog(Status);

            await RefreshProjectsAsync(projectId, ct).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "新建项目失败");
            Status = $"新建项目失败：{error.Message}";
            AppendLog(Status);
        }
        finally
        {
            IsBusy = false;
        }
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

    /// <summary>重新读项目列表；<paramref name="selectProjectId"/> 为空时尽量保持当前选中。</summary>
    public async Task RefreshProjectsAsync(long? selectProjectId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(DataDirectory))
        {
            return;
        }

        long? keep = selectProjectId ?? SelectedProject?.Id;

        try
        {
            IReadOnlyList<TinyLabelProjectSummary> summaries = await _catalog
                .ListAsync(StorageLocation.Parse(DataDirectory), ct)
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

            Status = Projects.Count == 0
                ? "这个数据目录里还没有项目，可以用「导入数据集」建一个"
                : $"共 {Projects.Count} 个项目";
        }
        catch (Exception error)
        {
            _logger.LogError(error, "读取项目列表失败");
            Status = $"读取项目列表失败：{error.Message}";
            AppendLog(Status);
        }
    }

    partial void OnSelectedProjectChanged(ProjectSummaryViewModel? value)
        => _ = LoadProjectAsync(value);

    /// <summary>标完一张回来：重载当前项目，尽量还选中刚才那张。</summary>
    public Task RefreshCurrentProjectAsync(string? keepRelativePath = null)
        => LoadProjectAsync(SelectedProject, keepRelativePath);

    /// <summary>加载选中项目的图片明细，并选中第一张（或 <paramref name="keepRelativePath"/>）。</summary>
    private async Task LoadProjectAsync(ProjectSummaryViewModel? project, string? keepRelativePath = null)
    {
        int version = ++_loadVersion;

        Images.Clear();
        SelectedImage = null;
        _classes = [];

        if (project is null)
        {
            ProjectTitle = "未选择项目";
            return;
        }

        try
        {
            OpenedProject opened = await _browser
                .OpenAsync(DataDirectory, project.Id)
                .ConfigureAwait(true);

            // 加载期间用户已经切到别的项目，丢弃这次结果
            if (version != _loadVersion)
            {
                return;
            }

            ProjectTitle = $"项目：{opened.ProjectName}（{project.Stats}）";
            _classes = [.. opened.Classes];

            foreach (ProjectImage image in opened.Images)
            {
                Images.Add(new ProjectImageViewModel(image));
            }

            SelectedImage = (keepRelativePath is null
                                ? null
                                : Images.FirstOrDefault(image => image.RelativePath == keepRelativePath))
                            ?? Images.FirstOrDefault();

            Status = Images.Count == 0
                ? "这个项目还没有图片，可以导入一个数据集"
                : $"项目「{opened.ProjectName}」：{Images.Count} 张图 / {opened.BoxCount} 个框";
        }
        catch (Exception error)
        {
            _logger.LogError(error, "打开项目 {Project} 失败", project.Name);
            Status = $"打开项目失败：{error.Message}";
            AppendLog(Status);
        }
    }

    // ── 导入 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 把 YOLO / RF-DETR 数据集导入当前数据目录，作为新项目；导入完刷新列表并选中它。
    /// 目标固定为 <see cref="DataDirectory"/>，不另选目录。
    /// </summary>
    public async Task ImportAsync(ImportRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(DataDirectory))
        {
            Status = "请先打开数据目录，导入的项目会放进那个数据库";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = "正在导入…";

        try
        {
            LabelDatasetPolicy.EnsureImportable(request.Format);

            DatasetImportReport report = await _importer
                .ImportAsync(
                    new DatasetImportOptions(
                        StorageLocation.Parse(request.SourcePath),
                        StorageLocation.Parse(DataDirectory),
                        string.IsNullOrWhiteSpace(request.ProjectName) ? null : request.ProjectName.Trim(),
                        request.Format),
                    ct)
                .ConfigureAwait(true);

            Status = report.Summary;
            AppendLog(report);

            await RefreshProjectsAsync(report.ProjectId, ct).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "导入数据集失败");
            Status = $"导入失败：{error.Message}";
            AppendLog($"导入失败：{error.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── 内部 ──────────────────────────────────────────────────────────────────

    private void AppendLog(DatasetImportReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine(report.Summary);
        builder.AppendLine($"库：{report.DatabasePath}（项目 {report.ProjectId}）");

        foreach ((string split, int count) in report.ImagesPerSplit)
        {
            builder.AppendLine($"  {split}: {count} 张");
        }

        builder.AppendLine($"类别：{string.Join("、", report.Classes)}");
        builder.AppendLine($"有标注：{report.AnnotatedImages} 张；跳过：{report.ImagesSkipped} 张；丢弃框：{report.BoxesDropped} 个");

        foreach (string warning in report.Warnings.Take(20))
        {
            builder.AppendLine($"  ! {warning}");
        }

        AppendLog(builder.ToString());
    }

    private void AppendLog(string text)
        => LogText = string.IsNullOrEmpty(LogText) ? text : $"{LogText}{Environment.NewLine}{text}";
}
