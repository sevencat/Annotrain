using System.Globalization;
using Sevencat.Train.Engine.Core;
using Sevencat.Train.Engine.IO.Onnx;
using Sevencat.Train.Engine.Models.RfDetr;
using TorchSharp;
using static TorchSharp.torch;

namespace Sevencat.Train.Engine.Export;

/// <summary>
/// 把 <see cref="MultiScaleDeformableAttention"/> 导出成 ONNX 子图，采样用 opset 16 的
/// <c>GridSample</c>（bilinear / zeros / align_corners=0），与 C# 侧 <c>grid_sample</c> 语义一致。
/// <para>
/// 固定 batch = 1（RF-DETR 逐图推理）；输入 <c>query</c>、<c>reference</c>、<c>level{i}</c>，
/// 输出 <c>output</c>。这是 RF-DETR 完整导出中唯一的非标准算子部分。
/// </para>
/// </summary>
public static class DeformAttnOnnxExporter
{
    /// <summary>按层实例与各尺度特征图尺寸构图。</summary>
    public static OnnxModel Build(
        MultiScaleDeformableAttention attention,
        int queries,
        IReadOnlyList<(int Height, int Width)> levelShapes,
        int embeddingSize,
        string outputName = "output")
    {
        ArgumentNullException.ThrowIfNull(attention);
        ArgumentNullException.ThrowIfNull(levelShapes);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputName);

        if (queries <= 0)
            throw new ArgumentOutOfRangeException(nameof(queries), queries, "query 数必须大于 0。");

        int levels = attention.Levels;
        int points = attention.Points;
        int headDim = attention.HeadDim;
        int heads = embeddingSize / headDim;

        if (levelShapes.Count != levels)
        {
            throw new ArgumentException(
                $"需要 {levels} 个尺度形状，实际 {levelShapes.Count} 个。",
                nameof(levelShapes));
        }

        IReadOnlyDictionary<string, Tensor> state = attention.state_dict();
        var builder = new OnnxGraphBuilder("msdeformattn");

        builder.Input("query", OnnxDataType.Float, 1L, queries, embeddingSize);
        builder.Input("reference", OnnxDataType.Float, 1L, queries, levels, 2);

        var levelInputs = new string[levels];
        for (int level = 0; level < levels; level++)
        {
            (int height, int width) = levelShapes[level];
            levelInputs[level] = $"level{level}";
            builder.Input(levelInputs[level], OnnxDataType.Float, 1L, embeddingSize, height, width);
        }

        Append(
            builder,
            state,
            "query",
            "reference",
            levelInputs,
            queries,
            heads,
            points,
            levelShapes,
            embeddingSize,
            outputName);

