using Sevencat.Train.Engine.Core;
using Sevencat.Train.Engine.IO.Onnx;
using Sevencat.Train.Engine.Models.SimpleDetr;
using TorchSharp;
using static TorchSharp.torch;

namespace Sevencat.Train.Engine.Export;

/// <summary>
/// 把 RF-DETR 风格模型导出成 ONNX：输入 <c>input [1,3,H,W]</c>，输出
/// <c>boxes [1,Q,4]</c>（sigmoid 后的归一化 cxcywh）与 <c>logits [1,Q,nc]</c>。
/// <para>
/// 与 YOLO 导出的差别：DETR 导出时把 batch 固定为 1（不做 <c>Expand</c>），H/W 也固定；
/// 注意力用显式 <c>MatMul + Softmax</c> 展开，LayerNorm 用 opset 17 的
/// <c>LayerNormalization</c>，卷积的 BatchNorm 用 <see cref="YoloOnnxExporter.FoldBatchNorm"/> 折叠。
/// </para>
/// </summary>
public static class SimpleDetrOnnxExporter
{
    /// <summary>输入名。</summary>
    public const string DefaultInputName = "input";

    /// <summary>框输出名（归一化 cxcywh，取值 0~1）。</summary>
    public const string BoxesOutputName = "boxes";

    /// <summary>分类输出名（原始 logits）。</summary>
    public const string LogitsOutputName = "logits";

    private static readonly int[] Widths = { 64, 128, 256, 512 };

