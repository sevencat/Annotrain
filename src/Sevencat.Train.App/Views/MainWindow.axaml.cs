using System.Collections.Specialized;
using System.Diagnostics;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Sevencat.Core.AutoLabel;
using Sevencat.Core.Dataset;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Infrastructure;
using Sevencat.Core.Localization;
using Sevencat.Train.App.Controls;
using Sevencat.Train.App.ViewModels;

namespace Sevencat.Train.App.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _chartSource;

    public MainWindow()
    {
        InitializeComponent();

        // 曲线是命令式控件，必须把 ViewModel 的逐轮指标喂给它，否则训练时一直是空白提示
        DataContextChanged += (_, _) => AttachChart();
    }

    /// <summary>数据源换掉之后刷新曲线（启动自动恢复时也要调一次）。</summary>
    public void RefreshChartHint() => AttachChart();

    private void AttachChart()
    {
        var viewModel = DataContext as MainWindowViewModel;

        if (!ReferenceEquals(_chartSource, viewModel))
        {
            if (_chartSource is not null)
            {
                _chartSource.Points.CollectionChanged -= OnPointsChanged;
            }

            _chartSource = viewModel;

            if (viewModel is not null)
            {
                viewModel.Points.CollectionChanged += OnPointsChanged;
            }
        }

        if (_chartSource is null)
        {
            return;
        }

        Chart.SetPoints(_chartSource.Points);
        Chart.SetHint(_chartSource.ChartHint);
    }

    private void OnPointsChanged(object? sender, NotifyCollectionChangedEventArgs e) => AttachChart();

    /// <summary>「训练参数」：epochs / batch / imgsz 收进对话框改，界面只做只读展示。</summary>
    private async void OnEditParams(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var dialog = new TrainingParamsDialog(viewModel.Epochs, viewModel.BatchSize, viewModel.ImageSize);

        if (await dialog.ShowDialog<TrainParams?>(this) is { } updated)
        {
            viewModel.Epochs = updated.Epochs;
            viewModel.BatchSize = updated.BatchSize;
            viewModel.ImageSize = updated.ImageSize;
        }
    }

    /// <summary>「YOLO」：选目录，按 YOLO 格式打开。</summary>
    private async void OnOpenYolo(object? sender, RoutedEventArgs e)
    {
        await OpenDatasetFolderAsync(DatasetFormat.Yolo, Loc.Get("Train.OpenYoloTip"));
    }

    /// <summary>「RF-DETR」：选目录，按 COCO（RF-DETR）格式打开。</summary>
    private async void OnOpenRfDetr(object? sender, RoutedEventArgs e)
    {
        await OpenDatasetFolderAsync(DatasetFormat.Coco, Loc.Get("Train.OpenRfDetrTip"));
    }

    private async Task OpenDatasetFolderAsync(DatasetFormat format, string title)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        string? folder = await PickFolderAsync(title);

        if (folder is null)
        {
            return;
        }

        await viewModel.OpenDatasetAsync(folder, format);
        Chart.SetHint(viewModel.ChartHint);
    }

    /// <summary>
    /// 「TinyLabel」：先选一个 <c>annotrain.db</c>，读出库里的项目让用户挑一个，
    /// 再把该项目的图片与标注读成训练数据集。文件框停在上次用的库目录。
    /// </summary>
    private async void OnOpenTinyLabel(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        string? database = await PickFileAsync(
            Loc.Get("Train.OpenTinyLabelTip"),
            [new FilePickerFileType("TinyLabel") { Patterns = ["*.db"] }],
            Path.GetDirectoryName(viewModel.LastLibraryFile));

        if (database is null)
        {
            return;
        }

        while (true)
        {
            string folder = Path.GetDirectoryName(database)!;

            IReadOnlyList<TinyLabelProjectSummary> projects = await viewModel.ListLibraryProjectsAsync(database);

            if (projects.Count == 0)
            {
                viewModel.Report(Loc.Format("Train.NoProjectsInLibrary", database));
                return;
            }

            ProjectPickResult? picked = await new ProjectPickerDialog(
                    projects, database, viewModel.LastLibraryProjectId)
                .ShowDialog<ProjectPickResult?>(this);

            if (picked is null)
            {
                return;
            }

            if (picked.IsChangeLibrary)
            {
                string? chosen = await PickFileAsync(
                    Loc.Get("Train.OpenTinyLabelTip"),
                    [new FilePickerFileType("TinyLabel") { Patterns = ["*.db"] }],
                    Path.GetDirectoryName(database));

                if (chosen is null)
                {
                    continue;
                }

                database = chosen;
                continue;
            }

            TinyLabelProjectSummary project = picked.Project!;

            // 打开项目 = 拉取它的图片清单与标注，顺手记住「库 + 项目」
            await viewModel.OpenLibraryProjectAsync(database, project.Id, $"{project.Name} · #{project.Id}");
            Chart.SetHint(viewModel.ChartHint);
            return;
        }
    }

    private async void OnPickOutput(object? sender, RoutedEventArgs e)
    {
        string? folder = await PickFolderAsync(Localizer.Instance.Get("Train.OutputDir"));

        if (folder is not null && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.OutputDirectory = folder;
        }
    }

    /// <summary>「打开」：在资源管理器里打开输出目录（不存在就先建出来，免得报错）。</summary>
    private void OnOpenOutput(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(viewModel.OutputDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{viewModel.OutputDirectory}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            viewModel.Report(Localizer.Instance.Format("Train.OpenOutputFailed", viewModel.OutputDirectory));
        }
    }

    private async void OnStart(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        Chart.SetHint(viewModel.ChartHint);
        await viewModel.StartTrainingAsync();
        Chart.SetHint(viewModel.ChartHint);
    }

    private void OnStop(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.StopTraining();
        }
    }

    private void OnPause(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.PauseTraining();
        }
    }

    private void OnResume(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.ResumeTraining();
        }
    }

    // ── 顶栏：服务入口 / 数据目录 / 关于 / 日志 ────────────────────────────────

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

    /// <summary>服务地址后面的「打开」：端口在听就交给浏览器，没在听就只提示（免得开出一个打不开的页面）。</summary>
    private void OnOpenService(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (!IsListening(viewModel.ServicePort))
        {
            viewModel.Report(Loc.Get("Train.ServiceNotReady"));
            return;
        }

        if (!OpenInBrowser(viewModel.ServiceUrl))
        {
            viewModel.Report(Loc.Format("Train.OpenServiceFailed", viewModel.ServiceUrl));
        }
    }

    private static bool IsListening(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", port).Wait(300);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>「模拟数据」：随机画圆 / 正方形 / 三角形连同标注一起生成，存成 YOLO、RF-DETR 或 TinyLabel。</summary>
    private async void OnSimulate(object? sender, RoutedEventArgs e)
    {
        if (Application.Current is not App app || DataContext is not MainWindowViewModel main)
        {
            return;
        }

        var simulation = ActivatorUtilities.CreateInstance<SimulationViewModel>(app.Services);
        var dialog = new SimulationWindow(simulation);

        await dialog.ShowDialog(this);

        // 生成到当前库里的话，主界面的项目下拉要能立刻看见
        if (simulation.HasGenerated)
        {
            await main.RefreshProjectsAsync();
        }
    }

    private async void OnAbout(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var dialog = new AboutWindow(viewModel.ServiceUrl, AppDataPaths.DataDirectory, viewModel.OutputDirectory);

        await dialog.ShowDialog(this);
    }

    private static Localizer Loc => Localizer.Instance;

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

    /// <summary>
    /// 检测验证：选图片 → 用 ONNX 模型推理 → 弹窗把结果叠在图上。
    /// 不占主页空间，只在需要时开一个对话框。
    /// </summary>
    private async void OnDetect(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        IServiceProvider? services = (Application.Current as App)?.Services;
        if (services is null)
        {
            return;
        }

        AppSettings settings = services.GetRequiredService<AppSettings>();
        SettingsStore settingsStore = services.GetRequiredService<SettingsStore>();

        string? imagePath = await PickFileAsync(
            "选择要检测的图片",
            [new FilePickerFileType("图片") { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp", "*.webp"] }]);

        if (imagePath is null)
        {
            return;
        }

        string modelPath = settings.AutoLabelModelPath;

        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            string? picked = await PickFileAsync(
                "选择 ONNX 模型",
                [new FilePickerFileType("ONNX 模型") { Patterns = ["*.onnx"] }]);

            if (picked is null)
            {
                viewModel.Status = "没有可用模型，检测取消";
                return;
            }

            modelPath = picked;
            settings.AutoLabelModelPath = modelPath;
            SaveSettings(settingsStore, settings);
        }

        try
        {
            viewModel.Status = "检测中…";

            using var runner = new YoloOnnxRunner(modelPath);
            using var bitmap = new Bitmap(imagePath);

            byte[] bgra = BitmapPixels.ToBgra(bitmap, out int width, out int height);

            var options = new AutoLabelOptions(
                modelPath,
                settings.AutoLabelConfidence,
                ClassNames: viewModel.Classes.Count > 0 ? viewModel.Classes : null);

            IReadOnlyList<AutoLabelCandidate> candidates =
                await Task.Run(() => runner.Predict(bgra, width, height, options));

            viewModel.Status = $"检测完成：{candidates.Count} 个框";

            var window = new DetectWindow(
                bitmap,
                candidates,
                Path.GetFileName(imagePath),
                Path.GetFileName(modelPath));

            await window.ShowDialog(this);
        }
        catch (Exception error)
        {
            viewModel.Status = $"检测失败：{error.Message}";
        }
    }

    private static void SaveSettings(SettingsStore settingsStore, AppSettings settings)
    {
        try
        {
            settingsStore.Save(settings);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 配置写不进去不影响本次检测
        }
    }

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

    private async Task<string?> PickFileAsync(
        string title,
        FilePickerFileType[] filters,
        string? startFolder = null)
    {
        IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        IStorageFolder? start = null;

        if (!string.IsNullOrWhiteSpace(startFolder) && Directory.Exists(startFolder))
        {
            start = await storage.TryGetFolderFromPathAsync(startFolder);
        }

        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = filters,
            SuggestedStartLocation = start,
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}
