using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Sevencat.Core.AutoLabel;

/// <summary>AI 标注推理器的抽象：输入 BGRA 像素，输出原图坐标的候选框。</summary>
public interface IAutoLabelRunner : IDisposable
{
    IReadOnlyList<AutoLabelCandidate> Predict(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        AutoLabelOptions options);
}

/// <summary>
/// YOLO（Ultralytics 导出的 <c>.onnx</c>）推理：letterbox → 归一化 CHW → 推理 → 解码 + NMS。
/// 只支持 v8/v11 那套「4 + 类别数」输出；v5 的 objectness 列没有处理。
/// </summary>
public sealed class YoloOnnxRunner : IAutoLabelRunner
{
    private const float PadValue = 114f / 255f;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _inputWidth;
    private readonly int _inputHeight;

    public YoloOnnxRunner(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("找不到模型文件", modelPath);
        }

        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

        _session = new InferenceSession(modelPath, sessionOptions);
        _inputName = _session.InputMetadata.Keys.First();

        int[] dimensions = _session.InputMetadata[_inputName].Dimensions;
        _inputHeight = dimensions.Length == 4 && dimensions[2] > 0 ? dimensions[2] : 640;
        _inputWidth = dimensions.Length == 4 && dimensions[3] > 0 ? dimensions[3] : 640;
    }

    public IReadOnlyList<AutoLabelCandidate> Predict(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        AutoLabelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
        {
            return [];
        }

        double scale = Math.Min((double)_inputWidth / width, (double)_inputHeight / height);
        int scaledWidth = Math.Max(1, (int)Math.Round(width * scale));
        int scaledHeight = Math.Max(1, (int)Math.Round(height * scale));
        int padX = (_inputWidth - scaledWidth) / 2;
        int padY = (_inputHeight - scaledHeight) / 2;

        float[] input = BuildInput(bgra, width, height, scaledWidth, scaledHeight, padX, padY);

        var tensor = new DenseTensor<float>(input, [1, 3, _inputHeight, _inputWidth]);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
            _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);

        int[] dimensions = [.. results[0].AsTensor<float>().Dimensions];

        if (dimensions.Length != 3)
        {
            return [];
        }

        int second = dimensions[1];
        int third = dimensions[2];

        bool channelFirst = second < third;
        int channels = channelFirst ? second : third;
        int count = channelFirst ? third : second;

        float[] buffer = [.. results[0].AsTensor<float>()];
        var decodeOptions = new YoloDecodeOptions(
            options.Confidence,
            options.IouThreshold,
            scale,
            padX,
            padY,
            width,
            height);

        return YoloOutputDecoder.Decode(
            buffer,
            channels,
            count,
            channelFirst,
            decodeOptions,
            options.ClassNames,
            options.ClassMap);
    }

    /// <summary>letterbox + 最近邻缩放 + 归一化，输出 CHW 的 0..1 浮点。</summary>
    private float[] BuildInput(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        int scaledWidth,
        int scaledHeight,
        int padX,
        int padY)
    {
        int plane = _inputWidth * _inputHeight;
        float[] input = new float[plane * 3];
        input.AsSpan().Fill(PadValue);

        for (int y = 0; y < scaledHeight; y++)
        {
            int sourceY = Math.Min(height - 1, y * height / scaledHeight);
            int sourceRow = sourceY * width;
            int targetRow = ((y + padY) * _inputWidth) + padX;

            for (int x = 0; x < scaledWidth; x++)
            {
                int sourceX = Math.Min(width - 1, x * width / scaledWidth);
                int sourceIndex = ((sourceRow + sourceX) * 4);
                int target = targetRow + x;

                input[target] = bgra[sourceIndex + 2] / 255f;
                input[plane + target] = bgra[sourceIndex + 1] / 255f;
                input[(2 * plane) + target] = bgra[sourceIndex] / 255f;
            }
        }

        return input;
    }

    public void Dispose() => _session.Dispose();
}