    /// <summary>按模型结构与 <c>state_dict</c> 构图。</summary>
    public static OnnxModel Build(
        SimpleDetrModel model,
        int imageHeight,
        int imageWidth,
        string inputName = DefaultInputName)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputName);

        if (imageHeight <= 0 || imageWidth <= 0 || imageHeight % 16 != 0 || imageWidth % 16 != 0)
        {
            throw new ArgumentException(
                $"导出尺寸必须是 16 的倍数，实际 {imageHeight}x{imageWidth}。",
                nameof(imageHeight));
        }

        SimpleDetrArchitecture architecture = model.Architecture;
        int tokens = imageHeight / 16 * (imageWidth / 16);

        if (tokens > SimpleDetrModel.MaxTokens)
        {
            throw new ArgumentException(
                $"{imageHeight}x{imageWidth} 需要 {tokens} 个 token，超过位置编码上限 {SimpleDetrModel.MaxTokens}。",
                nameof(imageHeight));
        }

        var builder = new OnnxGraphBuilder($"simple-detr-detect-{architecture.ClassCount}");
        builder.Input(inputName, OnnxDataType.Float, 1L, 3L, imageHeight, imageWidth);

        var context = new ExportContext(builder, model.state_dict(), architecture);

        string backbone = EmitBackbone(context, architecture, inputName);
        string memory = EmitMemory(context, backbone, tokens);
        string queries = EmitDecoder(context, architecture, memory, tokens);

        string boxes = EmitSigmoid(context, EmitHead(context, "box_head", 4, queries), "boxes.sigmoid");
        string logits = EmitHead(context, "class_head", architecture.ClassCount, queries);

        builder.Node("Identity", new[] { boxes }, new[] { BoxesOutputName });
        builder.Node("Identity", new[] { logits }, new[] { LogitsOutputName });

        builder.Output(BoxesOutputName, OnnxDataType.Float, 1L, architecture.Queries, 4);
        builder.Output(LogitsOutputName, OnnxDataType.Float, 1L, architecture.Queries, architecture.ClassCount);

        return new OnnxModel(builder.Build());
    }

    /// <summary>构图并写出文件。</summary>
    public static void Save(SimpleDetrModel model, string path, int imageHeight, int imageWidth)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        Build(model, imageHeight, imageWidth).Save(path);
    }

    /// <summary>主干：stem + 各 stage 的 BasicBlock（卷积已折叠 BN）。</summary>
    private static string EmitBackbone(
        ExportContext context,
        SimpleDetrArchitecture architecture,
        string input)
    {
        string current = EmitConvRelu(context, "backbone.stem", 3, 64, 3, 2, input);

        int stageCount = Math.Min(architecture.BlocksPerStage.Count, Widths.Length);
        int flat = 0;

        for (int stage = 0; stage < stageCount; stage++)
        {
            int inputChannels = stage == 0 ? 64 : Widths[stage - 1];
            int outputChannels = Widths[stage];
            int stride = stage == 0 ? 1 : 2;

            for (int block = 0; block < architecture.BlocksPerStage[stage]; block++, flat++)
            {
                bool first = block == 0;
                int blockStride = first ? stride : 1;
                int blockInput = first ? inputChannels : outputChannels;
                string prefix = $"backbone.stages.{flat}";

                string hidden = EmitConvRelu(
                    context, prefix + ".conv1", blockInput, outputChannels, 3, blockStride, current);

                string conv2 = EmitConv(
                    context, prefix + ".conv2", outputChannels, outputChannels, 3, 1, hidden);

                string residual = blockInput == outputChannels && blockStride == 1
                    ? current
                    : EmitConv(context, prefix + ".downsample", blockInput, outputChannels, 1, blockStride, current);

                string sum = context.Next(prefix + ".sum");
                context.Builder.Node("Add", new[] { conv2, residual }, new[] { sum });
                current = EmitRelu(context, sum, prefix + ".act");
            }
        }

        return current;
    }

    /// <summary>1×1 投影 → 展平成 token 序列 → 加位置编码（按实际 token 数裁剪）。</summary>
    private static string EmitMemory(
        ExportContext context,
        string backbone,
        int tokens)
    {
        Tensor weight = context.State("input_proj.weight");
        Tensor bias = context.State("input_proj.bias");
        string weightName = context.ConstantFloat("input_proj.weight", weight.shape, TorchInterop.ToArray(weight));
        string biasName = context.ConstantFloat("input_proj.bias", bias.shape, TorchInterop.ToArray(bias));

        string projected = context.Next("input_proj");
        context.Builder.Node(
            "Conv",
            new[] { backbone, weightName, biasName },
            new[] { projected },
            OnnxAttribute.Ints("pads", 0, 0, 0, 0),
            OnnxAttribute.Ints("strides", 1, 1),
            OnnxAttribute.Ints("dilations", 1, 1),
            OnnxAttribute.Int("group", 1));

        string permuted = context.Next("memory.transpose");
        context.Builder.Node(
            "Transpose",
            new[] { projected },
            new[] { permuted },
            OnnxAttribute.Ints("perm", 0, 2, 3, 1));

        string flat = context.Next("memory.flat");
        context.Builder.Node(
            "Reshape",
            new[] { permuted, context.ConstantInt64("memory.shape", 1, tokens, SimpleDetrModel.EmbeddingSize) },
            new[] { flat });

        float[] positions = TorchInterop.ToArray(context.State("pos_embed"));
        var trimmed = new float[tokens * SimpleDetrModel.EmbeddingSize];
        Array.Copy(positions, trimmed, trimmed.Length);

        string positionName = context.ConstantFloat(
            "pos_embed", new long[] { 1, tokens, SimpleDetrModel.EmbeddingSize }, trimmed);

        string memory = context.Next("memory");
        context.Builder.Node("Add", new[] { flat, positionName }, new[] { memory });
        return memory;
    }

    /// <summary>编码器 + 解码器，返回 query 特征 <c>[1,Q,D]</c>。</summary>
    private static string EmitDecoder(
        ExportContext context,
        SimpleDetrArchitecture architecture,
        string memory,
        int tokens)
    {
        string current = memory;

        for (int layer = 0; layer < architecture.EncoderLayers; layer++)
        {
            string prefix = $"encoder.{layer}";
            string norm1 = EmitLayerNorm(context, prefix + ".norm1", current, $"encoder{layer}.norm1");
            string attended = EmitAttention(context, prefix + ".self_attn", norm1, norm1, norm1, tokens, tokens);

            string sum1 = context.Next(prefix + ".sum1");
            context.Builder.Node("Add", new[] { current, attended }, new[] { sum1 });

            string norm2 = EmitLayerNorm(context, prefix + ".norm2", sum1, $"encoder{layer}.norm2");
            string ffn = EmitFeedForward(context, prefix + ".ffn", norm2);

            current = context.Next(prefix + ".out");
            context.Builder.Node("Add", new[] { sum1, ffn }, new[] { current });
        }

        // 编码器输出才是解码器 cross-attention 的 k/v（模型里 memory 在编码器循环中被重新赋值）。
        string encodedMemory = current;

        current = context.ConstantFloat(
            "query_embed",
            new long[] { 1, architecture.Queries, SimpleDetrModel.EmbeddingSize },
            TorchInterop.ToArray(context.State("query_embed")));

        for (int layer = 0; layer < architecture.DecoderLayers; layer++)
        {
            string prefix = $"decoder.{layer}";
            int queries = architecture.Queries;

            string norm1 = EmitLayerNorm(context, prefix + ".norm1", current, $"decoder{layer}.norm1");
            string selfAttended = EmitAttention(
                context, prefix + ".self_attn", norm1, norm1, norm1, queries, queries);

            string sum1 = context.Next(prefix + ".sum1");
            context.Builder.Node("Add", new[] { current, selfAttended }, new[] { sum1 });

            string norm2 = EmitLayerNorm(context, prefix + ".norm2", sum1, $"decoder{layer}.norm2");
            string cross = EmitAttention(
                context, prefix + ".cross_attn", norm2, encodedMemory, encodedMemory, queries, tokens);

            string sum2 = context.Next(prefix + ".sum2");
            context.Builder.Node("Add", new[] { sum1, cross }, new[] { sum2 });

            string norm3 = EmitLayerNorm(context, prefix + ".norm3", sum2, $"decoder{layer}.norm3");
            string ffn = EmitFeedForward(context, prefix + ".ffn", norm3);

            current = context.Next(prefix + ".out");
            context.Builder.Node("Add", new[] { sum2, ffn }, new[] { current });
        }

        return current;
    }

    private static string EmitConvRelu(
        ExportContext context,
        string prefix,
        int inputChannels,
        int outputChannels,
        int kernelSize,
        int stride,
        string input)
    {
        return EmitRelu(context, EmitConv(context, prefix, inputChannels, outputChannels, kernelSize, stride, input), prefix + ".act");
    }

    /// <summary>Conv + BN（折叠成一个带偏置的 Conv）。state_dict 布局：<c>prefix.0.weight</c> + <c>prefix.1.*</c>。</summary>
    private static string EmitConv(
        ExportContext context,
        string prefix,
        int inputChannels,
        int outputChannels,
        int kernelSize,
        int stride,
        string input)
    {
        Tensor convWeight = context.State(prefix + ".0.weight");

        (float[] weight, float[] bias) = YoloOnnxExporter.FoldBatchNorm(
            convWeight,
            context.State(prefix + ".1.weight"),
            context.State(prefix + ".1.bias"),
            context.State(prefix + ".1.running_mean"),
            context.State(prefix + ".1.running_var"),
            1e-5);

        string weightName = context.ConstantFloat(
            prefix + ".weight",
            new long[] { outputChannels, inputChannels, kernelSize, kernelSize },
            weight);

        string biasName = context.ConstantFloat(prefix + ".bias", new long[] { outputChannels }, bias);

        string output = context.Next(prefix);
        int padding = kernelSize / 2;

        context.Builder.Node(
            "Conv",
            new[] { input, weightName, biasName },
            new[] { output },
            OnnxAttribute.Ints("pads", padding, padding, padding, padding),
            OnnxAttribute.Ints("strides", stride, stride),
            OnnxAttribute.Ints("dilations", 1, 1),
            OnnxAttribute.Int("group", 1));

        return output;
    }

    private static string EmitRelu(ExportContext context, string input, string hint)
    {
        string output = context.Next(hint);
        context.Builder.Node("Relu", new[] { input }, new[] { output });
        return output;
    }

    private static string EmitSigmoid(ExportContext context, string input, string hint)
    {
        string output = context.Next(hint);
        context.Builder.Node("Sigmoid", new[] { input }, new[] { output });
        return output;
    }

    /// <summary>Linear：PyTorch 权重 <c>[out,in]</c> 转置成 <c>[in,out]</c> 直接做 MatMul，再加 bias。</summary>
    private static string EmitLinear(ExportContext context, string prefix, string input)
    {
        Tensor weight = context.State(prefix + ".weight");
        Tensor bias = context.State(prefix + ".bias");

        float[] transposed = TorchInterop.ToArray(weight.transpose(0, 1).contiguous());
        string weightName = context.ConstantFloat(
            prefix + ".weight",
            new long[] { weight.shape[1], weight.shape[0] },
            transposed);

        string product = context.Next(prefix + ".matmul");
        context.Builder.Node("MatMul", new[] { input, weightName }, new[] { product });

        string biasName = context.ConstantFloat(prefix + ".bias", bias.shape, TorchInterop.ToArray(bias));

        string output = context.Next(prefix);
        context.Builder.Node("Add", new[] { product, biasName }, new[] { output });
        return output;
    }

    /// <summary>LayerNorm（opset 17 的 <c>LayerNormalization</c>，axis=-1）。</summary>
    private static string EmitLayerNorm(
        ExportContext context,
        string prefix,
        string input,
        string hint)
    {
        Tensor weight = context.State(prefix + ".weight");
        Tensor bias = context.State(prefix + ".bias");

        string scale = context.ConstantFloat(prefix + ".weight", weight.shape, TorchInterop.ToArray(weight));
        string offset = context.ConstantFloat(prefix + ".bias", bias.shape, TorchInterop.ToArray(bias));

        string output = context.Next(hint);
        context.Builder.Node(
            "LayerNormalization",
            new[] { input, scale, offset },
            new[] { output },
            OnnxAttribute.Int("axis", -1),
            OnnxAttribute.Float("epsilon", 1e-5f));

        return output;
    }

    private static string EmitFeedForward(ExportContext context, string prefix, string input)
    {
        string hidden = EmitLinear(context, prefix + ".0", input);
        hidden = EmitRelu(context, hidden, prefix + ".relu");
        return EmitLinear(context, prefix + ".2", hidden);
    }

    private static string EmitHead(ExportContext context, string prefix, int outputSize, string input)
    {
        string hidden = EmitLinear(context, prefix + ".0", input);
        hidden = EmitRelu(context, hidden, prefix + ".relu0");
        hidden = EmitLinear(context, prefix + ".2", hidden);
        hidden = EmitRelu(context, hidden, prefix + ".relu2");
        return EmitLinear(context, prefix + ".4", hidden);
    }

    /// <summary>多头注意力：q/k/v 线性投影 → 拆头 → 缩放点积 → softmax → 合并 → out_proj。</summary>
    private static string EmitAttention(
        ExportContext context,
        string prefix,
        string query,
        string key,
        string value,
        int queryTokens,
        int keyTokens)
    {
        int heads = context.Architecture.Heads;
        int headDim = SimpleDetrModel.EmbeddingSize / heads;

        string q = EmitAttentionInput(context, prefix + ".q_proj", query, queryTokens, heads, headDim);
        string k = EmitAttentionInput(context, prefix + ".k_proj", key, keyTokens, heads, headDim);
        string v = EmitAttentionInput(context, prefix + ".v_proj", value, keyTokens, heads, headDim);

        string transposedKey = context.Next(prefix + ".kT");
        context.Builder.Node("Transpose", new[] { k }, new[] { transposedKey }, OnnxAttribute.Ints("perm", 0, 1, 3, 2));

        string scores = context.Next(prefix + ".scores");
        context.Builder.Node("MatMul", new[] { q, transposedKey }, new[] { scores });

        string scale = context.ConstantFloat(
            prefix + ".scale", new long[] { 1 }, new[] { (float)(1.0 / Math.Sqrt(headDim)) });

        string scaled = context.Next(prefix + ".scaled");
        context.Builder.Node("Mul", new[] { scores, scale }, new[] { scaled });

        string weights = context.Next(prefix + ".softmax");
        context.Builder.Node("Softmax", new[] { scaled }, new[] { weights }, OnnxAttribute.Int("axis", -1));

        string attended = context.Next(prefix + ".attended");
        context.Builder.Node("MatMul", new[] { weights, v }, new[] { attended });

        string transposed = context.Next(prefix + ".transpose");
        context.Builder.Node("Transpose", new[] { attended }, new[] { transposed }, OnnxAttribute.Ints("perm", 0, 2, 1, 3));

        string merged = context.Next(prefix + ".merged");
        context.Builder.Node(
            "Reshape",
            new[]
            {
                transposed,
                context.ConstantInt64(prefix + ".shape", 1, queryTokens, SimpleDetrModel.EmbeddingSize),
            },
            new[] { merged });

        return EmitLinear(context, prefix + ".out_proj", merged);
    }

    private static string EmitAttentionInput(
        ExportContext context,
        string prefix,
        string input,
        int tokens,
        int heads,
        int headDim)
    {
        string linear = EmitLinear(context, prefix, input);

        string reshaped = context.Next(prefix + ".reshape");
        context.Builder.Node(
            "Reshape",
            new[] { linear, context.ConstantInt64(prefix + ".shape", 1, tokens, heads, headDim) },
            new[] { reshaped });

        string transposed = context.Next(prefix + ".transpose");
        context.Builder.Node("Transpose", new[] { reshaped }, new[] { transposed }, OnnxAttribute.Ints("perm", 0, 2, 1, 3));
        return transposed;
    }

    /// <summary>导出上下文：state_dict 快照 + 唯一的张量命名（initializer 与节点输出共享命名空间）。</summary>
    private sealed class ExportContext
    {
        private readonly Dictionary<string, Tensor> _state;
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);

        public ExportContext(
            OnnxGraphBuilder builder,
            IReadOnlyDictionary<string, Tensor> state,
            SimpleDetrArchitecture architecture)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(architecture);

            Builder = builder;
            Architecture = architecture;
            _state = new Dictionary<string, Tensor>(state.Count, StringComparer.Ordinal);

            foreach ((string key, Tensor tensor) in state)
                _state[key] = tensor;
        }

        public OnnxGraphBuilder Builder { get; }

        public SimpleDetrArchitecture Architecture { get; }

        /// <summary>取 state_dict 张量；缺失时报错并指出键名。</summary>
        public Tensor State(string key) =>
            _state.TryGetValue(key, out Tensor? tensor)
                ? tensor
                : throw new InvalidDataException($"state_dict 缺少 '{key}'，无法导出 ONNX。");

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
                candidate = hint + "." + (++suffix).ToString(System.Globalization.CultureInfo.InvariantCulture);

            return candidate;
        }
    }
}
