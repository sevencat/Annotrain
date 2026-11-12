using System.Globalization;
using Sevencat.Train.Engine.Core;
using Sevencat.Train.Engine.IO.Onnx;
using Sevencat.Train.Engine.Models.Yolo;
using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Export;

/// <summary>
/// 把 YOLOv8 检测模型导出成 ONNX 推理图，输出 <c>output0: [B, 4+nc, N]</c>，与 ultralytics 一致。
/// <para>
/// TorchSharp 没有 <c>torch.onnx.export</c>，所以按层表手工拼图：
/// <list type="bullet">
/// <item>BatchNorm 折叠进卷积（<c>W' = W·γ/√(σ²+ε)</c>、<c>b' = β - μ·γ/√(σ²+ε)</c>），导出后没有 BN 节点；</item>
/// <item>SiLU 展开为 <c>Sigmoid + Mul</c>；C2f 用 <c>Split</c> 切半、<c>Concat</c> 汇总；SPPF 用连续 <c>MaxPool</c>；</item>
/// <item>检测头按尺度解码：<c>Softmax → DFL 加权和 → ltrb</c>，再用常量锚点算 <c>xyxy</c>，分类分支做 <c>Sigmoid</c>。</item>
/// </list>
/// </para>
/// <para>
/// 输入高宽在导出时就固定下来（锚点被写成常量），批维保留为动态维 <c>batch</c>。
/// </para>
/// </summary>
public static class YoloOnnxExporter
{
    /// <summary>默认输入名（见 docs/formats.md）。</summary>
    public const string DefaultInputName = "input";

    /// <summary>默认输出名（ultralytics 检测模型的 output0）。</summary>
    public const string DefaultOutputName = "output0";

    /// <summary>默认导出尺寸（与 ultralytics 一致）。</summary>
    public const int DefaultImageSize = 640;

