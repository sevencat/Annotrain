using System.Globalization;

namespace Sevencat.Train.Engine.Models.Yolo;

/// <summary>YOLO 官方五种规模（n / s / m / l / x），v8 与 v11 共用同一套字母。</summary>
public enum YoloScale
{
    /// <summary>n（nano）。</summary>
    N,

    /// <summary>s（small）。</summary>
    S,

    /// <summary>m（medium）。</summary>
    M,

    /// <summary>l（large）。</summary>
    L,

    /// <summary>x（extra large）。</summary>
    X,
}

/// <summary>YOLO 模型家族：v8 与 v11 的层级结构 / 缩放系数 / 头部卷积都不同。</summary>
public enum YoloModelFamily
{
    /// <summary>YOLOv8（C2f + 常规 3×3 检测头）。</summary>
    V8,

    /// <summary>YOLOv11（C3k2 + C2PSA + 深度可分离分类头）。</summary>
    V11,
}

/// <summary>
/// 规模缩放系数：通道按 <c>ceil(min(通道, MaxChannels) * Width / 8) * 8</c> 取整，
/// 层重复次数按 <c>max(round(n * Depth), 1)</c> 取整，与 ultralytics 的 <c>make_divisible</c> / <c>parse_model</c> 一致。
/// </summary>
public readonly record struct YoloScaleConfig(double Depth, double Width, int MaxChannels);

/// <summary>规模字母与缩放系数的解析 / 展示（数值来自 ultralytics 的 <c>yolov8.yaml</c> / <c>yolo11.yaml</c>）。</summary>
public static class YoloScales
{
    /// <summary>YOLOv8 的 architecture 前缀。</summary>
    public const string V8Prefix = "yolov8";

    /// <summary>YOLOv11 的 architecture 前缀。</summary>
    public const string V11Prefix = "yolo11";

    /// <summary>取规模的官方字母（n / s / m / l / x）。</summary>
    public static char ToLetter(YoloScale scale) => scale switch
    {
        YoloScale.N => 'n',
        YoloScale.S => 's',
        YoloScale.M => 'm',
        YoloScale.L => 'l',
        YoloScale.X => 'x',
        _ => throw new ArgumentOutOfRangeException(nameof(scale), scale, "未知的 YOLO 规模。"),
    };

    /// <summary>解析规模字母（大小写不敏感）。</summary>
    public static YoloScale Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return value.Trim().ToLowerInvariant() switch
        {
            "n" or "nano" => YoloScale.N,
            "s" or "small" => YoloScale.S,
            "m" or "medium" => YoloScale.M,
            "l" or "large" => YoloScale.L,
            "x" or "xlarge" => YoloScale.X,
            _ => throw new ArgumentException(
                $"未知的 YOLO 规模 '{value}'；可选 n / s / m / l / x。", nameof(value)),
        };
    }

    /// <summary>取缩放系数；v8 与 v11 的 depth / width / max_channels 表不同，必须按家族查。</summary>
    public static YoloScaleConfig Config(YoloModelFamily family, YoloScale scale) => family switch
    {
        YoloModelFamily.V8 => scale switch
        {
            YoloScale.N => new YoloScaleConfig(0.33, 0.25, 1024),
            YoloScale.S => new YoloScaleConfig(0.33, 0.50, 1024),
            YoloScale.M => new YoloScaleConfig(0.67, 0.75, 1024),
            YoloScale.L => new YoloScaleConfig(1.00, 1.00, 1024),
            YoloScale.X => new YoloScaleConfig(1.00, 1.25, 1024),
            _ => throw new ArgumentOutOfRangeException(nameof(scale), scale, "未知的 YOLOv8 规模。"),
        },
        YoloModelFamily.V11 => scale switch
        {
            YoloScale.N => new YoloScaleConfig(0.50, 0.25, 1024),
            YoloScale.S => new YoloScaleConfig(0.50, 0.50, 1024),
            YoloScale.M => new YoloScaleConfig(0.50, 1.00, 512),
            YoloScale.L => new YoloScaleConfig(1.00, 1.00, 512),
            YoloScale.X => new YoloScaleConfig(1.00, 1.50, 512),
            _ => throw new ArgumentOutOfRangeException(nameof(scale), scale, "未知的 YOLOv11 规模。"),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, "未知的 YOLO 家族。"),
    };

    /// <summary>家族对应的 architecture 前缀。</summary>
    public static string Prefix(YoloModelFamily family) => family switch
    {
        YoloModelFamily.V8 => V8Prefix,
        YoloModelFamily.V11 => V11Prefix,
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, "未知的 YOLO 家族。"),
    };

    /// <summary>解析家族（<c>yolov8</c> / <c>v8</c> / <c>yolo11</c> / <c>yolov11</c> / <c>v11</c>）。</summary>
    public static YoloModelFamily ParseFamily(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return value.Trim().ToLowerInvariant() switch
        {
            "v8" or "yolov8" or "yolo8" => YoloModelFamily.V8,
            "v11" or "yolo11" or "yolov11" => YoloModelFamily.V11,
            _ => throw new ArgumentException(
                $"未知的 YOLO 家族 '{value}'；可选 v8 / v11。", nameof(value)),
        };
    }

    /// <summary>把 <c>min(通道, MaxChannels) * Width</c> 按 8 取整（ultralytics 的 <c>make_divisible</c>）。</summary>
    public static int ScaledChannels(int channels, YoloScaleConfig config)
    {
        double scaled = Math.Min(channels, config.MaxChannels) * config.Width;
        return (int)(Math.Ceiling(scaled / 8.0) * 8);
    }

    /// <summary>按 depth 缩放层重复次数，至少 1 层。</summary>
    public static int ScaledRepeats(int repeats, YoloScaleConfig config) =>
        Math.Max((int)Math.Round(repeats * config.Depth, MidpointRounding.AwayFromZero), 1);

    /// <summary>把规模字母拼到字符串（日志 / 文件命名用）。</summary>
    public static string Describe(YoloModelFamily family, YoloScale scale, int classCount) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix(family)}{ToLetter(scale)}-detect-{classCount}");
}
