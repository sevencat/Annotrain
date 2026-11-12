using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Infrastructure;
using Sevencat.Core.Localization;
using Sevencat.Core.Simulation;
using Sevencat.Core.Storage;

namespace Sevencat.Train.App.ViewModels;

/// <summary>模拟数据要存成什么。</summary>
public enum SimulationTarget
{
    Yolo = 0,
    RfDetr = 1,
    TinyLabel = 2,
}

/// <summary>格式下拉的一行。</summary>
public sealed record TargetOption(SimulationTarget Value, string DisplayName);

/// <summary>
/// 「模拟数据」对话框：选尺寸 / 数量 / 落盘格式，随机画圆、正方形、三角形，
/// 图片与标注一起生成（YOLO 目录、RF-DETR 目录，或 TinyLabel 的库 + 数据目录）。
/// </summary>
public partial class SimulationViewModel : ObservableObject
{
    /// <summary>每张图最少画几个形状。</summary>
    private const int MinShapes = 1;

    private readonly ILogger<SimulationViewModel> _logger;
    private readonly BlobStoreRegistry _registry;
    private readonly TinyLabelDatabase _database;

    [ObservableProperty]
    private decimal _imageWidth = 640;

    [ObservableProperty]
    private decimal _imageHeight = 640;

    [ObservableProperty]
    private decimal _imageCount = 60;

    [ObservableProperty]
    private decimal _valPercent = 20;

    [ObservableProperty]
    private decimal _maxShapes = 4;

    [ObservableProperty]
    private TargetOption _selectedTarget;

    [ObservableProperty]
    private string _outputDirectory;

    [ObservableProperty]
    private string _databasePath;

    [ObservableProperty]
    private string _dataDirectory;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _status;

    public SimulationViewModel(
        ILogger<SimulationViewModel> logger,
        BlobStoreRegistry registry,
        TinyLabelDatabase database)
    {
        _logger = logger;
        _registry = registry;
        _database = database;

        Targets =
        [
            new(SimulationTarget.Yolo, Loc.Get("Sim.Yolo")),
            new(SimulationTarget.RfDetr, Loc.Get("Sim.RfDetr")),
            new(SimulationTarget.TinyLabel, Loc.Get("Sim.TinyLabel")),
        ];

        _selectedTarget = Targets[0];
        _outputDirectory = DefaultOutputDirectory(SimulationTarget.Yolo);
        _databasePath = database.Path;
        _dataDirectory = Path.GetDirectoryName(database.Path) ?? AppDataPaths.DataDirectory;
        _status = Loc.Get("Sim.Intro");
    }

    /// <summary>格式下拉候选。</summary>
    public IReadOnlyList<TargetOption> Targets { get; }

    /// <summary>TinyLabel：库 + 数据目录，而不是一个输出目录。</summary>
    public bool IsTinyLabel => SelectedTarget.Value == SimulationTarget.TinyLabel;

    /// <summary>YOLO / RF-DETR：只要一个输出目录。</summary>
    public bool IsFolderTarget => !IsTinyLabel;

    public bool CanGenerate => !IsBusy;

    public string ShapesHint => Loc.Format("Sim.ShapesHint", (int)MaxShapes);

    /// <summary>这次对话框里成功生成过没有（关窗后主界面用它决定要不要刷新项目列表）。</summary>
    public bool HasGenerated { get; private set; }

    private static Localizer Loc => Localizer.Instance;

    partial void OnSelectedTargetChanged(TargetOption value)
    {
        OnPropertyChanged(nameof(IsTinyLabel));
        OnPropertyChanged(nameof(IsFolderTarget));

        // 换格式时把默认目录一起换掉；用户自己改过就保留
        if (IsDefaultOutputDirectory(OutputDirectory))
        {
            OutputDirectory = DefaultOutputDirectory(value.Value);
        }
    }

