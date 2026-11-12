using TorchSharp;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.Yolo;

/// <summary>
/// YOLO 检测模型的公共骨架：主干 + 颈部由 <see cref="YoloModuleChain"/> 描述，
/// 最后接一个 <see cref="DetectHeadModule"/>。v8 与 v11 只在层类型与缩放系数上不同，
/// 训练 / 评估 / 导出用的都是这里暴露的接口，所以它们可以互换。
/// <para>
/// <see cref="forward"/> 返回<b>未解码</b>的原始预测 <c>[B, 4*reg_max + nc, N]</c>，
/// 其中 <c>N = (H/8)*(W/8) + (H/16)*(W/16) + (H/32)*(W/32)</c>。
/// </para>
/// </summary>
public abstract class YoloDetectionModel : Module<Tensor, Tensor>
{
    /// <summary>输入尺寸必须是该值的倍数（P3 / P4 / P5 的最大下采样倍数）。</summary>
    public const int InputStride = 32;

    private readonly YoloModuleChain _model;
    private readonly DetectHeadModule _head;

    /// <summary>由具体家族（v8 / v11）的工厂调用。</summary>
    protected YoloDetectionModel(
        string name,
        YoloModelFamily family,
        YoloScale scale,
        int classCount,
        YoloModuleChain model,
        DetectHeadModule head)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(head);

        _model = model;
        _head = head;
        Family = family;
        Scale = scale;
        ClassCount = classCount;

        register_module("model", model);
    }

    /// <summary>模型家族（决定 architecture 前缀）。</summary>
    public YoloModelFamily Family { get; }

    /// <summary>模型规模（n / s / m / l / x）。</summary>
    public YoloScale Scale { get; }

    /// <summary>类别数。</summary>
    public int ClassCount { get; }

    /// <summary>检查点 <c>architecture</c> 元数据，例如 <c>yolo11n-detect-3</c>。</summary>
    public string Architecture => YoloScales.Describe(Family, Scale, ClassCount);

    /// <summary>层数（v8 为 23 层，v11 为 24 层）。</summary>
    public int LayerCount => _model.Count;

    /// <summary>特征图相对输入的下采样倍数（P3 / P4 / P5）。</summary>
    public IReadOnlyList<int> Strides { get; } = new[] { 8, 16, 32 };

    /// <summary>每个锚点的输出宽度（<c>4*reg_max + nc</c>）。</summary>
    public int OutputChannels => _head.OutputChannels;

    /// <summary>检测头（导出 / 解码时需要 DFL 权重）。</summary>
    public DetectHeadModule Head => _head;

    /// <summary>取某一层的子模块（导出与调试用）。</summary>
    public Module Layer(int index) => _model.Layer(index);

    /// <summary>每层的输入来源（<c>-1</c> = 上一层，非负 = 该层号），ONNX 导出按它连边。</summary>
    public IReadOnlyList<IReadOnlyList<int>> InputSources => _model.InputSources;

    /// <summary>按输入尺寸算三个尺度的锚点数量，供损失按尺度拆分原始预测。</summary>
    public (int Small, int Medium, int Large, int Anchors) FeatureSizes(int inputHeight, int inputWidth)
    {
        ValidateInputSize(inputHeight, inputWidth);

        int small = inputHeight / 8 * (inputWidth / 8);
        int medium = inputHeight / 16 * (inputWidth / 16);
        int large = inputHeight / 32 * (inputWidth / 32);
        return (small, medium, large, small + medium + large);
    }

    /// <summary>前向：输入 <c>[B,3,H,W]</c>，输出原始预测 <c>[B,4*reg_max+nc,N]</c>。</summary>
    public override Tensor forward(Tensor input) => ForwardTrace(input)[^1];

    /// <summary>
    /// 逐层输出（调试 / 与参考实现逐层对齐用）：<c>result[i]</c> 是第 <c>i</c> 层的输出。
    /// </summary>
    public IReadOnlyList<Tensor> ForwardTrace(Tensor input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.dim() != 4 || input.shape[1] != 3)
        {
            throw new ArgumentException(
                $"{Architecture} 输入必须是 [B,3,H,W]，实际是 [{string.Join(",", input.shape)}]。",
                nameof(input));
        }

        ValidateInputSize((int)input.shape[2], (int)input.shape[3]);
        return _model.ForwardTrace(new[] { input });
    }

    /// <summary>输入高宽必须是 32 的倍数（P3/P4/P5 下采样），否则后续 reshape 会错位。</summary>
    protected static void ValidateInputSize(int height, int width)
    {
        if (height <= 0 || width <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), height, "输入尺寸必须大于 0。");

        if (height % InputStride != 0 || width % InputStride != 0)
        {
            throw new ArgumentException(
                $"YOLO 输入高宽必须是 {InputStride} 的倍数（P3/P4/P5 下采样），实际 {height}x{width}。");
        }
    }
}
