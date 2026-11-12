using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Sevencat.Core;
using Sevencat.Core.Dataset;
using Sevencat.Core.Infrastructure;
using Sevencat.Core.Infrastructure.Logging;
using Sevencat.Core.Localization;
using Sevencat.Core.Simulation;
using Sevencat.Core.Storage;

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

    private IDatasetStore? _store;
    private CancellationTokenSource? _training;

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

    [ObservableProperty]
    private string _outputDirectory = Path.Combine(AppDataPaths.DataDirectory, "runs");

    [ObservableProperty]
    private bool _isTraining;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _runInfo = string.Empty;

    /// <summary>训练曲线数据点（epoch / loss / mAP）。干跑阶段为空，控件显示占位提示。</summary>
    public ObservableCollection<ChartPoint> Points { get; } = [];

    /// <summary>数据集类别（检测验证时用来给框命名）。</summary>
    public IReadOnlyList<string> Classes { get; private set; } = [];

    /// <summary>当前打开的数据集是否可用于训练。</summary>
    public bool HasDataset => _store is not null;

    /// <summary>语言下拉的候选。</summary>
    public IReadOnlyList<LanguageOption> Languages => Localizer.Supported;

    private static Localizer Loc => Localizer.Instance;

    public MainWindowViewModel(
        ILogger<MainWindowViewModel> logger,
        LogBuffer logBuffer,
        DatasetStoreResolver resolver,
        IDatasetTrainingRunner runner,
        AppSettings settings,
        SettingsStore settingsStore)
    {
        _logger = logger;
        _resolver = resolver;
        _runner = runner;
        _settings = settings;
        _settingsStore = settingsStore;

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
    }

    // ── 数据集 ────────────────────────────────────────────────────────────────

    public async Task OpenDatasetAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Status = Localizer.Instance.Get("Train.Opening");

        try
        {
            DatasetOpenResult opened = await _resolver
                .OpenAsync(StorageLocation.Parse(path), format: null, datasetId: null, ct)
                .ConfigureAwait(true);

            (_store as IDisposable)?.Dispose();
            _store = opened.Store;

            DatasetPath = path;
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

    // ── 训练 ──────────────────────────────────────────────────────────────────

    public async Task StartTrainingAsync()
    {
        if (IsTraining || _store is null)
        {
            Status = Localizer.Instance.Get(_store is null ? "Train.NeedDataset" : "Train.AlreadyRunning");
            return;
        }

        _training = new CancellationTokenSource();
        IsTraining = true;
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
            _training?.Dispose();
            _training = null;
        }
    }

    public void StopTraining()
    {
        if (!IsTraining || _training is null)
        {
            return;
        }

        _training.Cancel();
        Status = Localizer.Instance.Get("Train.Stopping");
    }

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