    /// <summary>按模型结构构图；<paramref name="imageHeight"/> / <paramref name="imageWidth"/> 必须是 32 的倍数。</summary>
    public static OnnxModel Build(
        YoloDetectionModel model,
        int imageHeight = DefaultImageSize,
        int imageWidth = DefaultImageSize,
        string inputName = DefaultInputName,
        string outputName = DefaultOutputName)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputName);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputName);

        if (imageHeight <= 0 || imageWidth <= 0
            || imageHeight % YoloDetectionModel.InputStride != 0 || imageWidth % YoloDetectionModel.InputStride != 0)
        {
            throw new ArgumentException(
                $"导出尺寸必须是 {YoloDetectionModel.InputStride} 的倍数，实际 {imageHeight}x{imageWidth}。",
                nameof(imageHeight));
        }

        if (string.Equals(inputName, outputName, StringComparison.Ordinal))
            throw new ArgumentException("输入名与输出名不能相同。", nameof(outputName));

        int headIndex = model.LayerCount - 1;
        if (model.Layer(headIndex) is not DetectHeadModule head)
        {
            throw new InvalidOperationException(
                $"YOLO 导出要求最后一层（第 {headIndex} 层）是 DetectHeadModule，实际是 "
                + $"{model.Layer(headIndex).GetType().Name}。");
        }

        var builder = new OnnxGraphBuilder(model.Architecture);
        builder.Input(inputName, OnnxDataType.Float, "batch", 3, imageHeight, imageWidth);

        var context = new YoloExportContext(builder, model.state_dict());

        string[] layerOutputs = new string[headIndex];

        for (int index = 0; index < headIndex; index++)
        {
            IReadOnlyList<int> sources = model.InputSources[index];
            var inputs = new string[sources.Count];

            for (int item = 0; item < sources.Count; item++)
            {
                int source = sources[item];
                inputs[item] = source switch
                {
                    -1 when index == 0 => inputName,
                    -1 => layerOutputs[index - 1],
                    >= 0 => layerOutputs[source],
                    _ => throw new InvalidOperationException(
                        $"第 {index} 层引用了非法的来源索引 {source}。"),
                };
            }

            layerOutputs[index] = EmitLayer(context, $"model.{index}", model.Layer(index), inputs);
        }

        var headInputs = new string[head.LevelCount];
        IReadOnlyList<int> headSources = model.InputSources[headIndex];
        for (int level = 0; level < head.LevelCount; level++)
            headInputs[level] = layerOutputs[headSources[level]];

        EmitDetect(
            context,
            $"model.{headIndex}",
            head,
            headInputs,
            imageHeight,
            imageWidth,
            model.Strides,
            outputName);

        (_, _, _, int anchors) = model.FeatureSizes(imageHeight, imageWidth);
        builder.Output(outputName, OnnxDataType.Float, "batch", 4 + model.ClassCount, anchors);
        return new OnnxModel(builder.Build());
    }

    /// <summary>构图并写出 ONNX 文件。</summary>
    public static void Save(
        YoloDetectionModel model,
        string path,
        int imageHeight = DefaultImageSize,
        int imageWidth = DefaultImageSize,
        string inputName = DefaultInputName,
        string outputName = DefaultOutputName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        Build(model, imageHeight, imageWidth, inputName, outputName).Save(path);
    }

    /// <summary>
    /// 把 BN 折叠进卷积：返回融合后的权重与偏置。
    /// <para>
    /// 推理时 <c>y = γ·(Wx + b - μ)/√(σ²+ε) + β</c>，等价于
    /// <c>W' = W·γ/√(σ²+ε)</c>、<c>b' = β - μ·γ/√(σ²+ε)</c>。
    /// </para>
    /// </summary>
    public static (float[] Weight, float[] Bias) FoldBatchNorm(
        Tensor convWeight,
        Tensor gamma,
        Tensor beta,
        Tensor mean,
        Tensor variance,
        double epsilon)
    {
        ArgumentNullException.ThrowIfNull(convWeight);
        ArgumentNullException.ThrowIfNull(gamma);
        ArgumentNullException.ThrowIfNull(beta);
        ArgumentNullException.ThrowIfNull(mean);
        ArgumentNullException.ThrowIfNull(variance);

        if (convWeight.dim() != 4)
        {
            throw new ArgumentException(
                $"卷积权重必须是 [out,in,k,k]，实际是 [{string.Join(",", convWeight.shape)}]。",
                nameof(convWeight));
        }

        using (torch.no_grad())
        {
            Tensor scale = gamma / (variance + epsilon).sqrt();
            Tensor weight = convWeight * scale.view(-1, 1, 1, 1);
            Tensor bias = beta - mean * scale;
            return (TorchInterop.ToArray(weight), TorchInterop.ToArray(bias));
        }
    }

    private static string EmitLayer(
        YoloExportContext context,
        string prefix,
        Module layer,
        IReadOnlyList<string> inputs)
    {
        return layer switch
        {
            ConvModule conv => EmitConvModule(context, prefix, conv, inputs[0]),
            C2fModule c2f => EmitC2f(context, prefix, c2f, inputs[0]),
            C3k2Module c3k2 => EmitC3k2(context, prefix, c3k2, inputs[0]),
            C2PsaModule psa => EmitC2Psa(context, prefix, psa, inputs[0]),
            SppfModule sppf => EmitSppf(context, prefix, sppf, inputs[0]),
            ConcatModule concat => EmitConcat(context, prefix, concat, inputs),
            Upsample upsample => EmitUpsample(context, prefix, upsample, inputs[0]),
            _ => throw new NotSupportedException(
                $"ONNX 导出不支持层类型 {layer.GetType().Name}（前缀 {prefix}）。"),
        };
    }

    /// <summary>Conv2d(bias=false) + BatchNorm + SiLU → 单个带偏置的 Conv + Sigmoid/Mul。</summary>
    private static string EmitConvModule(
        YoloExportContext context,
        string prefix,
        ConvModule module,
        string input)
    {
        Tensor convWeight = context.State(prefix + ".conv.weight");
        Tensor gamma = context.State(prefix + ".bn.weight");
        Tensor beta = context.State(prefix + ".bn.bias");
        Tensor mean = context.State(prefix + ".bn.running_mean");
        Tensor variance = context.State(prefix + ".bn.running_var");

        (float[] weight, float[] bias) = FoldBatchNorm(
            convWeight,
            gamma,
            beta,
            mean,
            variance,
            ConvModule.BatchNormEpsilon);

        long[] shape = convWeight.shape;
        string weightName = context.ConstantFloat(prefix + ".conv.weight", shape, weight);
        string biasName = context.ConstantFloat(prefix + ".conv.bias", new long[] { shape[0] }, bias);

        string convolution = context.Next(prefix + ".conv");
        context.Builder.Node(
            "Conv",
            new[] { input, weightName, biasName },
            new[] { convolution },
            OnnxAttribute.Ints("pads", module.Padding, module.Padding, module.Padding, module.Padding),
            OnnxAttribute.Ints("strides", module.Stride, module.Stride),
            OnnxAttribute.Ints("dilations", 1, 1),
            OnnxAttribute.Int("group", module.Groups));

        // act=False 的层（Attention 的 qkv/proj/pe、PSA 的 ffn 尾层）导出后没有激活节点。
        return module.HasActivation ? EmitSiLU(context, prefix + ".act", convolution) : convolution;
    }

    private static string EmitSiLU(YoloExportContext context, string prefix, string input)
    {
        string sigmoid = context.Next(prefix + ".sigmoid");
        context.Builder.Node("Sigmoid", new[] { input }, new[] { sigmoid });

        string output = context.Next(prefix + ".out");
        context.Builder.Node("Mul", new[] { input, sigmoid }, new[] { output });
        return output;
    }

    /// <summary>C2f：cv1 → 切半 → Bottleneck 串联（每步结果都保留）→ Concat → cv2。</summary>
    private static string EmitC2f(
        YoloExportContext context,
        string prefix,
        C2fModule module,
        string input)
    {
        string head = EmitConvModule(context, prefix + ".cv1", module.Cv1, input);

        int hidden = module.HiddenChannels;
        string left = context.Next(prefix + ".split.a");
        string right = context.Next(prefix + ".split.b");
        context.Builder.Node(
            "Split",
            new[] { head, context.ConstantInt64(prefix + ".split.shape", hidden, hidden) },
            new[] { left, right },
            OnnxAttribute.Int("axis", 1));

        var parts = new List<string>(module.Blocks.Count + 2) { left, right };
        string current = right;

        for (int index = 0; index < module.Blocks.Count; index++)
        {
            current = EmitBottleneck(context, $"{prefix}.m.{index}", module.Blocks[index], current);
            parts.Add(current);
        }

        string merged = context.Next(prefix + ".cat");
        context.Builder.Node("Concat", parts.ToArray(), new[] { merged }, OnnxAttribute.Int("axis", 1));

        return EmitConvModule(context, prefix + ".cv2", module.Cv2, merged);
    }

    private static string EmitBottleneck(
        YoloExportContext context,
        string prefix,
        BottleneckModule module,
        string input)
    {
        string inner = EmitConvModule(context, prefix + ".cv1", module.Cv1, input);
        inner = EmitConvModule(context, prefix + ".cv2", module.Cv2, inner);

        if (!module.HasShortcut)
            return inner;

        string sum = context.Next(prefix + ".add");
        context.Builder.Node("Add", new[] { input, inner }, new[] { sum });
        return sum;
    }

    /// <summary>C3k2：cv1 拆两半 → 内层块（Bottleneck 或 C3k）串接 → Concat → cv2。</summary>
    private static string EmitC3k2(
        YoloExportContext context,
        string prefix,
        C3k2Module module,
        string input)
    {
        string head = EmitConvModule(context, prefix + ".cv1", module.Cv1, input);
        int hidden = module.HiddenChannels;

        string left = context.Next(prefix + ".split.a");
        string right = context.Next(prefix + ".split.b");
        context.Builder.Node(
            "Split",
            new[] { head, context.ConstantInt64(prefix + ".split.shape", hidden, hidden) },
            new[] { left, right },
            OnnxAttribute.Int("axis", 1));

        var parts = new List<string>(module.Blocks.Count + 2) { left, right };
        string current = right;

        for (int index = 0; index < module.Blocks.Count; index++)
        {
            string blockPrefix = $"{prefix}.m.{index}";
            current = module.Blocks[index] switch
            {
                C3kModule c3k => EmitC3k(context, blockPrefix, c3k, current),
                BottleneckModule bottleneck => EmitBottleneck(context, blockPrefix, bottleneck, current),
                _ => throw new NotSupportedException(
                    $"C3k2 内层块不支持 {module.Blocks[index].GetType().Name}（{blockPrefix}）。"),
            };

            parts.Add(current);
        }

        string merged = context.Next(prefix + ".cat");
        context.Builder.Node("Concat", parts.ToArray(), new[] { merged }, OnnxAttribute.Int("axis", 1));

        return EmitConvModule(context, prefix + ".cv2", module.Cv2, merged);
    }

    /// <summary>C3k：cv1 → Bottleneck 链，与 cv2 分支拼接后由 cv3 融合（只拼两支）。</summary>
    private static string EmitC3k(
        YoloExportContext context,
        string prefix,
        C3kModule module,
        string input)
    {
        string left = EmitConvModule(context, prefix + ".cv1", module.Cv1, input);
        for (int index = 0; index < module.Blocks.Count; index++)
            left = EmitBottleneck(context, $"{prefix}.m.{index}", module.Blocks[index], left);

        string right = EmitConvModule(context, prefix + ".cv2", module.Cv2, input);

        string merged = context.Next(prefix + ".cat");
        context.Builder.Node("Concat", new[] { left, right }, new[] { merged }, OnnxAttribute.Int("axis", 1));

        return EmitConvModule(context, prefix + ".cv3", module.Cv3, merged);
    }

    /// <summary>C2PSA：cv1 拆两半，后半过 PSABlock 链，再与前一半拼接后 cv2 融合。</summary>
    private static string EmitC2Psa(
        YoloExportContext context,
        string prefix,
        C2PsaModule module,
        string input)
    {
        string head = EmitConvModule(context, prefix + ".cv1", module.Cv1, input);
        int hidden = module.HiddenChannels;

        string left = context.Next(prefix + ".split.a");
        string right = context.Next(prefix + ".split.b");
        context.Builder.Node(
            "Split",
            new[] { head, context.ConstantInt64(prefix + ".split.shape", hidden, hidden) },
            new[] { left, right },
            OnnxAttribute.Int("axis", 1));

        string current = right;
        for (int index = 0; index < module.Blocks.Count; index++)
            current = EmitPsaBlock(context, $"{prefix}.m.{index}", module.Blocks[index], current);

        string merged = context.Next(prefix + ".cat");
        context.Builder.Node("Concat", new[] { left, current }, new[] { merged }, OnnxAttribute.Int("axis", 1));

        return EmitConvModule(context, prefix + ".cv2", module.Cv2, merged);
    }

    /// <summary>PSABlock：<c>x += attn(x)</c>，再 <c>x += ffn(x)</c>。</summary>
    private static string EmitPsaBlock(
        YoloExportContext context,
        string prefix,
        PsaBlockModule module,
        string input)
    {
        string attended = EmitAttention(context, prefix + ".attn", module.Attention, input);
        string afterAttention = module.HasShortcut
            ? EmitAdd(context, prefix + ".add.attn", input, attended)
            : attended;

        IReadOnlyList<Module<Tensor, Tensor>> feedForward = module.FeedForward.Items;
        string expanded = EmitConvModule(context, prefix + ".ffn.0", (ConvModule)feedForward[0], afterAttention);
        string projected = EmitConvModule(context, prefix + ".ffn.1", (ConvModule)feedForward[1], expanded);

        return module.HasShortcut
            ? EmitAdd(context, prefix + ".add.ffn", afterAttention, projected)
            : projected;
    }

    private static string EmitAdd(YoloExportContext context, string prefix, string left, string right)
    {
        string output = context.Next(prefix);
        context.Builder.Node("Add", new[] { left, right }, new[] { output });
        return output;
    }

    /// <summary>SPPF：cv1 → 三次同核最大池化 → 四级拼接 → cv2。</summary>
    private static string EmitSppf(
        YoloExportContext context,
        string prefix,
        SppfModule module,
        string input)
    {
        string first = EmitConvModule(context, prefix + ".cv1", module.Cv1, input);
        string[] pooled = new string[4];
        pooled[0] = first;

        for (int index = 1; index < pooled.Length; index++)
        {
            string output = context.Next($"{prefix}.pool{index}");
            context.Builder.Node(
                "MaxPool",
                new[] { pooled[index - 1] },
                new[] { output },
                OnnxAttribute.Ints("kernel_shape", module.KernelSize, module.KernelSize),
                OnnxAttribute.Ints("strides", 1, 1),
                OnnxAttribute.Ints("pads", module.KernelSize / 2, module.KernelSize / 2, module.KernelSize / 2, module.KernelSize / 2));

            pooled[index] = output;
        }

        string merged = context.Next(prefix + ".cat");
        context.Builder.Node("Concat", pooled, new[] { merged }, OnnxAttribute.Int("axis", 1));

        return EmitConvModule(context, prefix + ".cv2", module.Cv2, merged);
    }

    private static string EmitConcat(
        YoloExportContext context,
        string prefix,
        ConcatModule module,
        IReadOnlyList<string> inputs)
    {
        string output = context.Next(prefix + ".cat");
        context.Builder.Node(
            "Concat",
            inputs.ToArray(),
            new[] { output },
            OnnxAttribute.Int("axis", module.Dimension));

        return output;
    }

    private static string EmitUpsample(
        YoloExportContext context,
        string prefix,
        Upsample module,
        string input)
    {
        double[] factors = module.scale_factor.ToArray();
        float height = factors.Length >= 2 ? (float)factors[0] : 2f;
        float width = factors.Length >= 2 ? (float)factors[1] : 2f;

        string scales = context.ConstantFloat(
            prefix + ".scales",
            new long[] { 4 },
            new[] { 1f, 1f, height, width });

        string output = context.Next(prefix + ".resize");
        context.Builder.Node(
            "Resize",
            new[] { input, string.Empty, scales },
            new[] { output },
            OnnxAttribute.String("mode", "nearest"),
            OnnxAttribute.String("coordinate_transformation_mode", "asymmetric"),
            OnnxAttribute.String("nearest_mode", "floor"));

        return output;
    }

    /// <summary>
    /// Attention：qkv（1×1）→ 拆 q/k/v → <c>softmax((q·scale)ᵀ·k)</c> → <c>v·attnᵀ</c> + 深度可分离位置编码 → proj。
    /// <para>
    /// token 数 <c>N = H·W</c> 随输入变化，所以 reshape 目标写成 <c>[0, heads, 2·key+head_dim, -1]</c>（<c>-1</c> 让 ONNX 推断）；
    /// 还原成特征图时用 <c>Shape(input)</c>（=[B,C,H,W]）作目标，避免在导出阶段把空间尺寸写死。
    /// </para>
    /// </summary>
    private static string EmitAttention(
        YoloExportContext context,
        string prefix,
        YoloAttentionModule module,
        string input)
    {
        string packed = EmitConvModule(context, prefix + ".qkv", module.Qkv, input);

        string inputShape = context.Next(prefix + ".shape");
        context.Builder.Node("Shape", new[] { input }, new[] { inputShape });

        string tokens = context.Next(prefix + ".qkv.reshape");
        context.Builder.Node(
            "Reshape",
            new[]
            {
                packed,
                context.ConstantInt64(
                    prefix + ".qkv.shape",
                    0,
                    module.HeadCount,
                    module.HeadChannels + 2 * module.KeyChannels,
                    -1),
            },
            new[] { tokens });

        string q = context.Next(prefix + ".q");
        string k = context.Next(prefix + ".k");
        string v = context.Next(prefix + ".v");
        context.Builder.Node(
            "Split",
            new[]
            {
                tokens,
                context.ConstantInt64(
                    prefix + ".split.shape", module.KeyChannels, module.KeyChannels, module.HeadChannels),
            },
            new[] { q, k, v },
            OnnxAttribute.Int("axis", 2));

        string scaled = context.Next(prefix + ".q.scale");
        context.Builder.Node(
            "Mul",
            new[]
            {
                q,
                context.ConstantFloat(
                    prefix + ".scale",
                    new long[] { 1 },
                    new[] { (float)Math.Pow(module.KeyChannels, -0.5) }),
            },
            new[] { scaled });

        string queryTransposed = context.Next(prefix + ".q.transpose");
        context.Builder.Node(
            "Transpose", new[] { scaled }, new[] { queryTransposed }, OnnxAttribute.Ints("perm", 0, 1, 3, 2));

        string scores = context.Next(prefix + ".scores");
        context.Builder.Node("MatMul", new[] { queryTransposed, k }, new[] { scores });

        string attention = context.Next(prefix + ".softmax");
        context.Builder.Node("Softmax", new[] { scores }, new[] { attention }, OnnxAttribute.Int("axis", 3));

        string attentionTransposed = context.Next(prefix + ".attention.transpose");
        context.Builder.Node(
            "Transpose",
            new[] { attention },
            new[] { attentionTransposed },
            OnnxAttribute.Ints("perm", 0, 1, 3, 2));

        string product = context.Next(prefix + ".product");
        context.Builder.Node("MatMul", new[] { v, attentionTransposed }, new[] { product });

        string attended = context.Next(prefix + ".attended");
        context.Builder.Node("Reshape", new[] { product, inputShape }, new[] { attended });

        string values = context.Next(prefix + ".values");
        context.Builder.Node("Reshape", new[] { v, inputShape }, new[] { values });

        string positioned = EmitConvModule(context, prefix + ".pe", module.PositionalEncoding, values);
        string combined = EmitAdd(context, prefix + ".positioned", attended, positioned);

        return EmitConvModule(context, prefix + ".proj", module.Projection, combined);
    }

    /// <summary>
    /// 检测头的分支结构（3×3 / 3×3 / 1×1）；前几层可以是 <see cref="ConvModule"/> 或嵌套的顺序容器
    /// （YOLOv11 的分类分支是「DWConv + 1×1」两段式），最后一层必须是裸 <c>Conv2d</c>。
    /// </summary>
    private static string EmitSequential(
        YoloExportContext context,
        string prefix,
        YoloSequential sequential,
        string input)
    {
        IReadOnlyList<Module<Tensor, Tensor>> items = sequential.Items;
        if (items.Count == 0)
            throw new InvalidOperationException($"检测分支 '{prefix}' 不能为空。");

        string current = input;
        for (int index = 0; index < items.Count; index++)
            current = EmitBranchItem(context, $"{prefix}.{index}", items[index], current);

        return current;
    }

    /// <summary>分支里的一项：Conv（含 BN + 激活）/ 嵌套 Sequential / 裸 Conv2d（分支末端的 1×1）。</summary>
    private static string EmitBranchItem(
        YoloExportContext context,
        string prefix,
        Module<Tensor, Tensor> item,
        string input) => item switch
    {
        ConvModule conv => EmitConvModule(context, prefix, conv, input),
        YoloSequential nested => EmitSequential(context, prefix, nested, input),
        Conv2d bare => EmitBareConv(context, prefix, bare, input),
        _ => throw new InvalidOperationException(
            $"检测分支 '{prefix}' 的层类型不支持：{item.GetType().Name}（只接受 ConvModule / 嵌套 Sequential / Conv2d）。"),
    };

    /// <summary>裸 <c>Conv2d</c>：键名是 <c>&lt;prefix&gt;.weight</c> / <c>.bias</c>（没有 BN，也不做折叠）。</summary>
    private static string EmitBareConv(
        YoloExportContext context,
        string prefix,
        Conv2d conv,
        string input)
    {
        string weightKey = prefix + ".weight";
        string biasKey = prefix + ".bias";
        Tensor weight = context.State(weightKey);
        Tensor bias = context.State(biasKey);

        string weightName = context.ConstantFloat(weightKey, weight.shape, TorchInterop.ToArray(weight));
        string biasName = context.ConstantFloat(biasKey, bias.shape, TorchInterop.ToArray(bias));

        long stride = conv.stride.Length > 0 ? conv.stride[0] : 1;
        long padding = conv.padding.Length > 0 ? conv.padding[0] : 0;
        int groups = (int)conv.groups;

        string output = context.Next(prefix + ".conv");
        context.Builder.Node(
            "Conv",
            new[] { input, weightName, biasName },
            new[] { output },
            OnnxAttribute.Ints("pads", padding, padding, padding, padding),
            OnnxAttribute.Ints("strides", stride, stride),
            OnnxAttribute.Ints("dilations", 1, 1),
            OnnxAttribute.Int("group", groups));

        return output;
    }

    /// <summary>
    /// 检测头解码：三个尺度各自 DFL → ltrb → xyxy，分类分支 Sigmoid，最后沿锚点维拼成 <c>[B,4+nc,N]</c>。
    /// </summary>
    private static void EmitDetect(
        YoloExportContext context,
        string prefix,
        DetectHeadModule head,
        IReadOnlyList<string> features,
        int imageHeight,
        int imageWidth,
        IReadOnlyList<int> strides,
        string outputName)
    {
        int regressionMax = head.RegressionMax;
        int classCount = head.ClassCount;
        Tensor dflWeight = context.State(prefix + ".dfl.conv.weight");
        float[] weights = TorchInterop.ToArray(dflWeight);

        string[] levelOutputs = new string[head.LevelCount];

        for (int level = 0; level < head.LevelCount; level++)
        {
            int stride = strides[level];
            int rows = imageHeight / stride;
            int columns = imageWidth / stride;
            int anchors = rows * columns;
            string levelPrefix = $"{prefix}.level{level}";

            string boxRaw = EmitSequential(context, $"{prefix}.cv2.{level}", head.BoxBranches[level], features[level]);
            string classRaw = EmitSequential(context, $"{prefix}.cv3.{level}", head.ClassBranches[level], features[level]);

            // [B,4R,h,w] → [B,4,R,hw] → [B,R,4,hw] → softmax → 加权和（单位：stride）
            string reshapedBox = context.Next(levelPrefix + ".reshape.box");
            context.Builder.Node(
                "Reshape",
                new[] { boxRaw, context.ConstantInt64(levelPrefix + ".shape.box", 0, 4, regressionMax, anchors) },
                new[] { reshapedBox });

            string transposed = context.Next(levelPrefix + ".transpose");
            context.Builder.Node(
                "Transpose",
                new[] { reshapedBox },
                new[] { transposed },
                OnnxAttribute.Ints("perm", 0, 2, 1, 3));

            string probabilities = context.Next(levelPrefix + ".softmax");
            context.Builder.Node(
                "Softmax",
                new[] { transposed },
                new[] { probabilities },
                OnnxAttribute.Int("axis", 1));

            string weighted = context.Next(levelPrefix + ".dfl.mul");
            context.Builder.Node(
                "Mul",
                new[] { probabilities, context.ConstantFloat(levelPrefix + ".dfl.weight", dflWeight.shape, weights) },
                new[] { weighted });

            string summed = context.Next(levelPrefix + ".dfl.sum");
            context.Builder.Node(
                "ReduceSum",
                new[] { weighted, context.ConstantInt64(levelPrefix + ".dfl.axes", 1) },
                new[] { summed },
                OnnxAttribute.Int("keepdims", 1));

            string distances = context.Next(levelPrefix + ".distances");
            context.Builder.Node(
                "Reshape",
                new[] { summed, context.ConstantInt64(levelPrefix + ".shape.dist", 0, 4, anchors) },
                new[] { distances });

            // DFL 期望值的单位是 stride，乘以该层的 stride 才是像素距离。
            string pixels = context.Next(levelPrefix + ".pixels");
            context.Builder.Node(
                "Mul",
                new[]
                {
                    distances,
                    context.ConstantFloat(levelPrefix + ".stride", new long[] { 1 }, new[] { (float)stride }),
                },
                new[] { pixels });

            var sides = new string[4];
            for (int side = 0; side < sides.Length; side++)
                sides[side] = context.Next($"{levelPrefix}.side{side}");

            context.Builder.Node(
                "Split",
                new[] { pixels, context.ConstantInt64(levelPrefix + ".split.shape", 1, 1, 1, 1) },
                sides,
                OnnxAttribute.Int("axis", 1));

            var anchorX = new float[anchors];
            var anchorY = new float[anchors];
            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    int index = row * columns + column;
                    anchorX[index] = (column + 0.5f) * stride;
                    anchorY[index] = (row + 0.5f) * stride;
                }
            }

            string anchorXName = context.ConstantFloat(
                levelPrefix + ".anchor.x", new long[] { 1, 1, anchors }, anchorX);
            string anchorYName = context.ConstantFloat(
                levelPrefix + ".anchor.y", new long[] { 1, 1, anchors }, anchorY);

            string x1 = context.Next(levelPrefix + ".x1");
            context.Builder.Node("Sub", new[] { anchorXName, sides[0] }, new[] { x1 });

            string y1 = context.Next(levelPrefix + ".y1");
            context.Builder.Node("Sub", new[] { anchorYName, sides[1] }, new[] { y1 });

            string x2 = context.Next(levelPrefix + ".x2");
            context.Builder.Node("Add", new[] { anchorXName, sides[2] }, new[] { x2 });

            string y2 = context.Next(levelPrefix + ".y2");
            context.Builder.Node("Add", new[] { anchorYName, sides[3] }, new[] { y2 });

            string boxes = context.Next(levelPrefix + ".boxes");
            context.Builder.Node(
                "Concat",
                new[] { x1, y1, x2, y2 },
                new[] { boxes },
                OnnxAttribute.Int("axis", 1));

            string classFlat = context.Next(levelPrefix + ".cls");
            context.Builder.Node(
                "Reshape",
                new[] { classRaw, context.ConstantInt64(levelPrefix + ".shape.cls", 0, classCount, anchors) },
                new[] { classFlat });

            string scores = context.Next(levelPrefix + ".cls.sigmoid");
            context.Builder.Node("Sigmoid", new[] { classFlat }, new[] { scores });

            levelOutputs[level] = context.Next(levelPrefix + ".out");
            context.Builder.Node(
                "Concat",
                new[] { boxes, scores },
                new[] { levelOutputs[level] },
                OnnxAttribute.Int("axis", 1));
        }

        context.Builder.Node(
            "Concat",
            levelOutputs,
            new[] { outputName },
            OnnxAttribute.Int("axis", 2));
    }

    /// <summary>导出上下文：持有图构建器、state_dict 快照与唯一的张量命名。</summary>
    private sealed class YoloExportContext
    {
        private readonly Dictionary<string, Tensor> _state;
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);

        public YoloExportContext(OnnxGraphBuilder builder, IReadOnlyDictionary<string, Tensor> state)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(state);

            Builder = builder;
            _state = new Dictionary<string, Tensor>(state.Count, StringComparer.Ordinal);
            foreach ((string key, Tensor tensor) in state)
                _state[key] = tensor;
        }

        public OnnxGraphBuilder Builder { get; }

        /// <summary>取 state_dict 中的张量；缺失时报错并指出键名。</summary>
        public Tensor State(string key)
        {
            if (!_state.TryGetValue(key, out Tensor? tensor))
            {
                throw new InvalidDataException(
                    $"state_dict 缺少 '{key}'，无法导出 ONNX。请确认检查点与 architecture 元数据匹配。");
            }

            return tensor;
        }

        /// <summary>生成唯一的中间张量名（ONNX 的 initializer 与节点输出共享命名空间）。</summary>
        public string Next(string hint) => Reserve(hint);

        public string ConstantFloat(string hint, long[] dims, float[] values)
        {
            string name = Reserve(hint);
            Builder.Initializer(OnnxTensor.FromFloat32(name, dims, values));
            return name;
        }

        public string ConstantInt64(string hint, params long[] values)
        {
            string name = Reserve(hint);
            Builder.Initializer(OnnxTensor.FromInt64(name, new long[] { values.Length }, values));
            return name;
        }

        private string Reserve(string hint)
        {
            string candidate = hint;
            int suffix = 0;

            while (!_names.Add(candidate))
                candidate = hint + "." + (++suffix).ToString(CultureInfo.InvariantCulture);

            return candidate;
        }
    }
}

