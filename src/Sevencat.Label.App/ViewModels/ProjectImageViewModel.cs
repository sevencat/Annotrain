using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Localization;
using Sevencat.Core.Model;
using Sevencat.Core.Storage;

namespace Sevencat.Label.App.ViewModels;

/// <summary>
/// 右侧图片清单的一行：缩略图 / 路径 / 标注 / 划分 / 标注人 / 标注时间 / 版本 / 备注 / 操作。
/// 数据直接来自库里的 <c>images</c> 行，不再经过 Data 层的样本装载。
/// 缩略图来自项目的 <c>thumbs/</c> 目录，由 <see cref="MainWindowViewModel"/> 后台补齐后回调 <see cref="SetThumbnail"/>。
/// </summary>
public sealed partial class ProjectImageViewModel(TinyLabelImageRow row) : ObservableObject, IDisposable
{
    private static Localizer Loc => Localizer.Instance;

    /// <summary>列表里缩略图的显示宽度；解码按 2 倍取，给高分屏用。</summary>
    private const int ThumbnailDecodeWidth = 96;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    public TinyLabelImageRow Row { get; } = row;

    public long Id => Row.Id;

    /// <summary>路径列：项目内相对路径（扫描模式是相对扫描根）。</summary>
    public string DisplayPath => Row.RelPath;

    public string FileName => Row.FileName;

    public string RelativePath => Row.RelPath;

    /// <summary>标注列：有标注显示「已标注」，否则「未标注」。</summary>
    public string BoxText => Loc.Get(Row.State == "pending" ? "State.Pending" : "State.Labeled");

    /// <summary>状态列：库里 <c>images.State</c> 的本地化名字。</summary>
    public string StateText => Loc.Get(Row.State switch
    {
        "pending" => "State.Pending",
        "labeled" => "State.Labeled",
        "reviewed" => "State.Reviewed",
        "excluded" => "State.Excluded",
        "skipped" => "State.Skipped",
        _ => "State.Pending",
    });

    /// <summary>是否还没标注：列表与「保存并继续」都按它挑下一张。</summary>
    public bool IsPending => string.Equals(Row.State, "pending", StringComparison.OrdinalIgnoreCase);

    /// <summary>标注列：有状态就算有标注数据（勾），未标注显示横线。</summary>
    public bool HasAnnotation => !IsPending;

    /// <summary>划分列：train / val / test 用不同图标区分，未划分给个淡灰问号。</summary>
    public MaterialIconKind SplitIcon => NormalizedSplit switch
    {
        "train" => MaterialIconKind.School,
        // 验证集不用勾：清单里「标注」列已经是绿勾，会混
        "val" or "valid" => MaterialIconKind.ShieldCheck,
        "test" => MaterialIconKind.TestTube,
        _ => MaterialIconKind.HelpCircleOutline,
    };

    /// <summary>划分图标颜色：训练蓝、验证绿、测试橙、未划分灰。</summary>
    public IBrush SplitColor => NormalizedSplit switch
    {
        "train" => Solid(0x25, 0x63, 0xEB),
        "val" or "valid" => Solid(0x16, 0xA3, 0x4A),
        "test" => Solid(0xD9, 0x77, 0x06),
        _ => Solid(0x9C, 0xA3, 0xAF),
    };

    /// <summary>划分图标提示：训练集 / 验证集 / 测试集 / 未划分。</summary>
    public string SplitTip => Loc.Get(NormalizedSplit switch
    {
        "train" => "Label.SplitTrainTip",
        "val" or "valid" => "Label.SplitValTip",
        "test" => "Label.SplitTestTip",
        _ => "Label.SplitNoneTip",
    });

    /// <summary>最后标注人；没标注过显示破折号。</summary>
    public string AnnotatorText => string.IsNullOrWhiteSpace(Row.AnnotatorName)
        ? Loc.Get("State.Empty")
        : Row.AnnotatorName;

    /// <summary>最后标注时间（库里存 UTC，这里转本地）；没标注过显示破折号。</summary>
    public string AnnotatedAtText => Row.AnnotatedAt is { } at
        ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : Loc.Get("State.Empty");

    private string NormalizedSplit => Row.Split?.Trim().ToLowerInvariant() ?? string.Empty;

    private static SolidColorBrush Solid(byte red, byte green, byte blue)
        => new(Color.FromRgb(red, green, blue));

    public string RevisionText => $"rev {Row.Revision}";

    public string RemarkText => string.IsNullOrWhiteSpace(Row.Remark) ? Loc.Get("State.Empty") : Row.Remark;

    public string SplitText => string.IsNullOrWhiteSpace(Row.Split) ? Loc.Get("State.NoSplit") : Row.Split;

    public string SizeText => Row.SizeText;

    /// <summary>标注窗口用：图片引用（本地绝对路径 → 目录 + 文件名）。</summary>
    public ImageRef Image
    {
        get
        {
            string? directory = Path.GetDirectoryName(Row.FilePath);

            return new ImageRef(
                Path.GetFileName(Row.FilePath),
                StorageLocation.FromPath(string.IsNullOrEmpty(directory) ? "." : directory));
        }
    }

    /// <summary>标注窗口用：尺寸（读不到时退回 0，保存时以库里的宽高为准）。</summary>
    public int Width => Row.Width;

    public int Height => Row.Height;

    /// <summary>把 <c>thumbs/</c> 里的文件读成位图；文件不在或解码失败就保持无图。</summary>
    public void SetThumbnail(string? thumbnailPath)
    {
        Bitmap? previous = Thumbnail;

        if (string.IsNullOrWhiteSpace(thumbnailPath) || !File.Exists(thumbnailPath))
        {
            Thumbnail = null;
            previous?.Dispose();
            return;
        }

        try
        {
            using FileStream stream = File.OpenRead(thumbnailPath);
            Thumbnail = Bitmap.DecodeToWidth(stream, ThumbnailDecodeWidth);
            previous?.Dispose();
        }
        catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException)
        {
            Thumbnail = null;
            previous?.Dispose();
        }
    }

    public void Dispose()
    {
        Thumbnail?.Dispose();
        Thumbnail = null;
    }
}
