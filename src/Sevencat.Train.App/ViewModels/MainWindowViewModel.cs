using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Sevencat.Core;
using Sevencat.Core.Dataset;
using Sevencat.Core.Infrastructure;
using Sevencat.Core.Infrastructure.Logging;
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
    private string _title = $"{AnnotrainInfo.ProductName} · 训练";

    [ObservableProperty]
    private string _status = "就绪";

    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    private string _datasetPath = string.Empty;

    [ObservableProperty]
    private string _datasetSummary = "未选择数据集";

    [ObservableProperty]
    private string _datasetStats = string.Empty;

    [ObservableProperty]
    private decimal _epochs = 10;

    [ObservableProperty]
    private decimal _batchSize = 4;

    [ObservableProperty]
    private decimal _imageSize = 640;

    [ObservableProperty]
    private string _outputDirectory = Path.Combine(AppPaths.ConfigDirectory, "runs");

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

        if (!string.IsNullOrWhiteSpace(settings.LastDataDirectory))
        {
            OutputDirectory = Path.Combine(settings.LastDataDirectory, "runs");
        }

        logger.LogInformation("训练主窗口已创建，等待选择数据集");

        LogText = string.Join(Environment.NewLine, logBuffer.Snapshot().Select(entry => entry.ToString()));
    }

    // ── 数据集 ────────────────────────────────────────────────────────────────

    public async Task OpenDatasetAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Status = "正在打开数据集…";

        try
        {
            DatasetOpenResult opened = await _resolver
                .OpenAsync(StorageLocation.Parse(path), format: null, datasetId: null, ct)
                .ConfigureAwait(true);

            (_store as IDisposable)?.Dispose();
            _store = opened.Store;

            DatasetPath = path;
            DatasetSummary = $"{opened.Store.Format.ToDisplayName()} · split {string.Join(" / ", opened.Store.Splits)} · " +
                             $"{opened.Store.Classes.Count} 类";

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
            DatasetStats = $"{images} 张图 / {boxes} 个框";
            Status = $"已打开数据集：{DatasetStats}";
            OnPropertyChanged(nameof(HasDataset));

            _settings.LastDataDirectory = Path.GetDirectoryName(path) ?? string.Empty;
            SaveSettings();
        }
        catch (Exception error)
        {
            _logger.LogError(error, "打开数据集失败");
            Status = $"打开数据集失败：{error.Message}";
            AppendLog(Status);
        }
    }

    // ── 训练 ──────────────────────────────────────────────────────────────────

    public async Task StartTrainingAsync()
    {
        if (IsTraining || _store is null)
        {
            Status = _store is null ? "先选择数据集" : "训练已在进行";
            return;
        }

        _training = new CancellationTokenSource();
        IsTraining = true;
        Progress = 0;
        Points.Clear();
        RunInfo = $"epochs {Epochs:0} · batch {BatchSize:0} · imgsz {ImageSize:0}";

        Status = "训练中…";
        AppendLog($"开始训练：{DatasetPath}");

        try
        {
            TrainingRunSummary summary = await _runner
                .TrainAsync(
                    _store,
                    new TrainingRunOptions(StorageLocation.Parse(OutputDirectory)),
                    _training.Token)
                .ConfigureAwait(true);

            Progress = 1;
            Status = summary.Summary;
            AppendLog(summary.Summary);

            foreach (string warning in summary.Warnings.Take(10))
            {
                AppendLog($"  ! {warning}");
            }

            _settings.LastDataDirectory = Path.GetDirectoryName(DatasetPath) ?? _settings.LastDataDirectory;
            SaveSettings();
        }
        catch (OperationCanceledException)
        {
            Status = "训练已停止";
            AppendLog(Status);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "训练失败");
            Status = $"训练失败：{error.Message}";
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
        Status = "正在停止…";
    }

    /// <summary>图表控件用：提示文字随状态变化。</summary>
    public string ChartHint => IsTraining
        ? "真实训练引擎接入后，这里显示每个 epoch 的 loss 与 mAP"
        : "开始训练后显示 loss / mAP 曲线";

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