    partial void OnMaxShapesChanged(decimal value) => OnPropertyChanged(nameof(ShapesHint));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanGenerate));

    /// <summary>选了数据库就把数据目录带成库所在目录（用户随后可以改）。</summary>
    partial void OnDatabasePathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            string? folder = Path.GetDirectoryName(value.Trim());
            if (!string.IsNullOrWhiteSpace(folder))
            {
                DataDirectory = folder;
            }
        }
        catch (ArgumentException)
        {
            // 路径写了一半，先不动数据目录
        }
    }

    /// <summary>
    /// 按当前参数生成。成功返回给用户看的报告（由界面弹窗展示）；失败返回 <c>null</c>，
    /// 原因写在 <see cref="Status"/> 上。
    /// </summary>
    public async Task<string?> GenerateAsync(CancellationToken ct = default)
    {
        if (IsBusy)
        {
            return null;
        }

        int count = (int)ImageCount;
        if (count < 1)
        {
            Status = Loc.Get("Sim.BadCount");
            return null;
        }

        var generation = new SyntheticOptions(
            count,
            (int)ImageWidth,
            (int)ImageHeight,
            MinShapes,
            Math.Max(MinShapes, (int)MaxShapes));

        double valRatio = Math.Clamp((double)ValPercent / 100.0, 0, 0.9);

        IsBusy = true;
        Progress = 0;
        Status = Loc.Get("Sim.Working");

        var progress = new Progress<(int Done, int Total)>(item =>
        {
            Progress = item.Total <= 0 ? 0 : Math.Min(1, (double)item.Done / item.Total);
            Status = Loc.Format("Sim.Progress", item.Done, item.Total);
        });

        try
        {
            return IsTinyLabel
                ? await GenerateIntoDatabaseAsync(generation, valRatio, progress, ct).ConfigureAwait(true)
                : await GenerateIntoFolderAsync(generation, valRatio, progress, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Status = Loc.Get("Sim.Cancelled");
        }
        catch (Exception error)
        {
            _logger.LogError(error, "生成模拟数据失败");
            Status = Loc.Format("Sim.Failed", error.Message);
        }
        finally
        {
            IsBusy = false;
        }

        return null;
    }

    private async Task<string?> GenerateIntoFolderAsync(
        SyntheticOptions generation,
        double valRatio,
        IProgress<(int Done, int Total)> progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            Status = Loc.Get("Sim.NeedFolder");
            return null;
        }

        SyntheticExportFormat format = SelectedTarget.Value == SimulationTarget.RfDetr
            ? SyntheticExportFormat.RfDetr
            : SyntheticExportFormat.Yolo;

        string folder = OutputDirectory.Trim();

        SyntheticWriteResult result = await Task.Run(
            () => SyntheticDatasetWriter.Write(generation, format, folder, valRatio, progress, ct),
            ct).ConfigureAwait(true);

        Progress = 1;
        HasGenerated = true;
        Status = Loc.Format("Sim.Done", result.Images, result.Boxes);

        _logger.LogInformation(
            "模拟数据已生成：{Folder}（{Images} 张 / {Boxes} 框，{Format}）",
            folder,
            result.Images,
            result.Boxes,
            format);

        return BuildReport(result);
    }

    /// <summary>成功弹窗的内容：张数、训练/验证划分、每类框数、产物路径、用时。</summary>
    private static string BuildReport(SyntheticWriteResult result)
    {
        int train = result.ImagesPerSplit.TryGetValue("train", out int trainCount) ? trainCount : 0;
        int val = result.ImagesPerSplit.TryGetValue("val", out int valCount) ? valCount : 0;

        return string.Join(
            Environment.NewLine,
            Loc.Format("Sim.ResultImages", result.Images, train, val),
            Loc.Format("Sim.ResultBoxes", result.Boxes, FormatClasses(result.ObjectsPerClass)),
            Loc.Format("Sim.ResultOutput", result.DescriptorPath),
            Loc.Format(
                "Sim.ResultElapsed",
                result.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)));
    }

    /// <summary>「square 11 · triangle 8」这种每类计数。</summary>
    private static string FormatClasses(IReadOnlyList<int> counts)
    {
        var parts = new List<string>(counts.Count);

        for (int index = 0; index < counts.Count && index < SyntheticImageGenerator.ClassNames.Count; index++)
        {
            parts.Add($"{SyntheticImageGenerator.ClassNames[index]} {counts[index]}");
        }

        return string.Join(" · ", parts);
    }

    private async Task<string?> GenerateIntoDatabaseAsync(
        SyntheticOptions generation,
        double valRatio,
        IProgress<(int Done, int Total)> progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(DatabasePath) || string.IsNullOrWhiteSpace(DataDirectory))
        {
            Status = Loc.Get("Sim.NeedFolder");
            return null;
        }

        string databasePath = DatabasePath.Trim();
        string dataDirectory = DataDirectory.Trim();

        // 只往已有库里写：路径不对就直接失败，不新建库，也不落一张图
        if (!File.Exists(databasePath))
        {
            Status = Loc.Format("Sim.DatabaseMissing", databasePath);
            _logger.LogWarning("待写入的数据库不存在：{Database}", databasePath);
            return null;
        }

        var import = new SyntheticImportOptions(
            dataDirectory,
            Loc.Format("Sim.ProjectName", DateTime.Now.ToString("MMdd-HHmmss", CultureInfo.InvariantCulture)),
            valRatio,
            JitterPixels: 0,
            Seed: null)
        {
            // 库和图片目录可以分开放
            DatabasePath = databasePath,
        };

        // 画图这一段自己走一遍，好把进度报到界面上（写库那半段没有逐张回调）
        var images = new List<SyntheticImage>(generation.ImageCount);
        var perSplit = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int done = 0;

        foreach (SyntheticImage image in new SyntheticImageGenerator(generation).Enumerate())
        {
            ct.ThrowIfCancellationRequested();

            string split = SyntheticDatasetWriter.ResolveSplit(done, generation.ImageCount, valRatio);
            perSplit[split] = perSplit.TryGetValue(split, out int seen) ? seen + 1 : 1;

            images.Add(image);
            done++;
            progress.Report((done, generation.ImageCount * 2));
        }

        SyntheticImportResult result = await SyntheticLabelingSession
            .ImportAsync(images, _registry, import, _logger, ct, _database)
            .ConfigureAwait(true);

        Progress = 1;
        HasGenerated = true;
        Status = Loc.Format("Sim.Done", result.ImageCount, result.ShapeCount);

        _logger.LogInformation(
            "模拟数据已写入库：{Database}（项目 {ProjectId}，{Images} 张 / {Shapes} 框）",
            result.DatabasePath,
            result.ProjectId,
            result.ImageCount,
            result.ShapeCount);

        int train = perSplit.TryGetValue("train", out int trainCount) ? trainCount : 0;
        int val = perSplit.TryGetValue("val", out int valCount) ? valCount : 0;

        var perClass = new int[SyntheticImageGenerator.ClassNames.Count];
        foreach (SyntheticImage image in images)
        {
            foreach (SyntheticShape shape in image.Shapes)
            {
                perClass[(int)shape.Kind]++;
            }
        }

        return string.Join(
            Environment.NewLine,
            Loc.Format("Sim.ResultImages", result.ImageCount, train, val),
            Loc.Format("Sim.ResultBoxes", result.ShapeCount, FormatClasses(perClass)),
            Loc.Format("Sim.ResultProject", result.ProjectId),
            Loc.Format("Sim.ResultDatabase", result.DatabasePath),
            Loc.Format("Sim.ResultDataDir", dataDirectory));
    }

    private static string DefaultOutputDirectory(SimulationTarget target) => target switch
    {
        SimulationTarget.RfDetr => Path.Combine(AppDataPaths.DataDirectory, "simulated-rfdetr"),
        SimulationTarget.TinyLabel => Path.Combine(AppDataPaths.DataDirectory, "simulated-project"),
        _ => Path.Combine(AppDataPaths.DataDirectory, "simulated-yolo"),
    };

    private static bool IsDefaultOutputDirectory(string value)
        => value == DefaultOutputDirectory(SimulationTarget.Yolo)
           || value == DefaultOutputDirectory(SimulationTarget.RfDetr)
           || value == DefaultOutputDirectory(SimulationTarget.TinyLabel);
}
