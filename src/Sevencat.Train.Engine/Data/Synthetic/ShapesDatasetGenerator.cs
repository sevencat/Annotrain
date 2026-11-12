using System.Globalization;
using Sevencat.Train.Engine.Core;
using Sevencat.Train.Engine.Data.Images;

namespace Sevencat.Train.Engine.Data.Synthetic;

/// <summary>
/// 生成「几何形状」测试数据集：随机背景 + 1~N 个互不重叠的圆 / 正方形 / 长方形。
/// <para>
/// 支持三种落盘格式（见 <see cref="SyntheticDatasetFormat"/>）：
/// <list type="bullet">
/// <item><b>YOLO</b>（默认，本仓库一直用的布局）：<c>data.yaml</c> + <c>images/</c> + <c>labels/</c>；</item>
/// <item><b>RF-DETR / Roboflow</b>：<c>train/_annotations.coco.json</c> + <c>valid/_annotations.coco.json</c>；</item>
/// <item><b>COCO 单文件</b>：<c>annotations/{train,val}.json</c> + <c>images/{train,val}</c>。</item>
/// </list>
/// 类别固定为 0=circle、1=square、2=rectangle（COCO 里 category_id 是 1/2/3）。
/// </para>
/// </summary>
public static partial class ShapesDatasetGenerator
{
    /// <summary>固定类别名（与脚本一致）：0=circle、1=square、2=rectangle、3=triangle。</summary>
    public static readonly IReadOnlyList<string> ClassNames = new[] { "circle", "square", "rectangle", "triangle" };

    /// <summary>预览缩略图最多收录几张。</summary>
    private const int PreviewTiles = 16;

    /// <summary>生成数据集；<paramref name="progress"/> 收到「已生成 / 总数」。</summary>
    public static ShapesDatasetSummary Generate(
        ShapesDatasetOptions options,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        string root = Path.GetFullPath(options.OutputDirectory);
        var rng = new Rng(options.Seed);
        var objectsPerClass = new int[ClassNames.Count];
        var previewTiles = new List<byte[]>(PreviewTiles);

        int total = options.TrainImages + options.ValImages;
        int done = 0;
        string descriptor;

        switch (options.Format)
        {
            case SyntheticDatasetFormat.RfDetr:
                descriptor = WriteCocoSplit(root, "train", options.TrainImages)
                    ?? Path.Combine(root, "train", "_annotations.coco.json");
                WriteCocoSplit(root, "valid", options.ValImages);
                break;

            case SyntheticDatasetFormat.Coco:
            {
                string? trainJson = WriteCocoSplit(root, "train", options.TrainImages);
                WriteCocoSplit(root, "val", options.ValImages);
                descriptor = trainJson ?? Path.Combine(root, "annotations", "train.json");
                break;
            }

            default:
                WriteYoloSplit("train", options.TrainImages);
                WriteYoloSplit("val", options.ValImages);
                descriptor = Path.Combine(root, "data.yaml");
                File.WriteAllText(descriptor, BuildDataYaml(), new System.Text.UTF8Encoding(false));
                break;
        }

        string? preview = options.WritePreview && previewTiles.Count > 0
            ? WritePreview(previewTiles, Path.Combine(root, "preview.png"), options.ImageSize)
            : null;

        watch.Stop();

        return new ShapesDatasetSummary(
            descriptor,
            options.Format,
            options.TrainImages,
            options.ValImages,
            options.ImageSize,
            objectsPerClass,
            preview,
            watch.Elapsed);

        void WriteYoloSplit(string split, int count)
        {
            if (count <= 0)
                return;

            string images = Path.Combine(root, "images", split);
            string labels = Path.Combine(root, "labels", split);
            Directory.CreateDirectory(images);
            Directory.CreateDirectory(labels);

            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string name = string.Create(CultureInfo.InvariantCulture, $"{split}-{index:0000}");
                byte[] pixels = GenerateImage(rng, options, objectsPerClass, out List<string> lines);

                ImageCodecs.SaveRgb24(pixels, options.ImageSize, options.ImageSize, Path.Combine(images, name + ".png"));
                File.WriteAllLines(Path.Combine(labels, name + ".txt"), lines, new System.Text.UTF8Encoding(false));

                RememberPreview(pixels);
                done++;
                progress?.Report((done, total));
            }
        }

        string? WriteCocoSplit(string rootDirectory, string split, int count)
        {
            if (count <= 0)
                return null;

            // RF-DETR 把图片与 json 放在同一个 split 目录；COCO 单文件布局把图片放 images/<split>。
            bool roboflow = options.Format == SyntheticDatasetFormat.RfDetr;
            string images = roboflow
                ? Path.Combine(rootDirectory, split)
                : Path.Combine(rootDirectory, "images", split);

            Directory.CreateDirectory(images);

            var entries = new List<CocoImageEntry>(count);

            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string name = string.Create(CultureInfo.InvariantCulture, $"{split}-{index:0000}");
                byte[] pixels = GenerateImage(rng, options, objectsPerClass, out List<SyntheticShape> shapes);

                ImageCodecs.SaveRgb24(pixels, options.ImageSize, options.ImageSize, Path.Combine(images, name + ".png"));

                entries.Add(new CocoImageEntry(index + 1, name + ".png", options.ImageSize, options.ImageSize, shapes));

                RememberPreview(pixels);
                done++;
                progress?.Report((done, total));
            }

            string jsonPath = roboflow
                ? Path.Combine(images, RoboflowAnnotationFileName)
                : Path.Combine(rootDirectory, "annotations", split + ".json");

            WriteCocoJson(jsonPath, entries);
            return jsonPath;
        }

        void RememberPreview(byte[] pixels)
        {
            if (previewTiles.Count < PreviewTiles)
                previewTiles.Add(pixels);
        }
    }
}
