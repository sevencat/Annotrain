using System.Globalization;
using TorchSharp;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.Yolo;

/// <summary>
/// YOLOv8 检测模型工厂：按 ultralytics <c>yolov8.yaml</c> 的 23 层结构组装主干、PAN 颈部与检测头，
/// 并按 <see cref="YoloScale"/> 做 depth / width 缩放。
/// <para>
/// 结构与 ultralytics 一一对应，因此 yolov8n（nc=80）的参数量应当正好是 3,157,200。
/// 这个数字被测试固定下来，任何层数 / 通道 / 卷积核写错都会立刻暴露。
/// </para>
/// </summary>
public static class YoloV8
{
    /// <summary>输入尺寸必须是该值的倍数（P3 / P4 / P5 的最大下采样倍数）。</summary>
    public const int InputStride = YoloDetectionModel.InputStride;

    private const string ArchitecturePrefix = YoloScales.V8Prefix;

    /// <summary>创建 YOLOv8 检测模型。</summary>
    public static YoloV8DetectionModel Create(
        Device device,
        YoloScale scale = YoloScale.N,
        int classCount = 80)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (classCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(classCount), classCount, "类别数必须大于 0。");

        YoloScaleConfig config = YoloScales.Config(YoloModelFamily.V8, scale);

        var from = new List<int[]>();
        var modules = new List<Module>();
        var channels = new List<int>();

        int Ch(int index) => channels[index];

        void Add(int[] source, Module module, int outputChannels)
        {
            from.Add(source);
            modules.Add(module);
            channels.Add(outputChannels);
        }

        int Width(int channelsBeforeScale) => YoloScales.ScaledChannels(channelsBeforeScale, config);
        int Repeats(int repeats) => YoloScales.ScaledRepeats(repeats, config);

        // backbone 0-9
        Add(new[] { -1 }, new ConvModule(3, Width(64), 3, 2, device), Width(64));
        Add(new[] { -1 }, new ConvModule(Ch(0), Width(128), 3, 2, device), Width(128));
        Add(new[] { -1 }, new C2fModule(Ch(1), Width(128), Repeats(3), true, 0.5, device), Width(128));
        Add(new[] { -1 }, new ConvModule(Ch(2), Width(256), 3, 2, device), Width(256));
        Add(new[] { -1 }, new C2fModule(Ch(3), Width(256), Repeats(6), true, 0.5, device), Width(256));
        Add(new[] { -1 }, new ConvModule(Ch(4), Width(512), 3, 2, device), Width(512));
        Add(new[] { -1 }, new C2fModule(Ch(5), Width(512), Repeats(6), true, 0.5, device), Width(512));
        Add(new[] { -1 }, new ConvModule(Ch(6), Width(1024), 3, 2, device), Width(1024));
        Add(new[] { -1 }, new C2fModule(Ch(7), Width(1024), Repeats(3), true, 0.5, device), Width(1024));
        Add(new[] { -1 }, new SppfModule(Ch(8), Width(1024), 5, device), Width(1024));

        // neck 10-21（上采样 + 跨层拼接）
        Add(new[] { -1 }, nn.Upsample(null, new double[] { 2.0, 2.0 }, UpsampleMode.Nearest), Ch(9));
        Add(new[] { 10, 6 }, new ConcatModule(), Ch(10) + Ch(6));
        Add(new[] { -1 }, new C2fModule(Ch(11), Width(512), Repeats(3), false, 0.5, device), Width(512));
        Add(new[] { -1 }, nn.Upsample(null, new double[] { 2.0, 2.0 }, UpsampleMode.Nearest), Ch(12));
        Add(new[] { 13, 4 }, new ConcatModule(), Ch(13) + Ch(4));
        Add(new[] { -1 }, new C2fModule(Ch(14), Width(256), Repeats(3), false, 0.5, device), Width(256));
        Add(new[] { -1 }, new ConvModule(Ch(15), Width(256), 3, 2, device), Width(256));
        Add(new[] { 16, 12 }, new ConcatModule(), Ch(16) + Ch(12));
        Add(new[] { -1 }, new C2fModule(Ch(17), Width(512), Repeats(3), false, 0.5, device), Width(512));
        Add(new[] { -1 }, new ConvModule(Ch(18), Width(512), 3, 2, device), Width(512));
        Add(new[] { 19, 9 }, new ConcatModule(), Ch(19) + Ch(9));
        Add(new[] { -1 }, new C2fModule(Ch(20), Width(1024), Repeats(3), false, 0.5, device), Width(1024));

        // head 22：P3 / P4 / P5
        var head = new DetectHeadModule(
            new[] { Ch(15), Ch(18), Ch(21) },
            classCount,
            DetectHeadModule.DefaultRegressionMax,
            device);
        Add(new[] { 15, 18, 21 }, head, head.OutputChannels);

        var chain = new YoloModuleChain("model", from, modules);
        return new YoloV8DetectionModel("DetectionModel", chain, head, scale, classCount);
    }

    /// <summary>检查点 <c>architecture</c> 元数据，例如 <c>yolov8n-detect-80</c>。</summary>
    public static string FormatArchitecture(YoloScale scale, int classCount)
    {
        if (classCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(classCount), classCount, "类别数必须大于 0。");

        return YoloScales.Describe(YoloModelFamily.V8, scale, classCount);
    }

    /// <summary>
    /// 解析 <see cref="FormatArchitecture"/> 的输出；
    /// 也接受 <c>yolov8n</c> 这种省略类别数的写法（默认 80 类）。
    /// </summary>
    public static (YoloScale Scale, int ClassCount) ParseArchitecture(string architecture)
    {
        (YoloModelFamily family, YoloScale scale, int classCount) = YoloArchitectures.Parse(architecture);

        if (family != YoloModelFamily.V8)
        {
            throw new NotSupportedException(
                $"'{architecture}' 是 {YoloScales.Prefix(family)} 结构，不是 YOLOv8。");
        }

        return (scale, classCount);
    }

    /// <summary>参数总量（含 BN 的 weight / bias；不含 running_mean 等 buffer）。</summary>
    public static long CountParameters(Module module) => YoloArchitectures.CountParameters(module);

    /// <summary>逐层参数量摘要（CLI 与日志用）。</summary>
    public static IReadOnlyList<(int Layer, string Type, long Parameters)> DescribeLayers(YoloDetectionModel model) =>
        YoloArchitectures.DescribeLayers(model);
}

