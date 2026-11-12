using System.Globalization;
using System.Text.Json;
using Sevencat.Train.Engine.Models.RfDetr;
using Sevencat.Train.Engine.Models.SimpleDetr;
using Sevencat.Train.Engine.Models.Yolo;
using Sevencat.Train.Engine.Training;
using TorchSharp;
using static TorchSharp.torch;

namespace Sevencat.Train.Engine.Export;

/// <summary>导出结果：目标目录里落了哪些文件。</summary>
/// <param name="Directory">导出目录。</param>
/// <param name="SafetensorsPath">复制过去的 <c>best.safetensors</c>。</param>
/// <param name="ConfigPath">配套的 <c>model.json</c>。</param>
/// <param name="OnnxPath">导出的 <c>best.onnx</c>；没要求导出 ONNX 时为 <c>null</c>。</param>
public sealed record TrainingExportResult(
    string Directory,
    string SafetensorsPath,
    string ConfigPath,
    string? OnnxPath);

/// <summary>
/// 把一次训练的产物导成能直接用的模型文件：
/// <c>best.safetensors</c> + <c>model.json</c>（架构 / 类别 / 输入尺寸），
/// 以及可交付的 <c>best.onnx</c>。
/// <para>
/// 支持三种架构，按检查点元数据里的 <c>architecture</c> 分派：
/// <c>yolov8*</c> / <c>yolo11*</c>（YOLO）、<c>rfdetr-detect-N</c>（RF-DETR）、
/// <c>simple-detr-detect-N</c>（简化 DETR）。
/// 架构与类别都从检查点元数据、<c>train.json</c> 里取，不靠界面猜。
/// </para>
/// </summary>
public sealed class TrainingExportService
{
    /// <summary>训练产物里的最优权重文件名。</summary>
    public const string WeightsFileName = "best.safetensors";

    /// <summary>导出的配置文件名。</summary>
    public const string ConfigFileName = "model.json";

    /// <summary>导出的 ONNX 文件名。</summary>
    public const string OnnxFileName = "best.onnx";

    private const string RfDetrPrefix = "rfdetr";
    private const string SimpleDetrPrefix = "simple-detr";

    /// <summary>
    /// 导出。<paramref name="runDirectory"/> 是训练输出目录（里面有 best.safetensors / train.json），
    /// <paramref name="targetDirectory"/> 是导出目录（例如 <c>{输出目录}/export</c>）。
    /// </summary>
    public TrainingExportResult Export(string runDirectory, string targetDirectory, bool exportOnnx)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        string source = Path.Combine(runDirectory, WeightsFileName);

        if (!File.Exists(source))
        {
            throw new FileNotFoundException(
                $"{runDirectory} 里没有 {WeightsFileName}：先训练一轮，或换输出目录。", source);
        }

        Directory.CreateDirectory(targetDirectory);

        using Checkpoint checkpoint = Checkpoint.Open(source);

