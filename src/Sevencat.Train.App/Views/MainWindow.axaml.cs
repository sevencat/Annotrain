using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Sevencat.Core.AutoLabel;
using Sevencat.Core.Infrastructure;
using Sevencat.Train.App.Controls;
using Sevencat.Train.App.ViewModels;

namespace Sevencat.Train.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private async void OnOpenDataset(object? sender, RoutedEventArgs e)
    {
        string? folder = await PickFolderAsync("选择数据集目录（YOLO / COCO / 数据库）");

        if (folder is not null && DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.OpenDatasetAsync(folder);
            Chart.SetHint(viewModel.ChartHint);
        }
    }

    private async void OnPickOutput(object? sender, RoutedEventArgs e)
    {
        string? folder = await PickFolderAsync("选择训练产物目录");

        if (folder is not null && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.OutputDirectory = folder;
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

    private async Task<string?> PickFileAsync(string title, FilePickerFileType[] filters)
    {
        IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = filters,
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}
