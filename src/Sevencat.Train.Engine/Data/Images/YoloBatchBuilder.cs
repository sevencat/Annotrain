namespace Sevencat.Train.Engine.Data.Images;

/// <summary>
/// 一个检测批次的托管数组形式：输入 <c>[B,3,H,W]</c>（CHW，<c>[0,1]</c>）+
/// 目标 <c>[B,M,6]</c>（每行 <c>valid, class, x1, y1, x2, y2</c>，坐标是网络输入像素）。
/// <para>
/// 目标用 <c>valid</c> 列区分「空位」，避免 class 0 与填充混淆；
/// 训练侧把这几个数组交给 <see cref="Sevencat.Train.Engine.Core.TorchInterop"/> 转成张量，
/// 因此本层继续保持对 TorchSharp 零依赖。
/// </para>
/// </summary>
public sealed record YoloBatchResult(
    float[] Inputs,
    float[] Targets,
    int BatchSize,
    int Channels,
    int Height,
    int Width,
    int MaxBoxes,
    IReadOnlyList<string> ImagePaths);

/// <summary>把预处理后的样本打包成训练批次。</summary>
public static class YoloBatchBuilder
{
    /// <summary>目标张量每行的列数：<c>valid, class, x1, y1, x2, y2</c>。</summary>
    public const int TargetColumns = 6;

    /// <summary>按样本列表打包；同一批次内尺寸必须一致（mosaic / letterbox 已统一过）。</summary>
    public static YoloBatchResult Build(IReadOnlyList<YoloImageSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
            throw new ArgumentException("批次不能为空。", nameof(samples));

        int height = samples[0].ModelHeight;
        int width = samples[0].ModelWidth;
        int maxBoxes = 1;

        foreach (YoloImageSample sample in samples)
        {
            if (sample.ModelHeight != height || sample.ModelWidth != width)
            {
                throw new ArgumentException(
                    $"批次内样本尺寸不一致：{sample.ModelWidth}x{sample.ModelHeight} 与 {width}x{height}。"
                    + "预处理阶段应统一 letterbox 尺寸。",
                    nameof(samples));
            }

            maxBoxes = Math.Max(maxBoxes, sample.Boxes.Count);
        }

        int plane = height * width;
        var inputs = new float[samples.Count * 3 * plane];
        var targets = new float[samples.Count * maxBoxes * TargetColumns];
        var paths = new string[samples.Count];

        for (int index = 0; index < samples.Count; index++)
        {
            YoloImageSample sample = samples[index];
            paths[index] = sample.ImagePath;

            float[] chw = sample.ToChw();
            Array.Copy(chw, 0, inputs, index * chw.Length, chw.Length);

            IReadOnlyList<YoloTargetBox> boxes = sample.Boxes;
            if (boxes.Count > maxBoxes)
            {
                throw new InvalidOperationException(
                    $"样本 {sample.ImagePath} 有 {boxes.Count} 个框，超过批次上限 {maxBoxes}。");
            }

            int rowBase = index * maxBoxes * TargetColumns;
            for (int boxIndex = 0; boxIndex < boxes.Count; boxIndex++)
            {
                YoloTargetBox box = boxes[boxIndex];
                int offset = rowBase + boxIndex * TargetColumns;

                targets[offset] = 1f;
                targets[offset + 1] = box.ClassId;
                targets[offset + 2] = box.X1;
                targets[offset + 3] = box.Y1;
                targets[offset + 4] = box.X2;
                targets[offset + 5] = box.Y2;
            }
        }

        return new YoloBatchResult(
            inputs,
            targets,
            samples.Count,
            3,
            height,
            width,
            maxBoxes,
            paths);
    }
}
