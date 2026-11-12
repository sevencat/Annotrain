using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Sevencat.Core;
using Sevencat.Core.Dataset;
using Sevencat.Core.Dataset.Import;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Infrastructure.Logging;
using Sevencat.Core.Model;
using Sevencat.Core.Storage;
using Sevencat.Label;

namespace Sevencat.Label.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly LabelDatasetImportService _importer;
    private readonly LabelProjectBrowser _browser;
    private readonly BlobStoreRegistry _registry;

    [ObservableProperty]
    private string _title = $"{AnnotrainInfo.ProductName} · 标注";

    [ObservableProperty]
    private string _status = "就绪";

    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    private string _projectTitle = "未打开项目";

    /// <summary>要导入的源数据集目录（YOLO 的 data.yaml 所在目录 / RF-DETR 的根目录）。</summary>
    [ObservableProperty]
    private string _importSource = string.Empty;

    /// <summary>导入后项目库放哪（annotrain.db 与 projects/ 都在这）。</summary>
    [ObservableProperty]
    private string _importDestination = string.Empty;

    [ObservableProperty]
    private string _importProjectName = string.Empty;

    [ObservableProperty]
    private bool _isImporting;

    /// <summary>当前项目的图片清单（按 split 顺序）。</summary>
    public ObservableCollection<ProjectImageViewModel> Images { get; } = [];

    /// <summary>列表选中项；外部设置（如打开项目后自动选第一张）会触发预览加载。</summary>
    [ObservableProperty]
    private ProjectImageViewModel? _selectedImage;

    /// <summary>当前选中图片的预览与叠框。</summary>
    [ObservableProperty]
    private Bitmap? _preview;

    [ObservableProperty]
    private double _previewWidth = 1;

    [ObservableProperty]
    private double _previewHeight = 1;

    [ObservableProperty]
    private string _selectionInfo = "打开项目后在这里看图片与标注";

    public ObservableCollection<BoxOverlay> OverlayBoxes { get; } = [];

    public MainWindowViewModel(
        ILogger<MainWindowViewModel> logger,
        LogBuffer logBuffer,
        LabelDatasetImportService importer,
        LabelProjectBrowser browser,
        BlobStoreRegistry registry)
    {
        _logger = logger;
        _importer = importer;
        _browser = browser;
        _registry = registry;

        logger.LogInformation("主窗口已创建，等待打开项目");

        LogText = string.Join(Environment.NewLine, logBuffer.Snapshot().Select(entry => entry.ToString()));
    }

    // ── 打开项目 ──────────────────────────────────────────────────────────────

    /// <summary>打开一个标注项目（「图片目录 + SQLite」），把图片清单填进左侧列表。</summary>
    public async Task OpenProjectAsync(string folder, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        Status = "正在打开项目…";

        try
        {
            OpenedProject project = await _browser.OpenAsync(folder, ct).ConfigureAwait(true);

            ProjectTitle = $"项目：{project.ProjectName}";

            Images.Clear();
            OverlayBoxes.Clear();
            Preview = null;
            SelectionInfo = "打开项目后在这里看图片与标注";

            foreach (ProjectImage image in project.Images)
            {
                Images.Add(new ProjectImageViewModel(image));
            }

            Status = $"已打开「{project.ProjectName}」：{Images.Count} 张图 / {project.BoxCount} 个框，" +
                     $"split {string.Join(" / ", project.Splits)}";
            AppendLog(Status);

            SelectedImage = Images.FirstOrDefault();
        }
        catch (DatasetProjectSelectionException error)
        {
            Status = "这个库里有多个项目，暂不支持选择";
            AppendLog($"打开失败：{error.Message}");
        }
        catch (Exception error)
        {
            _logger.LogError(error, "打开项目失败");
            Status = $"打开失败：{error.Message}";
            AppendLog($"打开失败：{error.Message}");
        }
    }

    /// <summary>列表选中项变化时自动加载预览（用户点击与程序预设走同一条路）。</summary>
    partial void OnSelectedImageChanged(ProjectImageViewModel? value)
    {
        if (value is not null)
        {
            _ = ShowAsync(value);
        }
    }

    /// <summary>选中一张图：读图片字节做预览，并把框按原图像素坐标叠上去。</summary>
    public async Task ShowAsync(ProjectImageViewModel? item, CancellationToken ct = default)
    {
        if (item is null)
        {
            return;
        }

        Bitmap? bitmap = await LoadBitmapAsync(item.Sample.Image, ct).ConfigureAwait(true);

        Preview = bitmap;
        PreviewWidth = Math.Max(1, item.Sample.Width);
        PreviewHeight = Math.Max(1, item.Sample.Height);

        OverlayBoxes.Clear();

        foreach (BoxAnnotation box in item.Sample.Boxes)
        {
            OverlayBoxes.Add(new BoxOverlay(box.X1, box.Y1, box.Width, box.Height, box.ClassId));
        }

        SelectionInfo = $"{item.RelativePath} — {item.Sample.Width}×{item.Sample.Height}，" +
                        $"{item.BoxCount} 个框（{item.Split}）";

        _logger.LogInformation(
            "预览：{Image}，{Width}×{Height}，{Boxes} 个框，位图{State}",
            item.RelativePath,
            item.Sample.Width,
            item.Sample.Height,
            item.BoxCount,
            bitmap is null ? "解码失败" : "已加载");
    }

    // ── 导入 ──────────────────────────────────────────────────────────────────

    /// <summary>把源数据集导成标注项目：图片、标注与 train/val 一起搬过来，导入后直接打开。</summary>
    public async Task ImportAsync(CancellationToken ct = default)
    {
        if (IsImporting)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ImportSource) || string.IsNullOrWhiteSpace(ImportDestination))
        {
            Status = "请先选择源数据集目录与目标目录";
            return;
        }

        IsImporting = true;
        Status = "正在导入…";

        try
        {
            DatasetImportReport report = await _importer
                .ImportAsync(
                    new DatasetImportOptions(
                        StorageLocation.Parse(ImportSource),
                        StorageLocation.Parse(ImportDestination),
                        string.IsNullOrWhiteSpace(ImportProjectName) ? null : ImportProjectName.Trim()),
                    ct)
                .ConfigureAwait(true);

            Status = report.Summary;
            AppendLog(report);

            await OpenProjectAsync(ImportDestination, ct).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "导入数据集失败");
            Status = $"导入失败：{error.Message}";
            AppendLog($"导入失败：{error.Message}");
        }
        finally
        {
            IsImporting = false;
        }
    }

    // ── 内部 ──────────────────────────────────────────────────────────────────

    private async Task<Bitmap?> LoadBitmapAsync(ImageRef image, CancellationToken ct)
    {
        try
        {
            IBlobStore store = _registry.Get(image.Location);
            var key = new StorageKey(image.Location, image.RelativePath);

            await using Stream input = await store.OpenReadAsync(key, ct).ConfigureAwait(true);
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, ct).ConfigureAwait(true);

            buffer.Position = 0;
            return new Bitmap(buffer);
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "读取图片失败：{Image}", image);
            return null;
        }
    }

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
