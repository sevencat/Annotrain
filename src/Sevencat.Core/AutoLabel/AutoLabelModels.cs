using Sevencat.Core.Model;

namespace Sevencat.Core.AutoLabel;

/// <summary>AI 标注参数：一个 ONNX 检测模型 + 置信度 + 类别映射。</summary>
/// <param name="ModelPath">YOLO 导出的 <c>.onnx</c>。</param>
/// <param name="Confidence">低于这个分数的候选直接丢。</param>
/// <param name="IouThreshold">NMS 的 IoU 阈值。</param>
/// <param name="ClassNames">模型自带的类别名（顺序即 class id）；为空时用 <c>class_{id}</c>。</param>
/// <param name="ClassMap">模型 class id → 项目类别名；命中时用项目类别，否则新建同名标签。</param>
public sealed record AutoLabelOptions(
    string ModelPath,
    double Confidence = 0.25,
    double IouThreshold = 0.45,
    IReadOnlyList<string>? ClassNames = null,
    IReadOnlyDictionary<int, string>? ClassMap = null);

/// <summary>AI 给出的一条候选框（原图像素坐标）。</summary>
public sealed record AutoLabelCandidate(RectD Box, string Label, int ClassId, double Score)
{
    public string ScoreText => Score.ToString("P0");
}