        builder.Output(outputName, OnnxDataType.Float, 1L, queries, embeddingSize);
        return new OnnxModel(builder.Build(), opsetVersion: 16);
    }

    /// <summary>
    /// 把可变形注意力子图**追加到已有图**里（输入名由调用方给出），返回输出张量名。
    /// 完整 RF-DETR 导出复用它，避免重复实现。
    /// </summary>
    public static string Append(
        OnnxGraphBuilder builder,
        IReadOnlyDictionary<string, Tensor> state,
        string queryInput,
        string referenceInput,
        IReadOnlyList<string> levelInputs,
        int queries,
        int heads,
        int points,
        IReadOnlyList<(int Height, int Width)> levelShapes,
        int embeddingSize,
        string outputName,
        string scope = "")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(levelInputs);
        ArgumentNullException.ThrowIfNull(levelShapes);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryInput);
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceInput);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputName);

        int levels = levelShapes.Count;
        if (levelInputs.Count != levels)
            throw new ArgumentException($"levelInputs 需要 {levels} 项，实际 {levelInputs.Count} 项。", nameof(levelInputs));

        int embedding = embeddingSize;
        int headDim = embedding / heads;

        var context = new Context(builder, state, scope);

        string offsetTensor = context.AppendReshape(
            context.AppendLinear("sampling_offsets", queryInput),
            "offsets",
            new long[] { 1, queries, heads, levels, points, 2 });

        string weightTensor = context.AppendReshape(
            context.AppendSoftmax(
                context.AppendReshape(
                    context.AppendLinear("attention_weights", queryInput),
                    "weights.flat",
                    new long[] { 1, queries, heads, levels * points }),
                "weights"),
            "weights.levels",
            new long[] { 1, queries, heads, levels, points });

        string? accumulated = null;

        for (int level = 0; level < levels; level++)
        {
            accumulated = AppendLevel(
                context,
                level,
                levelInputs[level],
                referenceInput,
                levelShapes[level].Height,
                levelShapes[level].Width,
                queries,
                heads,
                headDim,
                points,
                embedding,
                offsetTensor,
                weightTensor,
                accumulated);
        }

        string headsFirst = context.Next("merged.transpose");
        builder.Node(
            "Transpose", new[] { accumulated! }, new[] { headsFirst }, OnnxAttribute.Ints("perm", 0, 3, 2, 1));

        string merged = context.AppendReshape(headsFirst, "merged", new long[] { 1, queries, embedding });
        string projected = context.AppendLinear("output_proj", merged);

        if (!string.Equals(projected, outputName, StringComparison.Ordinal))
            builder.Node("Identity", new[] { projected }, new[] { outputName });

        return outputName;
    }

    /// <summary>构图并写出文件。</summary>
    public static void Save(
        MultiScaleDeformableAttention attention,
        string path,
        int queries,
        IReadOnlyList<(int Height, int Width)> levelShapes,
        int embeddingSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        Build(attention, queries, levelShapes, embeddingSize).Save(path);
    }

    /// <summary>单个尺度的采样 + 加权求和，返回累计张量 <c>[1,heads,headDim,queries]</c>。</summary>
    private static string AppendLevel(
        Context context,
        int level,
        string levelInput,
        string referenceInput,
        int height,
        int width,
        int queries,
        int heads,
        int headDim,
        int points,
        int embeddingSize,
        string offsetTensor,
        string weightTensor,
        string? accumulated)
    {
        string prefix = $"level{level}";

        // C# 是 flatten(2).transpose(1,2)（即 [B,D,H,W] → [B,HW,D] 的真转置），
        // ONNX 侧必须 Transpose(0,2,3,1) 再 Reshape，不能直接 Reshape。
        string channelLast = context.Next(prefix + ".channel.last");
        context.Builder.Node(
            "Transpose", new[] { levelInput }, new[] { channelLast }, OnnxAttribute.Ints("perm", 0, 2, 3, 1));

        string flat = context.AppendReshape(
            channelLast, prefix + ".flat", new long[] { 1, height * width, embeddingSize });

        string projected = context.AppendLinear("value_proj", flat);

        string channelsFirst = context.Next(prefix + ".channels.first");
        context.Builder.Node(
            "Transpose", new[] { projected }, new[] { channelsFirst }, OnnxAttribute.Ints("perm", 0, 2, 1));

        string value = context.AppendReshape(
            channelsFirst, prefix + ".value", new long[] { heads, headDim, height, width });

        string levelOffset = context.AppendReshape(
            context.AppendSlice(offsetTensor, prefix + ".offset", axis: 3, start: level, end: level + 1),
            prefix + ".offset.flat",
            new long[] { 1, queries, heads, 1, points, 2 });

        string reference = context.AppendReshape(
            context.AppendSlice(referenceInput, prefix + ".reference", axis: 2, start: level, end: level + 1),
            prefix + ".reference.flat",
            new long[] { 1, queries, 1, 1, 1, 2 });

        string normalizer = context.ConstantFloat(
            prefix + ".normalizer", new long[] { 1, 1, 1, 1, 1, 2 }, new[] { (float)width, (float)height });

        string divided = context.Next(prefix + ".offset.scaled");
        context.Builder.Node("Div", new[] { levelOffset, normalizer }, new[] { divided });

        string located = context.Next(prefix + ".located");
        context.Builder.Node("Add", new[] { reference, divided }, new[] { located });

        string doubled = context.Next(prefix + ".doubled");
        context.Builder.Node(
            "Mul",
            new[] { located, context.ConstantFloat(prefix + ".two", new long[] { 1 }, new[] { 2f }) },
            new[] { doubled });

        string shifted = context.Next(prefix + ".grid.flat");
        context.Builder.Node(
            "Sub",
            new[] { doubled, context.ConstantFloat(prefix + ".one", new long[] { 1 }, new[] { 1f }) },
            new[] { shifted });

        // 注意：不能直接 Reshape 成 [heads,queries,points,2]，那样只重排内存不换轴；
        // 必须与 C# 的 squeeze(3) + permute(0,2,1,3,4) 等价。
        string squeezed = context.AppendReshape(
            shifted, prefix + ".grid.squeezed", new long[] { 1, queries, heads, points, 2 });

        string ordered = context.Next(prefix + ".grid.ordered");
        context.Builder.Node(
            "Transpose",
            new[] { squeezed },
            new[] { ordered },
            OnnxAttribute.Ints("perm", 0, 2, 1, 3, 4));

        string grid = context.AppendReshape(
            ordered, prefix + ".grid", new long[] { heads, queries, points, 2 });

        string sampled = context.Next(prefix + ".sampled");
        context.Builder.Node(
            "GridSample",
            new[] { value, grid },
            new[] { sampled },
            OnnxAttribute.String("mode", "bilinear"),
            OnnxAttribute.String("padding_mode", "zeros"),
            OnnxAttribute.Int("align_corners", 0));

        string sampledTensor = context.AppendReshape(
            sampled, prefix + ".sampled.flat", new long[] { 1, heads, headDim, queries, points });

        string levelWeights = context.AppendReshape(
            context.AppendSlice(weightTensor, prefix + ".weights", axis: 3, start: level, end: level + 1),
            prefix + ".weights.flat",
            new long[] { 1, queries, heads, 1, points });

        string permutedWeights = context.Next(prefix + ".weights.permuted");
        context.Builder.Node(
            "Transpose",
            new[] { levelWeights },
            new[] { permutedWeights },
            OnnxAttribute.Ints("perm", 0, 2, 3, 1, 4));

        string weighted = context.Next(prefix + ".weighted");
        context.Builder.Node("Mul", new[] { sampledTensor, permutedWeights }, new[] { weighted });

        string reduced = context.Next(prefix + ".reduced");
        context.Builder.Node(
            "ReduceSum",
            new[] { weighted, context.ConstantInt64(prefix + ".axes", 4) },
            new[] { reduced },
            OnnxAttribute.Int("keepdims", 0));

        if (accumulated is null)
            return reduced;

        string sum = context.Next(prefix + ".accumulated");
        context.Builder.Node("Add", new[] { accumulated, reduced }, new[] { sum });
        return sum;
    }

    /// <summary>导出上下文：state_dict 快照 + 唯一命名（initializer 与节点输出共享命名空间）。</summary>
    private sealed class Context
    {
        private readonly Dictionary<string, Tensor> _state;
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);
        private readonly string _scope;

        public Context(OnnxGraphBuilder builder, IReadOnlyDictionary<string, Tensor> state, string scope = "")
        {
            Builder = builder;
            _scope = scope;
            _state = new Dictionary<string, Tensor>(state.Count, StringComparer.Ordinal);

            foreach ((string key, Tensor tensor) in state)
                _state[key] = tensor;
        }

        public OnnxGraphBuilder Builder { get; }

        public string Next(string hint)
        {
            string candidate = _scope + hint;
            int suffix = 0;

            while (!_names.Add(candidate))
                candidate = _scope + hint + "." + (++suffix).ToString(CultureInfo.InvariantCulture);

            return candidate;
        }

        public string ConstantFloat(string hint, long[] dims, float[] values)
        {
            string name = Next(hint);
            Builder.Initializer(OnnxTensor.FromFloat32(name, dims, values));
            return name;
        }

        public string ConstantInt64(string hint, params long[] values)
        {
            string name = Next(hint);
            Builder.Initializer(OnnxTensor.FromInt64(name, new long[] { values.Length }, values));
            return name;
        }

        /// <summary>Linear：权重 <c>[out,in]</c> 转置成 <c>[in,out]</c>，MatMul + Add。</summary>
        public string AppendLinear(string prefix, string input)
        {
            if (!_state.TryGetValue(prefix + ".weight", out Tensor? weight)
                || !_state.TryGetValue(prefix + ".bias", out Tensor? bias))
            {
                throw new InvalidDataException($"state_dict 缺少 '{prefix}.weight/.bias'。");
            }

            float[] transposed = TorchInterop.ToArray(weight.transpose(0, 1).contiguous());
            string weights = ConstantFloat(
                prefix + ".weight", new long[] { weight.shape[1], weight.shape[0] }, transposed);

            string product = Next(prefix + ".matmul");
            Builder.Node("MatMul", new[] { input, weights }, new[] { product });

            string biases = ConstantFloat(
                prefix + ".bias", new long[] { bias.shape[0] }, TorchInterop.ToArray(bias));

            string output = Next(prefix);
            Builder.Node("Add", new[] { product, biases }, new[] { output });
            return output;
        }

        public string AppendReshape(string input, string hint, long[] shape)
        {
            string output = Next(hint);
            Builder.Node("Reshape", new[] { input, ConstantInt64(hint + ".shape", shape) }, new[] { output });
            return output;
        }

        public string AppendSoftmax(string input, string hint)
        {
            string output = Next(hint);
            Builder.Node("Softmax", new[] { input }, new[] { output }, OnnxAttribute.Int("axis", -1));
            return output;
        }

        public string AppendSlice(string input, string hint, int axis, long start, long end)
        {
            string output = Next(hint);
            Builder.Node(
                "Slice",
                new[]
                {
                    input,
                    ConstantInt64(hint + ".starts", start),
                    ConstantInt64(hint + ".ends", end),
                    ConstantInt64(hint + ".axes", axis),
                },
                new[] { output });

            return output;
        }
    }
}
