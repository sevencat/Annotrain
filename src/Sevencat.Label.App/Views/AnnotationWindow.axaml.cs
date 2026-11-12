using Avalonia.Controls;
using Avalonia.Interactivity;
using Sevencat.Core.Infrastructure;
using Sevencat.Label.App.ViewModels;

namespace Sevencat.Label.App.Views;

/// <summary>
/// 单图标注对话框：界面与交互照 TinyLabel 的标注窗口，加载/保存在
/// <see cref="AnnotationViewModel"/> 里完成，这里只负责挂画布、适窗和释放图片句柄。
/// 「保存并继续」不关窗口，直接把画布换成队列里的下一张（<see cref="AnnotationTarget"/>）。
/// </summary>
public partial class AnnotationWindow : Window
{
    private readonly AnnotationViewModel? _viewModel;

    /// <summary>窗口关掉后主界面据此把列表落回最后标的那张。</summary>
    public long? LastImageId => _viewModel?.LastImageId;

    /// <summary>XAML 预览用。</summary>
    public AnnotationWindow()
    {
        InitializeComponent();
    }

    /// <summary>从图片清单打开（主界面用）：<paramref name="targets"/> 是这一轮要连续标的图，第一个就是起点。</summary>
    public AnnotationWindow(
        long projectId,
        IReadOnlyList<AnnotationTarget> targets,
        IReadOnlyList<string> classes,
        AppSettings settings,
        SettingsStore settingsStore) : this()
    {
        _viewModel = new AnnotationViewModel(
            projectId,
            targets,
            classes,
            settings,
            settingsStore);

        _viewModel.CloseRequested = outcome => Close(outcome);
        DataContext = _viewModel;
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (_viewModel is null)
        {
            return;
        }

        // 画布是自绘控件，历史栈与选中都在控件里：挂上之后再加载图片
        _viewModel.Attach(Canvas, this);
        await _viewModel.InitializeAsync();
        Canvas.FitToWindow();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // Bitmap 会占着原图文件，窗口关掉就释放
        _viewModel?.Shutdown();
    }

    private void OnFitToWindow(object? sender, RoutedEventArgs e) => Canvas.FitToWindow();

    private void OnClose(object? sender, RoutedEventArgs e) => Close(AnnotationOutcome.Cancelled);
}
