using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Sevencat.Core;
using Sevencat.Core.Dataset;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Infrastructure;
using Sevencat.Core.Infrastructure.Logging;
using Sevencat.Core.Localization;
using Sevencat.Core.Simulation;
using Sevencat.Core.Storage;
using Sevencat.Train.Engine.Training;

namespace Sevencat.Train.App.ViewModels;

/// <summary>
/// 训练主界面（单页）：上面工具栏、中间曲线、下面状态与日志。
/// 检测验证不占页面，收在工具栏的图标按钮里：选一张图 → 推理 → 弹对话框看叠加结果。
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly DatasetStoreResolver _resolver;
    private readonly IDatasetTrainingRunner _runner;
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly TinyLabelProjectCatalog _catalog;
    private readonly TinyLabelDatabase _database;

    private IDatasetStore? _store;
    private CancellationTokenSource? _training;
    private TrainControl? _control;

    [ObservableProperty]
    private string _title = Localizer.Instance.Get("Train.Title");

    [ObservableProperty]
    private string _status = Localizer.Instance.Get("Train.Ready");

    [ObservableProperty]
    private string _logText = string.Empty;

    /// <summary>当前界面语言；切换后写入设置。</summary>
    [ObservableProperty]
    private LanguageOption? _selectedLanguage;

    [ObservableProperty]
    private string _datasetPath = string.Empty;

    [ObservableProperty]
    private string _datasetSummary = Localizer.Instance.Get("Train.NoDataset");

    [ObservableProperty]
    private string _datasetStats = string.Empty;

    [ObservableProperty]
    private decimal _epochs = 10;

    [ObservableProperty]
    private decimal _batchSize = 4;

    [ObservableProperty]
    private decimal _imageSize = 640;

    /// <summary>epochs / batch / imgsz 在界面上只读展示，改值走「修改」对话框。</summary>
    public string EpochsText => ((int)Epochs).ToString(CultureInfo.InvariantCulture);

    public string BatchSizeText => ((int)BatchSize).ToString(CultureInfo.InvariantCulture);

    public string ImageSizeText => ((int)ImageSize).ToString(CultureInfo.InvariantCulture);

    partial void OnEpochsChanged(decimal value) => OnPropertyChanged(nameof(EpochsText));

    partial void OnBatchSizeChanged(decimal value) => OnPropertyChanged(nameof(BatchSizeText));

    partial void OnImageSizeChanged(decimal value) => OnPropertyChanged(nameof(ImageSizeText));

    [ObservableProperty]
    private string _outputDirectory = Path.Combine(AppDataPaths.DataDirectory, "runs");

    [ObservableProperty]
    private bool _isTraining;

    /// <summary>是否处于暂停状态（训练线程停在批次边界）。</summary>
    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _runInfo = string.Empty;

    /// <summary>训练曲线数据点（epoch / loss / mAP）。干跑阶段为空，控件显示占位提示。</summary>
    public ObservableCollection<ChartPoint> Points { get; } = [];

    /// <summary>
    /// 库（<c>annotrain.db</c>）里的标注项目：训练的输入就是标注端写进去的那些项目，
    /// 不用再让用户去挑目录。
    /// </summary>
    public ObservableCollection<ProjectOption> Projects { get; } = [];

    /// <summary>当前选中的标注项目。</summary>
    [ObservableProperty]
    private ProjectOption? _selectedProject;

    /// <summary>数据集类别（检测验证时用来给框命名）。</summary>
    public IReadOnlyList<string> Classes { get; private set; } = [];

    /// <summary>当前打开的数据集是否可用于训练。</summary>
    public bool HasDataset => _store is not null;

    // ── 顶栏：训练 Web 服务入口（M7 接入监听后由服务端填充真实端口）─────────────────

    /// <summary>训练服务的监听端口。</summary>
    public int ServicePort { get; } = ReadServicePort();

    /// <summary>「端口 5181」。</summary>
    public string ServicePortText => Loc.Format("Train.ServicePort", ServicePort);

    /// <summary>训练服务的地址（Web 端做好后指向它）。</summary>
    public string ServiceUrl => $"http://127.0.0.1:{ServicePort}";

    // ── 运行状态（按钮可用性与顶栏徽标都从这里取）──────────────────────────────

    public bool CanStart => !IsTraining;

    public bool CanPause => IsTraining && !IsPaused;

    public bool CanResume => IsTraining && IsPaused;

    public bool CanFinish => IsTraining;

    public bool ShowRunningBadge => IsTraining && !IsPaused;

    public bool ShowPausedBadge => IsPaused;

    public bool ShowReadyBadge => !IsTraining;

    partial void OnIsTrainingChanged(bool value) => RaiseRunState();

    partial void OnIsPausedChanged(bool value) => RaiseRunState();

    private void RaiseRunState()
    {
        OnPropertyChanged(nameof(ChartHint));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanFinish));
        OnPropertyChanged(nameof(ShowRunningBadge));
        OnPropertyChanged(nameof(ShowPausedBadge));
        OnPropertyChanged(nameof(ShowReadyBadge));
    }

    private static int ReadServicePort()
    {
        string? raw = Environment.GetEnvironmentVariable("ANNOTRAIN_TRAIN_PORT");

        return int.TryParse(raw, out int port) && port is > 0 and < 65536 ? port : 5181;
    }

    /// <summary>语言下拉的候选。</summary>
    public IReadOnlyList<LanguageOption> Languages => Localizer.Supported;

    private static Localizer Loc => Localizer.Instance;

    public MainWindowViewModel(
        ILogger<MainWindowViewModel> logger,
        LogBuffer logBuffer,
        DatasetStoreResolver resolver,
        IDatasetTrainingRunner runner,
        AppSettings settings,
        SettingsStore settingsStore,
        TinyLabelProjectCatalog catalog,
        TinyLabelDatabase database)
    {
        _logger = logger;
        _resolver = resolver;
        _runner = runner;
        _settings = settings;
        _settingsStore = settingsStore;
        _catalog = catalog;
        _database = database;

        _selectedLanguage = Localizer.Supported.FirstOrDefault(option => option.Code == Loc.Language);

        if (!string.IsNullOrWhiteSpace(settings.LastTrainDataDirectory))
        {
            OutputDirectory = Path.Combine(settings.LastTrainDataDirectory, "runs");
        }

        logger.LogInformation("训练主窗口已创建，等待选择数据集");

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

        Title = Loc.Get("Train.Title");
        Status = Loc.Get("Train.Ready");

        if (_store is null)
        {
            DatasetSummary = Loc.Get("Train.NoDataset");
        }

        RunInfo = string.Empty;
        OnPropertyChanged(nameof(ChartHint));
        OnPropertyChanged(nameof(ServicePortText));
    }

    // ── 数据集 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 打开外部数据集目录。<paramref name="format"/> 为 <c>null</c> 时交给探测器自己判断，
    /// 指定了（YOLO / COCO / TinyLabel）就按该格式打开。
    /// </summary>
    public async Task OpenDatasetAsync(string path, DatasetFormat? format = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Status = Localizer.Instance.Get("Train.Opening");

        try
        {
            DatasetOpenResult opened = await _resolver
                .OpenAsync(StorageLocation.Parse(path), format, datasetId: null, ct)
                .ConfigureAwait(true);

            await ApplyOpenedAsync(opened, path, ct).ConfigureAwait(true);

            _settings.LastTrainDataDirectory = Path.GetDirectoryName(path) ?? string.Empty;
            SaveSettings();
        }
        catch (Exception error)
        {
            _logger.LogError(error, "打开数据集失败");
            Status = Localizer.Instance.Format("Train.OpenFailed", error.Message);
            AppendLog(Status);
        }
    }

    /// <summary>
    /// 列出库（<c>annotrain.db</c>）里的标注项目：训练的输入就是标注端写进去的这些项目，
    /// 不用再让用户挑目录。
    /// </summary>
    public async Task RefreshProjectsAsync(CancellationToken ct = default)
    {
        string? folder = Path.GetDirectoryName(_database.Path);

        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        Status = Localizer.Instance.Get("Train.LoadingProjects");

        try
        {
            IReadOnlyList<TinyLabelProjectSummary> projects = await _catalog
                .ListAsync(StorageLocation.FromPath(folder), ct)
                .ConfigureAwait(true);

            Projects.Clear();

            foreach (TinyLabelProjectSummary project in projects)
            {
                Projects.Add(new ProjectOption(
                    project.Id, project.Name, project.ImageCount, project.LabeledCount));
            }

            if (Projects.Count == 0)
            {
                // 库里没有项目就不提示，状态回到就绪即可
                if (!IsTraining)
                {
                    Status = Localizer.Instance.Get("Train.Ready");
                }

                _logger.LogInformation("库里没有标注项目");
                return;
            }

            if (!IsTraining)
            {
                Status = Localizer.Instance.Get("Train.Ready");
            }

            _logger.LogInformation("库里有 {Count} 个标注项目", Projects.Count);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "读取标注项目失败");
            Status = Localizer.Instance.Format("Train.OpenProjectFailed", error.Message);
            AppendLog(Status);
        }
    }

    /// <summary>打开库里的一个项目当训练数据集：数据目录 + 项目 Id 交给统一解析入口。</summary>
    public async Task OpenProjectAsync(ProjectOption? project, CancellationToken ct = default)
    {
        if (project is null)
        {
            return;
        }

        string? folder = Path.GetDirectoryName(_database.Path);

        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        Status = Localizer.Instance.Get("Train.Opening");

        try
        {
            DatasetOpenResult opened = await _resolver
                .OpenAsync(
                    StorageLocation.FromPath(folder),
                    DatasetFormat.TinyLabelDb,
                    project.Id.ToString(),
                    ct)
                .ConfigureAwait(true);

            await ApplyOpenedAsync(opened, $"{project.Name} · #{project.Id}", ct).ConfigureAwait(true);

            _settings.LastTrainDataDirectory = folder;
            SaveSettings();
        }
        catch (Exception error)
        {
            _logger.LogError(error, "打开标注项目失败");
            Status = Localizer.Instance.Format("Train.OpenProjectFailed", error.Message);
            AppendLog(Status);
        }
    }

    /// <summary>
    /// 列出用户选中的库文件（任意位置的 <c>annotrain.db</c>）里的项目：
    /// 用独立只读连接查，不能借启动时那个工作库，否则读到的永远是默认库。
    /// </summary>
    public async Task<IReadOnlyList<TinyLabelProjectSummary>> ListLibraryProjectsAsync(
        string databaseFile,
        CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(
                    () => TinyLabelProjectCatalog.ReadFile(databaseFile, _logger, ct),
                    ct)
                .ConfigureAwait(true);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "读取标注项目失败");
            Status = Localizer.Instance.Format("Train.OpenProjectFailed", error.Message);
            AppendLog(Status);
            return [];
        }
    }

    /// <summary>上次用过的 TinyLabel 库文件（annotrain.db 全路径）；没选过就是空串。</summary>
    public string LastLibraryFile => _settings.LastTrainLibrary;

    /// <summary>上次在该库里选来训练的项目 Id；0 表示没记过。</summary>
    public long LastLibraryProjectId => _settings.LastTrainLibraryProjectId;

    /// <summary>打开指定库（annotrain.db）里的某个项目当训练数据集，并把「库 + 项目」记进配置。</summary>
    public async Task OpenLibraryProjectAsync(
        string databaseFile,
        long projectId,
        string source,
        CancellationToken ct = default)
    {
        string? folder = Path.GetDirectoryName(databaseFile);

        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        Status = Localizer.Instance.Get("Train.Opening");

        try
        {
            DatasetOpenResult opened = await _resolver
                .OpenAsync(
                    StorageLocation.FromPath(folder),
                    DatasetFormat.TinyLabelDb,
                    Path.GetFileName(databaseFile),
                    projectId.ToString(CultureInfo.InvariantCulture),
                    ct)
                .ConfigureAwait(true);

            await ApplyOpenedAsync(opened, source, ct).ConfigureAwait(true);

            _settings.LastTrainLibrary = databaseFile;
            _settings.LastTrainLibraryProjectId = projectId;
            _settings.LastTrainDataDirectory = folder;
            SaveSettings();
        }
        catch (Exception error)
        {
            _logger.LogError(error, "打开标注项目失败");
            Status = Localizer.Instance.Format("Train.OpenProjectFailed", error.Message);
            AppendLog(Status);
        }
    }

    /// <summary>启动时把上次的 TinyLabel 项目打开；库文件或项目没了就静默跳过。</summary>
    public async Task RestoreLastLibraryAsync(CancellationToken ct = default)
    {
        // 没记过项目就不自动打开，避免启动就替用户挑了数据集
        if (_settings.LastTrainLibraryProjectId <= 0)
        {
            return;
        }

        // 记的库文件没了就退回标注端那个库
        string database = _settings.LastTrainLibrary;

        if (string.IsNullOrWhiteSpace(database) || !File.Exists(database))
        {
            database = _database.Path;
        }

        if (!File.Exists(database))
        {
            return;
        }

        string? folder = Path.GetDirectoryName(database);

        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        IReadOnlyList<TinyLabelProjectSummary> projects;

        try
        {
            projects = await _catalog.ListAsync(StorageLocation.FromPath(folder), ct).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            // 启动恢复失败不打扰用户：状态留着，等他自己挑数据集
            _logger.LogWarning(error, "恢复上次的标注项目失败");
            return;
        }

        TinyLabelProjectSummary? project =
            projects.FirstOrDefault(item => item.Id == _settings.LastTrainLibraryProjectId)
            ?? projects.FirstOrDefault();

        if (project is null)
        {
            return;
        }

        _logger.LogInformation("启动恢复标注项目 {Project}（库 {Database}）", project.Name, database);

        await OpenLibraryProjectAsync(database, project.Id, $"{project.Name} · #{project.Id}", ct)
            .ConfigureAwait(true);
    }

    /// <summary>打开成功后统一收尾：换掉旧数据集、算摘要与统计、记类别。</summary>
    private async Task ApplyOpenedAsync(DatasetOpenResult opened, string source, CancellationToken ct)
    {
        (_store as IDisposable)?.Dispose();
        _store = opened.Store;

        DatasetPath = source;
        DatasetSummary = $"{opened.Store.Format.ToDisplayName()} · split {string.Join(" / ", opened.Store.Splits)} · " +
                         Localizer.Instance.Format("Train.Classes", opened.Store.Classes.Count);

        int images = 0;
        int boxes = 0;

        foreach (string split in opened.Store.Splits)
        {
            IReadOnlyList<DatasetSample> samples = await opened.Store
                .OpenSplit(split)
                .LoadSamplesAsync(ct)
                .ConfigureAwait(true);

            images += samples.Count;
            boxes += samples.Sum(sample => sample.Boxes.Count);
        }

        Classes = [.. opened.Store.Classes.Names];
        DatasetStats = Localizer.Instance.Format("Train.DatasetStats", images, boxes);
        Status = Localizer.Instance.Format("Train.DatasetOpened", DatasetStats);
        OnPropertyChanged(nameof(HasDataset));
    }

    // ── 训练 ──────────────────────────────────────────────────────────────────

    public async Task StartTrainingAsync()
    {
        if (IsTraining || _store is null)
        {
            Status = Localizer.Instance.Get(_store is null ? "Train.NeedDataset" : "Train.AlreadyRunning");
            return;
        }

        _training = new CancellationTokenSource();
        _control = new TrainControl(_training.Token);
        _control.PausedChanged += OnPausedChanged;

        IsTraining = true;
        IsPaused = false;
        Progress = 0;
        Points.Clear();
        RunInfo = $"epochs {Epochs:0} · batch {BatchSize:0} · imgsz {ImageSize:0}";

        Status = Localizer.Instance.Get("Train.Training");
        AppendLog(Localizer.Instance.Format("Train.Started", DatasetPath));

        // 进度与日志回调都在训练线程上触发：Progress<T> 会回到创建它的 UI 线程。
        var progress = new Progress<TrainingProgress>(item =>
        {
            Progress = item.Fraction;
            Points.Add(new ChartPoint(item.Epoch + 1, item.Loss, item.Map50));
            Status = $"epoch {item.Epoch + 1}/{item.TotalEpochs} · loss {item.Loss:F4} · mAP50 {item.Map50:F3}";
        });

        IProgress<string> log = new Progress<string>(AppendLog);

        var options = new TrainingRunOptions(
            StorageLocation.Parse(OutputDirectory),
            Epochs: (int)Epochs,
            BatchSize: (int)BatchSize,
            ImageSize: (int)ImageSize)
        {
            Progress = progress,
            Log = message => log.Report(message),
            Control = _control,
        };

        try
        {
            TrainingRunSummary summary = await Task
                .Run(() => _runner.TrainAsync(_store, options, _training.Token), _training.Token)
                .ConfigureAwait(true);

            Progress = 1;
            Status = summary.Summary;
            AppendLog(summary.Summary);

            foreach (string warning in summary.Warnings.Take(10))
            {
                AppendLog($"  ! {warning}");
            }

            _settings.LastTrainDataDirectory = Path.GetDirectoryName(DatasetPath) ?? _settings.LastTrainDataDirectory;
            SaveSettings();
        }
        catch (OperationCanceledException)
        {
            Status = Localizer.Instance.Get("Train.Stopped");
            AppendLog(Status);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "训练失败");
            Status = Localizer.Instance.Format("Train.Failed", error.Message);
            AppendLog(Status);
        }
        finally
        {
            IsTraining = false;
            IsPaused = false;

            if (_control is { } control)
            {
                control.PausedChanged -= OnPausedChanged;
                control.Dispose();
                _control = null;
            }

            _training?.Dispose();
            _training = null;
        }
    }

    /// <summary>暂停：训练线程在下一个批次边界停住，参数、优化器、EMA 全部原样保留。</summary>
    public void PauseTraining()
    {
        if (!IsTraining || _control is null || _control.IsPaused)
        {
            return;
        }

        Status = Localizer.Instance.Get("Train.Pausing");
        _control.Pause();
    }

    /// <summary>继续：从暂停处接着跑，不重新加载权重。</summary>
    public void ResumeTraining()
    {
        if (!IsTraining || _control is null || !_control.IsPaused)
        {
            return;
        }

        Status = Localizer.Instance.Get("Train.Resuming");
        _control.Resume();
    }

    /// <summary>结束训练：取消令牌，训练线程在批次边界退出。</summary>
    public void StopTraining()
    {
        if (!IsTraining || _training is null)
        {
            return;
        }

        // 暂停状态下也允许结束：先放开暂停，否则训练线程听不到取消。
        _control?.Resume();

        _training.Cancel();
        Status = Localizer.Instance.Get("Train.Stopping");
    }

    private void OnPausedChanged(bool paused)
    {
        IsPaused = paused;

        if (paused)
        {
            Status = Localizer.Instance.Get("Train.Paused");
        }
    }

    /// <summary>给窗口用：把提示信息同时写进状态行与日志。</summary>
    public void Report(string message)
    {
        Status = message;
        AppendLog(message);
    }

    /// <summary>「日志」窗口里的清空按钮。</summary>
    public void ClearLog() => LogText = string.Empty;

    /// <summary>图表控件用：提示文字随状态变化。</summary>
    public string ChartHint => IsTraining
        ? Localizer.Instance.Get("Train.ChartRunning")
        : Localizer.Instance.Get("Train.ChartIdle");

    // ── 内部 ──────────────────────────────────────────────────────────────────

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

    private void AppendLog(string text)
        => LogText = string.IsNullOrEmpty(LogText) ? text : $"{LogText}{Environment.NewLine}{text}";
}

/// <summary>曲线上的一个点。</summary>
public sealed record ChartPoint(int Epoch, double Loss, double Map);

/// <summary>「标注项目」下拉的一行：项目 Id 就是打开数据集时的 datasetId。</summary>
public sealed record ProjectOption(long Id, string Name, int ImageCount, int LabeledCount)
{
    /// <summary>下拉里显示「项目名 · 已标/总数」。</summary>
    public string DisplayName => $"{Name} · {LabeledCount}/{ImageCount}";
}
