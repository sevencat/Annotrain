using ImageFacade;
using ImageFacade.Backends;
using ImageFacade.Pipeline;
using Sevencat.Train.Engine.Core;
using Sevencat.Train.Engine.Data.Geometry;
using Sevencat.Train.Engine.Data.Yolo;

namespace Sevencat.Train.Engine.Data.Images;

/// <summary>
/// 把一张 YOLO 检测样本读成「网络尺寸的 RGB 像素 + 目标框」：
/// 解码（宿主注册的 ImageFacade 后端，本项目是 SkiaSharp）→ letterbox → 翻转 / HSV → 目标框映射；
/// 另有 <see cref="LoadMosaic"/> 做 4 图拼贴。
/// <para>
/// 解码引擎来自 <see cref="ImageCodecs.Engine"/>（宿主在 exe 入口注册的进程级单例），
/// 加载器本身不持有后端；训练管线是单线程的，不要跨线程共享同一个加载器。
/// </para>
/// </summary>
public sealed class YoloSampleLoader : IDisposable
{
    /// <summary>裁剪后小于这个像素边长的框直接丢弃（与 ultralytics 的 <c>min box</c> 过滤一致）。</summary>
    private const float MinimumBoxSize = 2f;

    private bool _disposed;

    private static ImageEngine Engine => ImageCodecs.Engine;

    /// <summary>按预处理参数创建加载器（参数非法时立刻抛错）。</summary>
    public YoloSampleLoader(YoloPreprocessOptions? options = null)
    {
        Options = (options ?? new YoloPreprocessOptions()).Validate();
    }

    /// <summary>当前预处理参数。</summary>
    public YoloPreprocessOptions Options { get; }