        string architecture = checkpoint.Metadata.GetValueOrDefault("architecture") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(architecture))
        {
            throw new InvalidDataException($"{WeightsFileName} 缺少 architecture 元数据，无法重建模型。");
        }

        Device device = CPU;
        int imageSize = ReadInt(checkpoint.Metadata, "image_size", 640);
        IReadOnlyList<string> classNames = ReadClassNames(runDirectory);

        string weights = Path.Combine(targetDirectory, WeightsFileName);
        File.Copy(source, weights, overwrite: true);

        string? onnx = null;
        string config = Path.Combine(targetDirectory, ConfigFileName);

        if (YoloArchitectures.TryParseFamily(architecture, out _))
        {
            (YoloModelFamily family, YoloScale scale, int classCount) = YoloArchitectures.Parse(architecture);

            if (exportOnnx)
            {
                onnx = Path.Combine(targetDirectory, OnnxFileName);

                using YoloDetectionModel model = YoloArchitectures.CreateFromArchitecture(device, architecture);
                checkpoint.LoadInto(model, device, strict: false);
                YoloOnnxExporter.Save(model, onnx, imageHeight: imageSize, imageWidth: imageSize);
            }

            WriteYoloConfig(
                config,
                architecture,
                family,
                scale,
                classCount,
                imageSize,
                classNames,
                checkpoint.Metadata,
                onnx);
        }
        else if (TryParseDetr(architecture, out bool useRfDetr, out int classCount))
        {
            int queries = ReadInt(checkpoint.Metadata, "queries", useRfDetr ? 300 : 50);
            int encoderLayers = ReadInt(checkpoint.Metadata, "encoder_layers", useRfDetr ? 1 : 2);
            int decoderLayers = ReadInt(checkpoint.Metadata, "decoder_layers", 2);
            int heads = ReadInt(checkpoint.Metadata, "heads", 8);
            int dinoEmbedding = ReadInt(checkpoint.Metadata, "dino_embed", 384);
            int dinoDepth = ReadInt(checkpoint.Metadata, "dino_depth", 12);
            int dinoHeads = ReadInt(checkpoint.Metadata, "dino_heads", 6);

            if (exportOnnx)
            {
                onnx = Path.Combine(targetDirectory, OnnxFileName);

                if (useRfDetr)
                {
                    // 与 DetrTrainingSession.BuildModel 的装配方式保持一致，键才对得上
                    var dinoConfig = new DinoV2Config(
                        ImageSize: 518,
                        EmbeddingSize: dinoEmbedding,
                        Depth: dinoDepth,
                        Heads: dinoHeads);

                    using var detector = new RfDetrDetector(
                        classCount,
                        queries,
                        dinoConfig,
                        heads,
                        device,
                        encoderLayers: encoderLayers,
                        decoderLayers: decoderLayers);

                    checkpoint.LoadInto(detector, device, strict: false);
                    RfDetrOnnxExporter.Save(detector, onnx, imageSize, imageSize);
                }
                else
                {
                    using var model = new SimpleDetrModel(
                        classCount,
                        queries,
                        encoderLayers,
                        decoderLayers,
                        device: device);

                    checkpoint.LoadInto(model, device, strict: false);
                    SimpleDetrOnnxExporter.Save(model, onnx, imageSize, imageSize);
                }
            }

            WriteDetrConfig(
                config,
                architecture,
                useRfDetr,
                classCount,
                imageSize,
                queries,
                encoderLayers,
                decoderLayers,
                heads,
                dinoEmbedding,
                dinoDepth,
                dinoHeads,
                classNames,
                checkpoint.Metadata,
                onnx);
        }
        else
        {
            throw new NotSupportedException(
                $"不认识的模型架构「{architecture}」：支持 YOLO / RF-DETR / Simple-DETR。");
        }

        return new TrainingExportResult(targetDirectory, weights, config, onnx);
    }

    // ── 配置 json ─────────────────────────────────────────────────────────────

    private static void WriteYoloConfig(
        string path,
        string architecture,
        YoloModelFamily family,
        YoloScale scale,
        int classCount,
        int imageSize,
        IReadOnlyList<string> classNames,
        IReadOnlyDictionary<string, string> metadata,
        string? onnxPath)
    {
        // Utf8JsonWriter 直接写：不依赖反射 / 源生成，AOT 或禁用反射的环境同样能导出
        using FileStream stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteString("format", "sevencat-model/1");
        writer.WriteString("architecture", architecture);
        writer.WriteString("family", family == YoloModelFamily.V11 ? "yolov11" : "yolov8");
        writer.WriteString("scale", YoloScales.ToLetter(scale).ToString());
        writer.WriteString("task", "detect");
        writer.WriteNumber("classCount", classCount);
        WriteClasses(writer, classNames);
        writer.WriteNumber("imageSize", imageSize);

        writer.WriteStartObject("input");
        writer.WriteString("name", "input");
        WriteImageShape(writer, imageSize);
        writer.WriteEndObject();

        writer.WriteStartObject("output");
        writer.WriteString("name", "output0");
        writer.WriteStartArray("shape");
        writer.WriteNumberValue(1);
        writer.WriteNumberValue(4 + classCount);
        writer.WriteStringValue("anchors");
        writer.WriteEndArray();
        writer.WriteEndObject();

        WriteOnnxAndBest(writer, metadata, onnxPath);
        writer.WriteEndObject();
        writer.Flush();
    }

    private static void WriteDetrConfig(
        string path,
        string architecture,
        bool useRfDetr,
        int classCount,
        int imageSize,
        int queries,
        int encoderLayers,
        int decoderLayers,
        int heads,
        int dinoEmbedding,
        int dinoDepth,
        int dinoHeads,
        IReadOnlyList<string> classNames,
        IReadOnlyDictionary<string, string> metadata,
        string? onnxPath)
    {
        using FileStream stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteString("format", "sevencat-model/1");
        writer.WriteString("architecture", architecture);
        writer.WriteString("family", useRfDetr ? "rf-detr" : "simple-detr");
        writer.WriteString("task", "detect");
        writer.WriteNumber("classCount", classCount);
        WriteClasses(writer, classNames);
        writer.WriteNumber("imageSize", imageSize);
        writer.WriteNumber("queries", queries);
        writer.WriteNumber("encoderLayers", encoderLayers);
        writer.WriteNumber("decoderLayers", decoderLayers);
        writer.WriteNumber("heads", heads);

        if (useRfDetr)
        {
            writer.WriteStartObject("dino");

            writer.WriteNumber("embedding", dinoEmbedding);
            writer.WriteNumber("depth", dinoDepth);
            writer.WriteNumber("heads", dinoHeads);

            writer.WriteEndObject();
        }

        writer.WriteStartObject("input");
        writer.WriteString("name", useRfDetr ? RfDetrOnnxExporter.InputName : "input");
        WriteImageShape(writer, imageSize);
        writer.WriteEndObject();

        // RF-DETR / Simple-DETR 的 ONNX 都是 boxes（sigmoid 归一化 cxcywh）+ logits
        writer.WriteStartArray("outputs");

        writer.WriteStringValue(RfDetrOnnxExporter.BoxesOutputName);
        writer.WriteStringValue(RfDetrOnnxExporter.LogitsOutputName);

        writer.WriteEndArray();

        WriteOnnxAndBest(writer, metadata, onnxPath);
        writer.WriteEndObject();
        writer.Flush();
    }

    private static void WriteClasses(Utf8JsonWriter writer, IReadOnlyList<string> classNames)
    {
        writer.WriteStartArray("classes");

        foreach (string name in classNames)
        {
            writer.WriteStringValue(name);
        }

        writer.WriteEndArray();
    }

    private static void WriteImageShape(Utf8JsonWriter writer, int imageSize)
    {
        writer.WriteStartArray("shape");

        writer.WriteNumberValue(1);
        writer.WriteNumberValue(3);
        writer.WriteNumberValue(imageSize);
        writer.WriteNumberValue(imageSize);

        writer.WriteEndArray();
    }

    private static void WriteOnnxAndBest(
        Utf8JsonWriter writer,
        IReadOnlyDictionary<string, string> metadata,
        string? onnxPath)
    {
        if (onnxPath is null)
        {
            writer.WriteNull("onnx");
        }
        else
        {
            writer.WriteString("onnx", OnnxFileName);
        }

        writer.WriteStartObject("best");
        WriteMaybeString(writer, "epoch", metadata.GetValueOrDefault("epoch"));
        WriteMaybeString(writer, "loss", metadata.GetValueOrDefault("loss"));
        WriteMaybeString(writer, "map50", metadata.GetValueOrDefault("map50"));
        WriteMaybeString(writer, "map50_95", metadata.GetValueOrDefault("map50-95"));
        writer.WriteEndObject();

        writer.WriteString("exportedAt", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
    }

    // ── 辅助 ──────────────────────────────────────────────────────────────────

    private static void WriteMaybeString(Utf8JsonWriter writer, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> metadata, string key, int fallback)
        => int.TryParse(
            metadata.GetValueOrDefault(key),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int value) && value > 0
            ? value
            : fallback;

    /// <summary>识别 <c>rfdetr-detect-N</c> / <c>simple-detr-detect-N</c>。</summary>
    private static bool TryParseDetr(string architecture, out bool useRfDetr, out int classCount)
    {
        useRfDetr = false;
        classCount = 0;

        string text = architecture.Trim();

        if (text.StartsWith(RfDetrPrefix, StringComparison.OrdinalIgnoreCase))
        {
            useRfDetr = true;
        }
        else if (!text.StartsWith(SimpleDetrPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int separator = text.LastIndexOf('-');

        if (separator < 0
            || !int.TryParse(
                text[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out classCount)
            || classCount <= 0)
        {
            classCount = 0;
            return false;
        }

        return true;
    }

    /// <summary>train.json 里的类别名（没有就空数组，配置里仍带 classCount）。</summary>
    private static IReadOnlyList<string> ReadClassNames(string runDirectory)
    {
        string path = Path.Combine(runDirectory, "train.json");

        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

            if (document.RootElement.TryGetProperty("classes", out JsonElement classes)
                && classes.ValueKind == JsonValueKind.Array)
            {
                return [.. classes.EnumerateArray().Select(item => item.GetString() ?? string.Empty)];
            }
        }
        catch (JsonException)
        {
            // train.json 坏了不影响导出，类别名留空即可
        }

        return [];
    }
}
