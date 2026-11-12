using Sevencat.Train.Engine.Data.Yolo;

namespace Sevencat.Train.Engine.Data.Coco;

/// <summary>
/// COCO → 训练样本的桥接：把 COCO json（RF-DETR / Roboflow 或 <c>annotations/*.json</c>）
/// 直接转成 <see cref="YoloDataset"/>，训练页 / CLI 不必先落地成 YOLO 文本标签。
/// <para>
/// 两条约定：
/// <list type="bullet">
/// <item>类别索引按 <c>categories[].id</c> 升序重排为 0..n-1（COCO 的 id 通常从 1 开始，不能直接当类别下标）；</item>
/// <item>bbox 是绝对像素 <c>[x, y, w, h]</c>，这里换算成归一化的 <c>cx cy w h</c>，并按 <c>images[].width/height</c> 归一化。</item>
/// </list>
/// 图片目录按 json 位置自动推断（见 <see cref="Load(string, string)"/>）。
/// </para>
/// </summary>
public static class CocoDetectionLoader
{
    /// <summary>
    /// 读取 COCO 标注并转成训练数据集。
    /// </summary>
    /// <param name="jsonPath">COCO json 路径。</param>
    /// <param name="split">split 名（只用于日志与数据集标签，如 train / val）。</param>
    /// <param name="imageRoot">图片目录；为空时按 json 位置自动推断。</param>
    public static YoloDataset Load(string jsonPath, string split = "train", string? imageRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);
        if (!File.Exists(jsonPath))
            throw new FileNotFoundException($"COCO 标注文件不存在：{jsonPath}", jsonPath);

        CocoDataset coco = CocoDataset.Load(jsonPath);
        string root = ResolveImageRoot(jsonPath, coco, split, imageRoot);

        // 类别按 id 升序重排：COCO 的 category_id 通常是 1..n，训练需要 0..n-1。
        var orderedCategories = coco.Categories.OrderBy(category => category.Id).ToList();
        var classIndex = new Dictionary<long, int>(orderedCategories.Count);

        for (int index = 0; index < orderedCategories.Count; index++)
            classIndex[orderedCategories[index].Id] = index;

        if (classIndex.Count == 0)
            throw new InvalidDataException($"COCO 标注里没有 categories：{jsonPath}");

        var byImage = coco.Annotations
            .Where(annotation => !annotation.IsCrowd && classIndex.ContainsKey(annotation.CategoryId))
            .GroupBy(annotation => annotation.ImageId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var samples = new List<YoloSample>(coco.Images.Count);
        var missing = new List<string>();

        foreach (CocoImage image in coco.Images.OrderBy(image => image.FileName, StringComparer.Ordinal))
        {
            string imagePath = Path.Combine(root, image.FileName);

            if (!File.Exists(imagePath))
            {
                missing.Add(image.FileName);
                continue;
            }

            var labels = new List<YoloLabel>();

            if (byImage.TryGetValue(image.Id, out List<CocoAnnotation>? annotations))
            {
                double width = Math.Max(1, image.Width);
                double height = Math.Max(1, image.Height);

                foreach (CocoAnnotation annotation in annotations)
                {
                    double centerX = (annotation.X + (annotation.Width / 2.0)) / width;
                    double centerY = (annotation.Y + (annotation.Height / 2.0)) / height;
                    double boxWidth = annotation.Width / width;
                    double boxHeight = annotation.Height / height;

                    labels.Add(new YoloLabel(
                        classIndex[annotation.CategoryId],
                        YoloTask.Detect,
                        new[]
                        {
                            Clamp01(centerX), Clamp01(centerY), Clamp01(boxWidth), Clamp01(boxHeight),
                        }));
                }
            }

            samples.Add(new YoloSample(imagePath, string.Empty, labels));
        }

        if (missing.Count > 0)
        {
            throw new InvalidDataException(
                $"COCO 标注里有 {missing.Count} 张图片在 {root} 下找不到，例如：{string.Join(", ", missing.Take(3))}"
                + "（可用 imageRoot 参数显式指定图片目录）。");
        }

        if (samples.Count == 0)
            throw new InvalidDataException($"COCO 标注里没有可用图片：{jsonPath}");

        return YoloDataset.CreateInMemory(
            split,
            samples,
            orderedCategories.Select(category => category.Name).ToArray(),
            root,
            YoloTask.Detect,
            jsonPath);
    }

    /// <summary>
    /// 推断图片目录，依次尝试：
    /// <list type="number">
    /// <item>json 同目录（RF-DETR / Roboflow：<c>train/_annotations.coco.json</c> 与图片同级）；</item>
    /// <item><c>&lt;json 目录&gt;/../images/&lt;split&gt;</c>（COCO 单文件：<c>annotations/train.json</c>）；</item>
    /// <item><c>&lt;json 目录&gt;/images/&lt;split&gt;</c>；</item>
    /// <item><c>&lt;json 目录&gt;/&lt;split&gt;</c>。</item>
    /// </list>
    /// 目前没有可用的图片目录时返回第一个候选，让调用方在缺图时报出具体路径。
    /// </summary>
    public static string ResolveImageRoot(string jsonPath, CocoDataset coco, string split, string? explicitRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            string full = Path.GetFullPath(explicitRoot);
            if (!Directory.Exists(full))
                throw new DirectoryNotFoundException($"图片目录不存在：{full}");
            return full;
        }

        string jsonDirectory = Path.GetDirectoryName(Path.GetFullPath(jsonPath))!;
        var candidates = new[]
        {
            jsonDirectory,
            Path.GetFullPath(Path.Combine(jsonDirectory, "..", "images", split)),
            Path.Combine(jsonDirectory, "images", split),
            Path.Combine(jsonDirectory, split),
        };

        string? firstImage = coco.Images.FirstOrDefault()?.FileName;

        if (firstImage is not null)
        {
            foreach (string candidate in candidates)
            {
                if (File.Exists(Path.Combine(candidate, firstImage)))
                    return candidate;
            }
        }

        return candidates[0];
    }

    private static float Clamp01(double value) => (float)Math.Clamp(value, 0.0, 1.0);
}
