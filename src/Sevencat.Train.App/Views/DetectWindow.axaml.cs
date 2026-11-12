using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Sevencat.Core.AutoLabel;
using Sevencat.Train.App.ViewModels;

namespace Sevencat.Train.App.Views;

/// <summary>检测结果：图片 + 叠加的框 + 右侧框列表。纯展示，不做编辑。</summary>
public partial class DetectWindow : Window
{
    public DetectWindow() => InitializeComponent();

    public DetectWindow(
        Bitmap image,
        IReadOnlyList<AutoLabelCandidate> candidates,
        string imageName,
        string modelName) : this()
    {
        double width = Math.Max(1, image.PixelSize.Width);
        double height = Math.Max(1, image.PixelSize.Height);

        Board.Width = width;
        Board.Height = height;
        Preview.Width = width;
        Preview.Height = height;
        Preview.Source = image;

        var boxes = candidates
            .Select(candidate => new DetectedBox(
                candidate.Box.X,
                candidate.Box.Y,
                candidate.Box.Width,
                candidate.Box.Height,
                candidate.Label,
                candidate.ScoreText))
            .ToList();

        Boxes.ItemsSource = boxes;
        BoxList.ItemsSource = boxes;

        HeaderText.Text = $"{imageName} · {modelName} · {boxes.Count} 个框";

        HintText.Text = boxes.Count == 0
            ? "没有检测到目标（调低置信度再试，或确认模型类别）"
            : $"置信度 {candidates.Min(item => item.Score):P0} ~ {candidates.Max(item => item.Score):P0}";
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
