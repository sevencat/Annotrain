using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sevencat.Core.Dataset;
using Sevencat.Core.Model;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Simulation;

/// <summary>训练参数。</summary>
/// <param name="Output">训练产物目录。</param>
/// <param name="Splits">要训练的 split；为空时用数据集自带的。</param>
/// <param name="Epochs">训练轮数。</param>
/// <param name="BatchSize">批大小。</param>
/// <param name="ImageSize">网络输入分辨率（32 的倍数）。</param>
/// <param name="LearningRate">初始学习率。</param>
/// <param name="Scale">模型规模（n / s / m / l / x）。</param>
/// <param name="Family">模型家族（v8 / v11）。</param>
/// <param name="Device">计算设备（cpu / cuda）。</param>
public sealed record TrainingRunOptions(
    StorageLocation Output,
    IReadOnlyList<string>? Splits = null,
    int Epochs = 10,
    int BatchSize = 8,
    int ImageSize = 640,
    float LearningRate = 0.01f,
    string Scale = "n",
    string Family = "v8",
    string Device = "cpu")
{
    /// <summary>每轮进度回调（真训练引擎用；干跑忽略）。</summary>
    public IProgress<TrainingProgress>? Progress { get; init; }

    /// <summary>日志回调：设备、模型、每轮摘要。</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// 一轮训练结束时的指标快照。只带标量：界面线程永远不会碰到 TorchSharp 张量。
/// </summary>
/// <param name="Epoch">已完成的 epoch 编号（从 0 起）。</param>
/// <param name="TotalEpochs">目标总轮数。</param>
/// <param name="Step">截至当前累计的优化步数。</param>
/// <param name="Loss">该 epoch 的平均损失。</param>
/// <param name="LearningRate">该 epoch 使用的学习率。</param>
/// <param name="Map50">验证集 mAP50（未评估为 0）。</param>
/// <param name="Map50_95">验证集 mAP50-95（未评估为 0）。</param>
public sealed record TrainingProgress(
    int Epoch,
    int TotalEpochs,
    int Step,
    float Loss,
    float LearningRate,
    float Map50,
    float Map50_95)
{
    /// <summary>完成比例（0~1，用于进度条）。</summary>
    public double Fraction => TotalEpochs <= 0 ? 0 : Math.Clamp((Epoch + 1) / (double)TotalEpochs, 0, 1);
}

/// <summary>一次训练的摘要。</summary>
public sealed record TrainingRunSummary(
    string DatasetId,
    int Images,
    int Boxes,
    IReadOnlyList<string> Classes,
    IReadOnlyList<string> Splits,
    StorageLocation Output,
    bool DryRun,
    IReadOnlyList<string> Warnings)
{
    /// <summary>最佳 mAP50（干跑为 0）。</summary>
    public float BestMap50 { get; init; }

    /// <summary>最佳 mAP50-95（干跑为 0）。</summary>
    public float BestMap50_95 { get; init; }

    /// <summary>模型结构标识（<c>yolov8n-detect-3</c> 之类；干跑为空）。</summary>
    public string? Architecture { get; init; }

    public string Summary => DryRun
        ? $"干跑 {DatasetId}：{Images} 张图 / {Boxes} 个框 / {Classes.Count} 类 / split {string.Join(",", Splits)}"
        : $"训练 {DatasetId}：{Images} 张图 / {Boxes} 个框 / {Classes.Count} 类 / {Architecture}"
          + $" / mAP50 {BestMap50:F3} / mAP50-95 {BestMap50_95:F3}";
}

/// <summary>训练入口抽象：模拟流程与将来的真训练引擎都实现它。</summary>
public interface IDatasetTrainingRunner
{
    Task<TrainingRunSummary> TrainAsync(IDatasetStore store, TrainingRunOptions options, CancellationToken ct = default);
}

/// <summary>
/// 当前实现：把数据集完整装载一遍（等价于训练前的 data loader），校验框/类别，把输入摘要写到输出目录。
/// 真实训练（YOLO / RF-DETR）在 M4 接入时替换本实现即可，调用方不用改。
/// </summary>
public sealed class SummaryTrainingRunner(
    BlobStoreRegistry registry,
    ILogger<SummaryTrainingRunner>? logger = null) : IDatasetTrainingRunner
{
    public async Task<TrainingRunSummary> TrainAsync(
        IDatasetStore store,
        TrainingRunOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        string[] splits = options.Splits is { Count: > 0 } ? [.. options.Splits] : [.. store.Splits];
        if (splits.Length == 0)
        {
            throw new InvalidOperationException("没有可训练的 split：去标注端给图片分配 train/val");
        }

        var warnings = new List<string>();
        var rows = new List<(string Split, string Image, int Boxes)>();
        int images = 0;
        int boxes = 0;

        foreach (string split in splits)
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<DatasetSample> samples = await store
                .OpenSplit(split)
                .LoadSamplesAsync(ct)
                .ConfigureAwait(false);

            foreach (DatasetSample sample in samples)
            {
                images += 1;
                boxes += sample.Boxes.Count;
                rows.Add((split, sample.Image.RelativePath, sample.Boxes.Count));

                if (sample.Width <= 0 || sample.Height <= 0)
                {
                    warnings.Add($"图片尺寸无效：{sample.Image.RelativePath}");
                }

                foreach (BoxAnnotation box in sample.Boxes)
                {
                    if (box.ClassId < 0 || box.ClassId >= store.Classes.Count)
                    {
                        warnings.Add($"类别越界：{sample.Image.RelativePath} classId={box.ClassId}");
                    }

                    RectD rect = box.ToRect();
                    if (rect.IsEmpty || rect.X < 0 || rect.Y < 0 || rect.Right > sample.Width || rect.Bottom > sample.Height)
                    {
                        warnings.Add($"框越界：{sample.Image.RelativePath} [{rect.X:0.#},{rect.Y:0.#},{rect.Right:0.#},{rect.Bottom:0.#}]");
                    }
                }
            }
        }

        IBlobStore output = registry.Get(options.Output);
        await WriteJsonAsync(output, options.Output, splits, store, rows, warnings, ct).ConfigureAwait(false);

        var builder = new StringBuilder();
        for (int index = 0; index < store.Classes.Count; index++)
        {
            builder.Append(store.Classes.NameOf(index)).Append('\n');
        }

        using (var classes = new MemoryStream(Encoding.UTF8.GetBytes(builder.ToString())))
        {
            await output
                .WriteAsync(new StorageKey(options.Output, "classes.txt"), classes, ct)
                .ConfigureAwait(false);
        }

        logger?.LogInformation(
            "训练输入就绪（干跑）：{Dataset} {Images} 张图 / {Boxes} 个框，split {Splits}",
            store.Id,
            images,
            boxes,
            string.Join(",", splits));

        return new TrainingRunSummary(
            store.Id,
            images,
            boxes,
            [.. store.Classes.Names],
            splits,
            options.Output,
            DryRun: true,
            [.. warnings]);
    }

    private static async Task WriteJsonAsync(
        IBlobStore output,
        StorageLocation location,
        IReadOnlyList<string> splits,
        IDatasetStore store,
        IReadOnlyList<(string Split, string Image, int Boxes)> rows,
        IReadOnlyList<string> warnings,
        CancellationToken ct)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("dataset", store.Id);
            writer.WriteBoolean("dryRun", true);

            writer.WriteStartArray("splits");
            foreach (string split in splits)
            {
                writer.WriteStringValue(split);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("classes");
            for (int index = 0; index < store.Classes.Count; index++)
            {
                writer.WriteStringValue(store.Classes.NameOf(index));
            }

            writer.WriteEndArray();

            writer.WriteStartArray("images");
            foreach ((string split, string image, int boxes) in rows)
            {
                writer.WriteStartObject();
                writer.WriteString("split", split);
                writer.WriteString("image", image);
                writer.WriteNumber("boxes", boxes);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("warnings");
            foreach (string warning in warnings)
            {
                writer.WriteStringValue(warning);
            }

            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        buffer.Position = 0;

        await output
            .WriteAsync(new StorageKey(location, "dataset-summary.json"), buffer, ct)
            .ConfigureAwait(false);
    }
}