    /// <summary>加载一条 <see cref="YoloSample"/>（图片 + 已解析的标签，含翻转 / HSV）。</summary>
    public YoloImageSample Load(YoloSample sample, Rng? rng = null)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return Load(sample.ImagePath, sample.Labels, rng);
    }

    /// <summary>按图片路径与标签加载；<paramref name="labels"/> 为空表示背景图。</summary>
    public YoloImageSample Load(string imagePath, IReadOnlyList<YoloLabel>? labels = null, Rng? rng = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        rng ??= new Rng(1);

        string fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"图像不存在：{fullPath}", fullPath);

        using FacadeImage source = Engine.Load(fullPath, new LoadOptions { AutoOrient = true });
        return BuildSample(source, fullPath, labels, rng);
    }

    /// <summary>
    /// 从已编码的图像流加载：数据集走 <c>IBlobStore</c>（远端 / 压缩包 / 数据库）时没有本地路径，
    /// 直接把字节流交给解码器；<paramref name="displayName"/> 只用于报错与日志。
    /// </summary>
    public YoloImageSample Load(
        Stream encodedImage,
        string displayName,
        IReadOnlyList<YoloLabel>? labels = null,
        Rng? rng = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(encodedImage);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        rng ??= new Rng(1);

        using FacadeImage source = Engine.Load(encodedImage, new LoadOptions { AutoOrient = true });
        return BuildSample(source, displayName, labels, rng);
    }

    /// <summary>letterbox → 目标框映射 → 翻转 / HSV。</summary>
    private YoloImageSample BuildSample(
        FacadeImage source,
        string displayPath,
        IReadOnlyList<YoloLabel>? labels,
        Rng rng)
    {
        var letterboxOptions = new LetterboxOptions
        {
            Width = Options.Width,
            Height = Options.Height,
            PadColor = new Rgba32(Options.PadValue, Options.PadValue, Options.PadValue, 255),
            Sampler = Sampler.Bilinear,
            Center = true,
        };

        using FacadeImage letterboxed = Letterbox.Apply(source, letterboxOptions, out LetterboxInfo info);
        byte[] rgb = letterboxed.CopyPixels(PixelFormat.Rgb24);

        LetterboxTransform transform = CreateTransform(info);
        var boxes = new List<YoloTargetBox>(BuildBoxes(
            labels ?? Array.Empty<YoloLabel>(),
            transform,
            displayPath));

        bool flipped = ApplyAugment(rgb, boxes, rng);

        return new YoloImageSample(
            displayPath,
            info.SourceSize.Width,
            info.SourceSize.Height,
            Options.Width,
            Options.Height,
            rgb,
            transform,
            boxes,
            flipped);
    }

    /// <summary>
    /// 4 图 mosaic：把 4 张已 letterbox 的图贴到 2 倍画布的四个象限，
    /// 再随机裁出 <see cref="YoloPreprocessOptions.Width"/>×<see cref="YoloPreprocessOptions.Height"/>。
    /// <para>
    /// 少于 4 条样本时循环复用（小数据集也能训练）；裁剪后小于 2px 的框会被丢掉，
    /// 其余框裁剪到窗口内，坐标仍是网络输入像素。
    /// </para>
    /// </summary>
    public YoloImageSample LoadMosaic(IReadOnlyList<YoloSample> samples, Rng? rng = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
            throw new ArgumentException("mosaic 至少需要 1 条样本。", nameof(samples));

        rng ??= new Rng(1);

        int width = Options.Width;
        int height = Options.Height;
        var canvas = new RgbCanvas(width * 2, height * 2, Options.PadValue);

        // 拼贴中心在画布中部 1 倍尺寸范围内抖动，保证 4 个象限都有机会露出。
        int centerX = (int)MathF.Round(rng.NextFloat(0.5f, 1.5f) * width);
        int centerY = (int)MathF.Round(rng.NextFloat(0.5f, 1.5f) * height);

        (int X, int Y)[] origins =
        {
            (centerX - width, centerY - height),
            (centerX, centerY - height),
            (centerX - width, centerY),
            (centerX, centerY),
        };

        var canvasBoxes = new List<YoloTargetBox>();

        for (int index = 0; index < origins.Length; index++)
        {
            YoloSample sample = samples[index % samples.Count];
            (string fullPath, Decoded decoded) = Decode(sample.ImagePath);

            (int offsetX, int offsetY) = origins[index];
            canvas.Blit(decoded.Rgb, width, height, offsetX, offsetY);

            LetterboxTransform transform = CreateTransform(decoded.Info);
            foreach (YoloTargetBox box in BuildBoxes(sample.Labels, transform, fullPath))
            {
                canvasBoxes.Add(new YoloTargetBox(
                    box.ClassId,
                    box.X1 + offsetX,
                    box.Y1 + offsetY,
                    box.X2 + offsetX,
                    box.Y2 + offsetY));
            }
        }

        int cropX = Math.Clamp(centerX - width / 2, 0, width);
        int cropY = Math.Clamp(centerY - height / 2, 0, height);

        byte[] cropped = canvas.Crop(cropX, cropY, width, height);

        var boxes = new List<YoloTargetBox>(canvasBoxes.Count);
        foreach (YoloTargetBox box in canvasBoxes)
        {
            float x1 = Math.Clamp(box.X1 - cropX, 0f, width);
            float y1 = Math.Clamp(box.Y1 - cropY, 0f, height);
            float x2 = Math.Clamp(box.X2 - cropX, 0f, width);
            float y2 = Math.Clamp(box.Y2 - cropY, 0f, height);

            if (x2 - x1 < MinimumBoxSize || y2 - y1 < MinimumBoxSize)
                continue;

            boxes.Add(new YoloTargetBox(box.ClassId, x1, y1, x2, y2));
        }

        bool flipped = ApplyAugment(cropped, boxes, rng);

        // 画布 → 裁剪窗口：scale = 1，pad = -crop，因此 Forward/Inverse 仍是自洽的仿射变换。
        var cropTransform = new LetterboxTransform(
            1f,
            1f,
            -cropX,
            -cropY,
            width * 2,
            height * 2,
            width,
            height);

        return new YoloImageSample(
            samples[0].ImagePath,
            width * 2,
            height * 2,
            width,
            height,
            cropped,
            cropTransform,
            boxes,
            flipped,
            isMosaic: true);
    }

    /// <summary>释放底层图像引擎。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
    }

    private (string FullPath, Decoded Decoded) Decode(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        string fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"图像不存在：{fullPath}", fullPath);

        using FacadeImage source = Engine.Load(fullPath, new LoadOptions { AutoOrient = true });

        var letterboxOptions = new LetterboxOptions
        {
            Width = Options.Width,
            Height = Options.Height,
            PadColor = new Rgba32(Options.PadValue, Options.PadValue, Options.PadValue, 255),
            Sampler = Sampler.Bilinear,
            Center = true,
        };

        using FacadeImage letterboxed = Letterbox.Apply(source, letterboxOptions, out LetterboxInfo info);
        return (fullPath, new Decoded(letterboxed.CopyPixels(PixelFormat.Rgb24), info));
    }

    private static LetterboxTransform CreateTransform(LetterboxInfo info) => new(
        info.Scale,
        info.Scale,
        info.PadLeft,
        info.PadTop,
        info.SourceSize.Width,
        info.SourceSize.Height,
        info.ModelSize.Width,
        info.ModelSize.Height);

    /// <summary>按概率做翻转 / HSV；返回是否翻转（框会同步镜像）。</summary>
    private bool ApplyAugment(byte[] rgb, List<YoloTargetBox> boxes, Rng rng)
    {
        bool flipped = Options.HorizontalFlipProbability > 0f
            && rng.NextFloat() < Options.HorizontalFlipProbability;

        if (flipped)
        {
            RgbAugment.FlipHorizontal(rgb, Options.Width, Options.Height);

            for (int index = 0; index < boxes.Count; index++)
            {
                YoloTargetBox box = boxes[index];
                boxes[index] = box with
                {
                    X1 = Options.Width - box.X2,
                    X2 = Options.Width - box.X1,
                };
            }
        }

        if (Options.HueGain > 0f || Options.SaturationGain > 0f || Options.ValueGain > 0f)
        {
            // 与 ultralytics 一致：每次增强采样一次 [-1,1] 的随机系数。
            float hueDegrees = rng.NextFloat(-1f, 1f) * Options.HueGain * 180f;
            float saturationScale = 1f + rng.NextFloat(-1f, 1f) * Options.SaturationGain;
            float valueScale = 1f + rng.NextFloat(-1f, 1f) * Options.ValueGain;

            RgbAugment.AdjustHsv(rgb, hueDegrees, saturationScale, valueScale);
        }

        return flipped;
    }

    private static IReadOnlyList<YoloTargetBox> BuildBoxes(
        IReadOnlyList<YoloLabel> labels,
        LetterboxTransform transform,
        string imagePath)
    {
        if (labels.Count == 0)
            return Array.Empty<YoloTargetBox>();

        var boxes = new List<YoloTargetBox>(labels.Count);

        foreach (YoloLabel label in labels)
        {
            if (label.Task != YoloTask.Detect)
            {
                throw new NotSupportedException(
                    $"{imagePath}：P1 只支持 detect 标签（class cx cy w h），实际是 {label.Task}。");
            }

            float pixelWidth = label.Values[2] * transform.SourceWidth;
            float pixelHeight = label.Values[3] * transform.SourceHeight;
            float pixelX = label.Values[0] * transform.SourceWidth - pixelWidth / 2f;
            float pixelY = label.Values[1] * transform.SourceHeight - pixelHeight / 2f;

            (float modelX, float modelY, float modelBoxWidth, float modelBoxHeight) =
                transform.Forward(pixelX, pixelY, pixelWidth, pixelHeight);

            boxes.Add(new YoloTargetBox(
                label.ClassId,
                modelX,
                modelY,
                modelX + modelBoxWidth,
                modelY + modelBoxHeight));
        }

        return boxes;
    }

    private sealed record Decoded(byte[] Rgb, LetterboxInfo Info);
}
