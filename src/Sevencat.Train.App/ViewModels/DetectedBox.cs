namespace Sevencat.Train.App.ViewModels;

/// <summary>检测结果里叠在图上的一行（原图像素坐标）。</summary>
public sealed record DetectedBox(
    double X,
    double Y,
    double Width,
    double Height,
    string Label,
    string ScoreText);
