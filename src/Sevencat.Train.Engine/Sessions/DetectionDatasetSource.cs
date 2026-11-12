using Sevencat.Train.Engine.Data.Coco;
using Sevencat.Train.Engine.Data.Yolo;

namespace Sevencat.Train.Engine.Sessions;

/// <summary>
/// 训练数据入口：YOLO（<c>data.yaml</c>）与 COCO（<c>*.json</c>）两条路径统一成一个 <see cref="YoloDataset"/>。
/// <para>
/// COCO 通过 <see cref="CocoDetectionLoader"/> 在内存里转换（类别 id 重排为 0..n-1、bbox 归一化），
/// 训练循环拿到的仍然是「图片路径 + 归一化标签」，不需要先落成 YOLO 文本。
/// </para>
/// </summary>
internal static class DetectionDatasetSource
{
    /// <summary>打开训练集（必须有）与验证集（可选）。</summary>
    /// <param name="dataYaml">YOLO 的 data.yaml；为空时改用 <paramref name="cocoJson"/>。</param>
    /// <param name="cocoJson">COCO 标注 json；优先级高于 dataYaml。</param>
    public static (YoloDataset Train, YoloDataset? Validation) Open(string? dataYaml, string? cocoJson)
    {
        if (!string.IsNullOrWhiteSpace(cocoJson))
        {
            YoloDataset train = CocoDetectionLoader.Load(cocoJson, "train");
            return (train, TryOpenCocoValidation(cocoJson));
        }

        if (string.IsNullOrWhiteSpace(dataYaml))
            throw new ArgumentException("训练需要 data.yaml 或 COCO 标注 json。", nameof(dataYaml));

        YoloDataset trainSet = YoloDataset.Open(dataYaml, "train");
        return (trainSet, TryOpenYoloValidation(dataYaml));
    }

    /// <summary>COCO 的验证集：按常见布局探测同级的 val / valid json。</summary>
    private static YoloDataset? TryOpenCocoValidation(string trainJsonPath)
    {
        foreach (string candidate in EnumerateCocoValidationCandidates(trainJsonPath))
        {
            if (!File.Exists(candidate))
                continue;

            try
            {
                return CocoDetectionLoader.Load(candidate, "val");
            }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
            {
                // 有 val json 但图片缺失等情况：当作没有验证集，不阻塞训练。
                continue;
            }
        }

        return null;
    }

    /// <summary>列出可能的验证集 json（Roboflow 的 valid/ 与 COCO 单文件的 val.json 都覆盖）。</summary>
    internal static IEnumerable<string> EnumerateCocoValidationCandidates(string trainJsonPath)
    {
        string full = Path.GetFullPath(trainJsonPath);
        string directory = Path.GetDirectoryName(full)!;
        string baseName = Path.GetFileNameWithoutExtension(full);

        // annotations/train.json → annotations/val.json、annotations/valid.json
        foreach (string split in new[] { "val", "valid" })
        {
            if (baseName.Contains("train", StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.Combine(
                    directory,
                    baseName.Replace("train", split, StringComparison.OrdinalIgnoreCase) + ".json");
            }
        }

        // train/_annotations.coco.json → ../valid/_annotations.coco.json
        string? parent = Path.GetDirectoryName(directory);

        if (parent is not null)
        {
            foreach (string split in new[] { "valid", "val" })
                yield return Path.Combine(parent, split, Path.GetFileName(full));
        }
    }

    private static YoloDataset? TryOpenYoloValidation(string dataYamlPath)
    {
        try
        {
            YoloDatasetConfig config = YoloDatasetConfig.Load(dataYamlPath);

            if (string.IsNullOrWhiteSpace(config.Val))
                return null;

            return YoloDataset.Open(dataYamlPath, "val");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }
}
