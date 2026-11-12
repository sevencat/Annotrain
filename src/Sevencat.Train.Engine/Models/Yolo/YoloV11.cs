using TorchSharp;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.Yolo;

/// <summary>
/// YOLOv11 检测模型工厂：按 ultralytics <c>yolo11.yaml</c> 的 24 层结构组装主干、PAN 颈部与检测头。
/// <para>
/// 与 v8 的差异（全部体现在这里）：
/// <list type="bullet">
/// <item>主干块换成 <c>C3k2</c>（内层 Bottleneck / C3k），主干末尾多一个 <c>C2PSA</c> 注意力块；</item>
/// <item>检测头的分类分支用「深度可分离 3×3 + 1×1」两段式；</item>
/// <item>缩放系数表不同（<c>depth 0.50</c>、m/l/x 的 <c>max_channels = 512</c>）。</item>
/// </list>
/// 结构正确的判据是 <c>yolo11n(nc=80)</c> 的参数总量恰好 2,624,080（ultralytics 8.4 实测值，测试锁定）。
/// </para>
/// </summary>
public static class YoloV11
{
    /// <summary>输入尺寸必须是该值的倍数（P3 / P4 / P5 的最大下采样倍数）。</summary>
    public const int InputStride = YoloDetectionModel.InputStride;

    /// <summary>创建 YOLOv11 检测模型。</summary>
    public static YoloV11DetectionModel Create(
        Device device,
        YoloScale scale = YoloScale.N,
        int classCount = 80)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (classCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(classCount), classCount, "类别数必须大于 0。");

        YoloScaleConfig config = YoloScales.Config(YoloModelFamily.V11, scale);

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

        int[] Self() => new[] { -1 };

        // backbone 0-10
        Add(Self(), new ConvModule(3, Width(64), 3, 2, device), Width(64));
        Add(Self(), new ConvModule(Ch(0), Width(128), 3, 2, device), Width(128));
        Add(Self(), new C3k2Module(Ch(1), Width(256), Repeats(2), false, 0.25, true, device), Width(256));
        Add(Self(), new ConvModule(Ch(2), Width(256), 3, 2, device), Width(256));
        Add(Self(), new C3k2Module(Ch(3), Width(512), Repeats(2), false, 0.25, true, device), Width(512));
        Add(Self(), new ConvModule(Ch(4), Width(512), 3, 2, device), Width(512));
        Add(Self(), new C3k2Module(Ch(5), Width(512), Repeats(2), true, 0.5, true, device), Width(512));
        Add(Self(), new ConvModule(Ch(6), Width(1024), 3, 2, device), Width(1024));
        Add(Self(), new C3k2Module(Ch(7), Width(1024), Repeats(2), true, 0.5, true, device), Width(1024));
        Add(Self(), new SppfModule(Ch(8), Width(1024), 5, device), Width(1024));
        Add(Self(), new C2PsaModule(Ch(9), Width(1024), Repeats(2), 0.5, device), Width(1024));

        // neck 11-22（上采样 + 跨层拼接）
        Add(Self(), nn.Upsample(null, new double[] { 2.0, 2.0 }, UpsampleMode.Nearest), Ch(10));
        Add(new[] { 11, 6 }, new ConcatModule(), Ch(11) + Ch(6));
        Add(Self(), new C3k2Module(Ch(12), Width(512), Repeats(2), false, 0.5, true, device), Width(512));
        Add(Self(), nn.Upsample(null, new double[] { 2.0, 2.0 }, UpsampleMode.Nearest), Ch(13));
        Add(new[] { 14, 4 }, new ConcatModule(), Ch(14) + Ch(4));
        Add(Self(), new C3k2Module(Ch(15), Width(256), Repeats(2), false, 0.5, true, device), Width(256));
        Add(Self(), new ConvModule(Ch(16), Width(256), 3, 2, device), Width(256));
        Add(new[] { 17, 13 }, new ConcatModule(), Ch(17) + Ch(13));
        Add(Self(), new C3k2Module(Ch(18), Width(512), Repeats(2), false, 0.5, true, device), Width(512));
        Add(Self(), new ConvModule(Ch(19), Width(512), 3, 2, device), Width(512));
        Add(new[] { 20, 10 }, new ConcatModule(), Ch(20) + Ch(10));
        Add(Self(), new C3k2Module(Ch(21), Width(1024), Repeats(2), true, 0.5, true, device), Width(1024));

        // head 23：P3 / P4 / P5（分类分支用深度可分离卷积）
        var head = new DetectHeadModule(
            new[] { Ch(16), Ch(19), Ch(22) },
            classCount,
            DetectHeadModule.DefaultRegressionMax,
            device,
            depthwiseClassBranch: true);
        Add(new[] { 16, 19, 22 }, head, head.OutputChannels);

        var chain = new YoloModuleChain("model", from, modules);
        return new YoloV11DetectionModel("DetectionModel", chain, head, scale, classCount);
    }
}


