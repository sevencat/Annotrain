using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.Yolo;

/// <summary>
/// ultralytics 的 <c>Detect</c> 头：三个尺度各走一条 <c>cv2</c>（box 回归）与 <c>cv3</c>（分类）分支，
/// 输出未解码的原始预测。
/// <para>
/// 每个尺度输出 <c>[B, 4*reg_max + nc, H*W]</c>，沿锚点维拼接成 <c>[B, 4*reg_max + nc, N]</c>。
/// 训练时该张量直接喂给 TAL 分配与 box / cls / dfl 三项损失；推理解码
/// （DFL 积分 + dist2bbox + sigmoid）由导出 / 预测路径负责，保持与 ultralytics 的
/// <c>output0</c> 布局一致。
/// </para>
/// <para>
/// 子模块名 <c>cv2</c> / <c>cv3</c> / <c>dfl</c> 与 ultralytics 一致，
/// 键名形如 <c>model.22.cv2.0.0.conv.weight</c>。
/// </para>
/// </summary>
public sealed class DetectHeadModule : Module<Tensor[], Tensor>
{
    /// <summary>ultralytics 默认的 DFL 区间数。</summary>
    public const int DefaultRegressionMax = 16;

    private readonly ModuleList<Module<Tensor, Tensor>> cv2;
    private readonly ModuleList<Module<Tensor, Tensor>> cv3;
    private readonly DflModule dfl;
    private readonly int _levels;
    private readonly YoloSequential[] _boxBranches;
    private readonly YoloSequential[] _classBranches;

    /// <summary>按各尺度输入通道创建检测头。</summary>
    /// <param name="inputChannels">P3 / P4 / P5 的输入通道。</param>
    /// <param name="classCount">类别数。</param>
    /// <param name="regressionMax">DFL 区间数（ultralytics 默认 16）。</param>
    /// <param name="device">计算设备。</param>
    /// <param name="depthwiseClassBranch">
    /// 分类分支是否用「深度可分离 3×3 + 1×1」的两段式（YOLOv11 的写法，键名多一层：
    /// <c>cv3.0.0.0.conv.weight</c>）；YOLOv8 用普通 3×3 卷积。
    /// </param>
    public DetectHeadModule(
        IReadOnlyList<int> inputChannels,
        int classCount,
        int regressionMax = DefaultRegressionMax,
        Device? device = null,
        bool depthwiseClassBranch = false)
        : base("Detect")
    {
        ArgumentNullException.ThrowIfNull(inputChannels);

        if (inputChannels.Count == 0)
            throw new ArgumentException("检测头至少需要 1 个尺度。", nameof(inputChannels));
        if (classCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(classCount), classCount, "类别数必须大于 0。");
        if (regressionMax <= 0)
            throw new ArgumentOutOfRangeException(nameof(regressionMax), regressionMax, "reg_max 必须大于 0。");

        _levels = inputChannels.Count;
        ClassCount = classCount;
        RegressionMax = regressionMax;
        UsesDepthwiseClassBranch = depthwiseClassBranch;

        // 与 ultralytics Detect 的宽度选择一致：回归分支取 max(16, ch/4, 4*reg_max)，
        // 分类分支取 max(ch[0], min(nc, 100))。
        int boxChannels = Math.Max(16, Math.Max(inputChannels[0] / 4, regressionMax * 4));
        int classChannels = Math.Max(inputChannels[0], Math.Min(classCount, 100));

        cv2 = new ModuleList<Module<Tensor, Tensor>>();
        cv3 = new ModuleList<Module<Tensor, Tensor>>();
        _boxBranches = new YoloSequential[inputChannels.Count];
        _classBranches = new YoloSequential[inputChannels.Count];

        for (int level = 0; level < inputChannels.Count; level++)
        {
            int channels = inputChannels[level];

            _boxBranches[level] = new YoloSequential(
                "Sequential",
                ("0", new ConvModule(channels, boxChannels, 3, 1, device)),
                ("1", new ConvModule(boxChannels, boxChannels, 3, 1, device)),
                ("2", nn.Conv2d(
                    boxChannels,
                    4 * regressionMax,
                    1,
                    1,
                    0,
                    bias: true,
                    device: device,
                    dtype: ScalarType.Float32)));

            _classBranches[level] = depthwiseClassBranch
                ? new YoloSequential(
                    "Sequential",
                    ("0", new YoloSequential(
                        "Sequential",
                        ("0", new ConvModule(channels, channels, 3, 1, device, groups: channels)),
                        ("1", new ConvModule(channels, classChannels, 1, 1, device)))),
                    ("1", new YoloSequential(
                        "Sequential",
                        ("0", new ConvModule(classChannels, classChannels, 3, 1, device, groups: classChannels)),
                        ("1", new ConvModule(classChannels, classChannels, 1, 1, device)))),
                    ("2", nn.Conv2d(
                        classChannels,
                        classCount,
                        1,
                        1,
                        0,
                        bias: true,
                        device: device,
                        dtype: ScalarType.Float32)))
                : new YoloSequential(
                    "Sequential",
                    ("0", new ConvModule(channels, classChannels, 3, 1, device)),
                    ("1", new ConvModule(classChannels, classChannels, 3, 1, device)),
                    ("2", nn.Conv2d(
                        classChannels,
                        classCount,
                        1,
                        1,
                        0,
                        bias: true,
                        device: device,
                        dtype: ScalarType.Float32)));

            cv2.Add(_boxBranches[level]);
            cv3.Add(_classBranches[level]);
        }

        dfl = new DflModule(regressionMax, device);

        register_module("cv2", cv2);
        register_module("cv3", cv3);
        register_module("dfl", dfl);
    }

    /// <summary>类别数（nc）。</summary>
    public int ClassCount { get; }

    /// <summary>DFL 区间数（reg_max）。</summary>
    public int RegressionMax { get; }

    /// <summary>分类分支是否用深度可分离卷积（YOLOv11）。</summary>
    public bool UsesDepthwiseClassBranch { get; }

    /// <summary>每个锚点的输出宽度：<c>4 * reg_max + nc</c>。</summary>
    public int OutputChannels => 4 * RegressionMax + ClassCount;

    /// <summary>尺度数量（YOLOv8 为 3：P3 / P4 / P5）。</summary>
    public int LevelCount => _levels;

    /// <summary>DFL 积分层（导出时需要它的权重）。</summary>
    public DflModule Dfl => dfl;

    /// <summary>各尺度的 box 回归分支（<c>cv2[level]</c>，导出时逐层展开）。</summary>
    public IReadOnlyList<YoloSequential> BoxBranches => _boxBranches;

    /// <summary>各尺度的分类分支（<c>cv3[level]</c>，导出时逐层展开）。</summary>
    public IReadOnlyList<YoloSequential> ClassBranches => _classBranches;

    /// <inheritdoc />
    public override Tensor forward(Tensor[] features)
    {
        ArgumentNullException.ThrowIfNull(features);
        if (features.Length != _levels)
        {
            throw new ArgumentException(
                $"检测头需要 {_levels} 个尺度输入，实际收到 {features.Length} 个。",
                nameof(features));
        }

        var outputs = new Tensor[_levels];
        for (int level = 0; level < _levels; level++)
        {
            Tensor box = cv2[level].forward(features[level]);
            Tensor classes = cv3[level].forward(features[level]);
            outputs[level] = torch.cat(new[] { box, classes }, 1).flatten(2);
        }

        return torch.cat(outputs, 2);
    }
}
