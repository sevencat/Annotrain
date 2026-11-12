using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sevencat.Train.App.Views;

/// <summary>「训练参数」对话框的结果；取消时对话框返回 <c>null</c>。</summary>
public sealed record TrainParams(int Epochs, int BatchSize, int ImageSize);

/// <summary>
/// 训练参数对话框：epochs / batch / imgsz 都在这里改，每个都带一句含义说明。
/// 主界面只读展示这三个值，避免直接手滑把输入框改坏。
/// </summary>
public partial class TrainingParamsDialog : Window
{
    public TrainingParamsDialog() => InitializeComponent();

    public TrainingParamsDialog(decimal epochs, decimal batchSize, decimal imageSize) : this()
    {
        EpochsBox.Value = epochs;
        BatchBox.Value = batchSize;
        ImageSizeBox.Value = imageSize;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(new TrainParams(
        (int)(EpochsBox.Value ?? 10m),
        (int)(BatchBox.Value ?? 4m),
        (int)(ImageSizeBox.Value ?? 640m)));

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
