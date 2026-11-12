using System.Globalization;
using Sevencat.Train.Engine.Core;
using Sevencat.Train.Engine.IO.Onnx;
using Sevencat.Train.Engine.Models.RfDetr;
using TorchSharp;
using static TorchSharp.torch;

namespace Sevencat.Train.Engine.Export;

/// <summary>
/// 把 <see cref="DinoV2Backbone"/> 导出成 ONNX（opset 17，固定 batch = 1、固定输入尺寸）。
/// <para>
/// patch 卷积 → token 序列 + cls → 位置编码 → N 个 ViT block → 最后一层 LayerNorm。
/// GELU 在 opset 17 没有算子，按 <c>0.5·x·(1+erf(x/√2))</c> 展开。
/// </para>
/// </summary>
public static class DinoV2OnnxExporter
{
    /// <summary>构图；输出 token 序列 <c>[1, 1+hp·wp, D]</c>。</summary>
    public static OnnxModel Build(
        DinoV2Backbone backbone,
        int imageHeight,
        int imageWidth,
        string inputName = "image",
        string outputName = "tokens")
    {
        ArgumentNullException.ThrowIfNull(backbone);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputName);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputName);

        DinoV2Config config = backbone.Config;

        var builder = new OnnxGraphBuilder($"dinov2-d{config.Depth}-e{config.EmbeddingSize}");
        builder.Input(inputName, OnnxDataType.Float, 1L, config.InChannels, imageHeight, imageWidth);

        int tokenCount = AppendCore(
            builder, config, backbone.state_dict(), inputName, imageHeight, imageWidth, outputName);

        builder.Output(outputName, OnnxDataType.Float, 1L, tokenCount, config.EmbeddingSize);
        return new OnnxModel(builder.Build());
    }

    /// <summary>
    /// 把 DINOv2 主干**追加到已有图**（输入名由调用方给出），返回 token 数（含 cls）。
    /// 完整 RF-DETR 导出复用它，避免重复实现 ViT。
    /// </summary>
    public static int AppendCore(
        OnnxGraphBuilder builder,
        DinoV2Config config,
        IReadOnlyDictionary<string, Tensor> state,
        string inputName,
        int imageHeight,
        int imageWidth,
        string outputName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputName);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputName);

        if (imageHeight % config.PatchSize != 0 || imageWidth % config.PatchSize != 0)
        {
            throw new ArgumentException(
                $"输入高宽必须是 patch 尺寸 {config.PatchSize} 的倍数，实际 {imageHeight}x{imageWidth}。",
                nameof(imageHeight));
        }

        int side = imageHeight / config.PatchSize;
        int tokens = side * side + 1;
        int embedding = config.EmbeddingSize;

        if (tokens > config.TokenCount)
            throw new ArgumentException($"token 数 {tokens} 超过位置编码上限 {config.TokenCount}。", nameof(imageHeight));

        var context = new Context(builder, state);

        string patches = context.Next("patch_embed");
        builder.Node(
            "Conv",
            new[] { inputName, context.AppendWeight("patch_embed.proj.weight"), context.AppendBias("patch_embed.proj.bias") },
            new[] { patches },
            OnnxAttribute.Ints("pads", 0, 0, 0, 0),
            OnnxAttribute.Ints("strides", config.PatchSize, config.PatchSize),
            OnnxAttribute.Ints("dilations", 1, 1),
            OnnxAttribute.Int("group", 1));

        string channelLast = context.Next("patches.channel.last");
        builder.Node("Transpose", new[] { patches }, new[] { channelLast }, OnnxAttribute.Ints("perm", 0, 2, 3, 1));

        string flatPatches = context.AppendReshape(
            channelLast, "patches.flat", new long[] { 1, side * side, embedding });

        string withCls = context.Next("tokens.cls");
        builder.Node(
            "Concat",
            new[] { context.AppendTensor("cls_token", new long[] { 1, 1, embedding }), flatPatches },
            new[] { withCls },
            OnnxAttribute.Int("axis", 1));

        float[] positions = TorchInterop.ToArray(context.State("pos_embed"));
        var trimmed = new float[tokens * embedding];
        Array.Copy(positions, trimmed, trimmed.Length);

        string positioned = context.Next("tokens.positioned");
        builder.Node(
            "Add",
            new[] { withCls, context.ConstantFloat("pos_embed", new long[] { 1, tokens, embedding }, trimmed) },
            new[] { positioned });

        string current = positioned;

        for (int index = 0; index < config.Depth; index++)
            current = AppendBlock(context, index, current, config.Heads, embedding);

        string normalized = context.AppendLayerNorm("norm", current, "tokens.final");

        if (!string.Equals(normalized, outputName, StringComparison.Ordinal))
            builder.Node("Identity", new[] { normalized }, new[] { outputName });

        return tokens;
    }

    /// <summary>构图并写出文件。</summary>
    public static void Save(DinoV2Backbone backbone, string path, int imageHeight, int imageWidth)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        Build(backbone, imageHeight, imageWidth).Save(path);
    }

    private static string AppendBlock(Context context, int index, string input, int heads, int embedding)
    {
        string prefix = $"blocks.{index}";
        int headDim = embedding / heads;

        string norm1 = context.AppendLayerNorm(prefix + ".norm1", input, prefix + ".norm1.out");
        string attended = context.AppendAttention(prefix + ".attn", norm1, heads, headDim, embedding);
        string scaled1 = context.AppendLayerScale(prefix + ".ls1.gamma", attended, prefix + ".ls1");

        string sum1 = context.Next(prefix + ".sum1");
        context.Builder.Node("Add", new[] { input, scaled1 }, new[] { sum1 });

        string norm2 = context.AppendLayerNorm(prefix + ".norm2", sum1, prefix + ".norm2.out");
        string hidden = context.AppendLinear(prefix + ".mlp.fc1", norm2);
        string activated = context.AppendGelu(hidden, prefix + ".gelu");
        string projected = context.AppendLinear(prefix + ".mlp.fc2", activated);
        string scaled2 = context.AppendLayerScale(prefix + ".ls2.gamma", projected, prefix + ".ls2");

        string output = context.Next(prefix + ".out");
        context.Builder.Node("Add", new[] { sum1, scaled2 }, new[] { output });
        return output;
    }

    /// <summary>导出上下文：state_dict 快照 + 唯一命名 + 常用算子封装。</summary>
    private sealed class Context
    {
        private readonly Dictionary<string, Tensor> _state;
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);

        public Context(OnnxGraphBuilder builder, IReadOnlyDictionary<string, Tensor> state)
        {
            Builder = builder;
            _state = new Dictionary<string, Tensor>(state.Count, StringComparer.Ordinal);

            foreach ((string key, Tensor tensor) in state)
                _state[key] = tensor;
        }

        public OnnxGraphBuilder Builder { get; }

        public Tensor State(string key) =>
            _state.TryGetValue(key, out Tensor? tensor)
                ? tensor
                : throw new InvalidDataException($"state_dict 缺少 '{key}'。");

        public string Next(string hint)
        {
            string candidate = hint;
            int suffix = 0;

            while (!_names.Add(candidate))
                candidate = hint + "." + (++suffix).ToString(CultureInfo.InvariantCulture);

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

        public string AppendTensor(string key, long[] dims) =>
            ConstantFloat(key, dims, TorchInterop.ToArray(State(key)));

        public string AppendWeight(string key) => AppendTensor(key, State(key).shape);

        public string AppendBias(string key) => AppendTensor(key, State(key).shape);

        public string AppendReshape(string input, string hint, long[] shape)
        {
            string output = Next(hint);
            Builder.Node("Reshape", new[] { input, ConstantInt64(hint + ".shape", shape) }, new[] { output });
            return output;
        }

        /// <summary>Linear：权重 <c>[out,in]</c> 转置成 <c>[in,out]</c>，MatMul + Add。</summary>
        public string AppendLinear(string prefix, string input)
        {
            Tensor weight = State(prefix + ".weight");
            float[] transposed = TorchInterop.ToArray(weight.transpose(0, 1).contiguous());

            string weights = ConstantFloat(
                prefix + ".weight", new long[] { weight.shape[1], weight.shape[0] }, transposed);

            string product = Next(prefix + ".matmul");
            Builder.Node("MatMul", new[] { input, weights }, new[] { product });

            string output = Next(prefix);
            Builder.Node("Add", new[] { product, AppendBias(prefix + ".bias") }, new[] { output });
            return output;
        }

        /// <summary>LayerNorm（opset 17，axis=-1，eps=1e-6）。</summary>
        public string AppendLayerNorm(string prefix, string input, string hint)
        {
            string output = Next(hint);
            Builder.Node(
                "LayerNormalization",
                new[] { input, AppendWeight(prefix + ".weight"), AppendBias(prefix + ".bias") },
                new[] { output },
                OnnxAttribute.Int("axis", -1),
                OnnxAttribute.Float("epsilon", 1e-6f));

            return output;
        }

        /// <summary>LayerScale：乘以逐通道 gamma。</summary>
        public string AppendLayerScale(string key, string input, string hint)
        {
            string output = Next(hint);
            Builder.Node("Mul", new[] { input, AppendWeight(key) }, new[] { output });
            return output;
        }

        /// <summary>GELU（精确式）：0.5·x·(1+erf(x/√2))。</summary>
        public string AppendGelu(string input, string hint)
        {
            string scaled = Next(hint + ".scaled");
            Builder.Node(
                "Div",
                new[] { input, ConstantFloat(hint + ".sqrt2", new long[] { 1 }, new[] { 1.4142135f }) },
                new[] { scaled });

            string erf = Next(hint + ".erf");
            Builder.Node("Erf", new[] { scaled }, new[] { erf });

            string shifted = Next(hint + ".shifted");
            Builder.Node(
                "Add", new[] { erf, ConstantFloat(hint + ".one", new long[] { 1 }, new[] { 1f }) }, new[] { shifted });

            string product = Next(hint + ".product");
            Builder.Node("Mul", new[] { input, shifted }, new[] { product });

            string output = Next(hint);
            Builder.Node(
                "Mul", new[] { product, ConstantFloat(hint + ".half", new long[] { 1 }, new[] { 0.5f }) }, new[] { output });

            return output;
        }

        /// <summary>DINOv2 注意力：fused qkv → 拆 3 份 → 缩放点积 → softmax → 合并 → proj。</summary>
        public string AppendAttention(string prefix, string input, int heads, int headDim, int embedding)
        {
            string packed = AppendLinear(prefix + ".qkv", input);

            string shaped = AppendReshape(packed, prefix + ".qkv.shape", new long[] { 1, -1, 3, heads, headDim });
            string split = Next(prefix + ".split.permute");
            Builder.Node(
                "Transpose", new[] { shaped }, new[] { split }, OnnxAttribute.Ints("perm", 2, 0, 3, 1, 4));

            string q = AppendSqueeze(AppendSlice(split, prefix + ".q", 0), prefix + ".q.flat");
            string k = AppendSqueeze(AppendSlice(split, prefix + ".k", 1), prefix + ".k.flat");
            string v = AppendSqueeze(AppendSlice(split, prefix + ".v", 2), prefix + ".v.flat");

            string kt = Next(prefix + ".kt");
            Builder.Node("Transpose", new[] { k }, new[] { kt }, OnnxAttribute.Ints("perm", 0, 1, 3, 2));

            string scores = Next(prefix + ".scores");
            Builder.Node("MatMul", new[] { q, kt }, new[] { scores });

            string scaled = Next(prefix + ".scaled");
            Builder.Node(
                "Div",
                new[] { scores, ConstantFloat(prefix + ".scale", new long[] { 1 }, new[] { MathF.Sqrt(headDim) }) },
                new[] { scaled });

            string weights = Next(prefix + ".softmax");
            Builder.Node("Softmax", new[] { scaled }, new[] { weights }, OnnxAttribute.Int("axis", -1));

            string attended = Next(prefix + ".attended");
            Builder.Node("MatMul", new[] { weights, v }, new[] { attended });

            string merged = Next(prefix + ".merged.permute");
            Builder.Node(
                "Transpose", new[] { attended }, new[] { merged }, OnnxAttribute.Ints("perm", 0, 2, 1, 3));

            string flat = AppendReshape(merged, prefix + ".merged", new long[] { 1, -1, embedding });
            return AppendLinear(prefix + ".proj", flat);
        }

        private string AppendSlice(string input, string hint, long start)
        {
            string output = Next(hint);
            Builder.Node(
                "Slice",
                new[]
                {
                    input,
                    ConstantInt64(hint + ".starts", start),
                    ConstantInt64(hint + ".ends", start + 1),
                    ConstantInt64(hint + ".axes", 0),
                },
                new[] { output });

            return output;
        }

        /// <summary>Squeeze：去掉长度为 1 的轴（opset 13+ 的 axes 是输入，不是属性）。</summary>
        private string AppendSqueeze(string input, string hint)
        {
            string output = Next(hint);
            Builder.Node(
                "Squeeze",
                new[] { input, ConstantInt64(hint + ".axes", 0) },
                new[] { output });

            return output;
        }
    }
}
