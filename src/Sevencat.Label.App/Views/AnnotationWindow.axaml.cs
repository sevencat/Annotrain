using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Sevencat.Core.Annotation;
using Sevencat.Core.AutoLabel;
using Sevencat.Core.Infrastructure;
using Sevencat.Core.Model;
using Sevencat.Core.Storage;
using Sevencat.Label.App.Controls;
using Sevencat.Label.App.ViewModels;

namespace Sevencat.Label.App.Views;

/// <summary>
/// 单图标注对话框：画矩形框、AI 预标注、保存回 <c>annotations.DocJson</c>。
/// 参考 TinyLabel 的「列表 → 点进一张 → 标完关闭 / 保存并继续」。
/// </summary>
public partial class AnnotationWindow : Window
{
    private readonly DatabaseAnnotationStore _store;
    private readonly IBlobStore _blobStore;
    private readonly ProjectImage _projectImage;
    private readonly ImageRef _image;
    private readonly IReadOnlyList<string> _classes;
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;

    private Bitmap? _bitmap;
    private IAutoLabelRunner? _runner;
    private string _runnerModel = string.Empty;

    /// <summary>XAML 预览用。</summary>
    public AnnotationWindow()
    {
        InitializeComponent();

        _store = null!;
        _blobStore = null!;
        _projectImage = null!;
        _image = null!;
        _classes = [];
        _settings = new AppSettings();
        _settingsStore = null!;
    }

    public AnnotationWindow(
        string databasePath,
        long projectId,
        ProjectImage image,
        IReadOnlyList<string> classes,
        IBlobStore blobStore,
        AppSettings settings,
        SettingsStore settingsStore) : this()
    {
        _store = new DatabaseAnnotationStore(databasePath, projectId);
        _blobStore = blobStore;
        _projectImage = image;
        _image = image.Sample.Image;
        _classes = classes;
        _settings = settings;
        _settingsStore = settingsStore;

        FileText.Text = image.FileName;

        string[] labels = classes.Count > 0 ? [.. classes] : ["object"];
        LabelBox.ItemsSource = labels;
        LabelBox.SelectedIndex = 0;

        Board.ActiveLabelProvider = () => string.IsNullOrWhiteSpace(LabelBox.Text)
            ? labels[0]
            : LabelBox.Text.Trim();

        Board.ShapesChanged += (_, _) => UpdateHint();
        Board.Tool = CanvasTool.Select;
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            await using Stream input = await _blobStore
                .OpenReadAsync(new StorageKey(_image.Location, _image.RelativePath))
                .ConfigureAwait(true);

            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer).ConfigureAwait(true);
            buffer.Position = 0;

            _bitmap = new Bitmap(buffer);
            Board.Source = _bitmap;

            AnnotationEnvelope? envelope = await _store.LoadAsync(_image).ConfigureAwait(true);
            Board.SetShapes(envelope?.Document.Shapes ?? []);

            UpdateHint();
        }
        catch (Exception error)
        {
            HintText.Text = $"加载失败：{error.Message}";
        }
    }

    private void UpdateHint()
    {
        string model = string.IsNullOrWhiteSpace(_settings.AutoLabelModelPath)
            ? "未设置 AI 模型"
            : Path.GetFileName(_settings.AutoLabelModelPath);

        HintText.Text = $"{_image.RelativePath} · {_projectImage.Sample.Width}×{_projectImage.Sample.Height} · " +
                        $"{Board.Shapes.Count} 个框 · 左键拖拽画框，选中后 Delete 删除 · AI 模型：{model}";
    }

    // ── 工具 ──────────────────────────────────────────────────────────────────

    private void OnSelectTool(object? sender, RoutedEventArgs e)
    {
        Board.Tool = CanvasTool.Select;
        SelectTool.IsChecked = true;
        RectTool.IsChecked = false;
    }

    private void OnRectTool(object? sender, RoutedEventArgs e)
    {
        Board.Tool = CanvasTool.Rectangle;
        RectTool.IsChecked = true;
        SelectTool.IsChecked = false;
    }

    private void OnFit(object? sender, RoutedEventArgs e) => Board.FitToWindow();

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (!Board.DeleteSelected())
        {
            HintText.Text = "先点选一个框，再删除";
        }
    }

    // ── AI 标注 ───────────────────────────────────────────────────────────────

    private async void OnAutoLabelOptions(object? sender, RoutedEventArgs e)
    {
        var dialog = new AutoLabelOptionsWindow(_settings.AutoLabelModelPath, _settings.AutoLabelConfidence);

        if (!await dialog.ShowDialog<bool>(this) || dialog.Result is null)
        {
            return;
        }

        _settings.AutoLabelModelPath = dialog.Result.ModelPath;
        _settings.AutoLabelConfidence = dialog.Result.Confidence;
        SaveSettings();

        UpdateHint();
    }

    private async void OnAutoLabel(object? sender, RoutedEventArgs e)
    {
        if (_bitmap is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.AutoLabelModelPath))
        {
            HintText.Text = "先点「AI 设置」选一个 .onnx 模型";
            return;
        }

        HintText.Text = "AI 推理中…";

        try
        {
            IAutoLabelRunner runner = GetRunner(_settings.AutoLabelModelPath);
            byte[] bgra = ToBgra(_bitmap, out int width, out int height);

            var options = new AutoLabelOptions(
                _settings.AutoLabelModelPath,
                _settings.AutoLabelConfidence,
                ClassNames: _classes.Count > 0 ? _classes : null);

            IReadOnlyList<AutoLabelCandidate> candidates =
                await Task.Run(() => runner.Predict(bgra, width, height, options)).ConfigureAwait(true);

            Board.SetCandidates(candidates);
            CandidateBar.IsVisible = candidates.Count > 0;
            CandidateText.Text = $"AI 候选 {candidates.Count} 个";

            HintText.Text = candidates.Count == 0
                ? "没有候选：调低置信度再试，或确认模型类别与项目类别一致"
                : "候选是黄色虚线框，确认后点「接受全部」或「只接受 ≥0.5」";
        }
        catch (Exception error)
        {
            _runner?.Dispose();
            _runner = null;
            _runnerModel = string.Empty;
            CandidateBar.IsVisible = false;
            HintText.Text = $"AI 标注失败：{error.Message}";
        }
    }

    private void OnAcceptAll(object? sender, RoutedEventArgs e) => AcceptCandidates(0);

    private void OnAcceptConfident(object? sender, RoutedEventArgs e) => AcceptCandidates(0.5);

    private void AcceptCandidates(double minScore)
    {
        int accepted = Board.AcceptCandidates(minScore);

        CandidateBar.IsVisible = false;
        UpdateHint();
        HintText.Text = $"接受 {accepted} 个 AI 框，记得保存";
    }

    private void OnDiscardCandidates(object? sender, RoutedEventArgs e)
    {
        Board.ClearCandidates();
        CandidateBar.IsVisible = false;
        UpdateHint();
    }

    private IAutoLabelRunner GetRunner(string modelPath)
    {
        if (_runner is not null && string.Equals(_runnerModel, modelPath, StringComparison.OrdinalIgnoreCase))
        {
            return _runner;
        }

        _runner?.Dispose();
        _runner = new YoloOnnxRunner(modelPath);
        _runnerModel = modelPath;
        return _runner;
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            HintText.Text = $"设置保存失败：{error.Message}";
        }
    }

    /// <summary>Avalonia 的 <see cref="Bitmap"/> 复制成 BGRA 字节，交给 Core 做推理前处理。</summary>
    private static byte[] ToBgra(Bitmap bitmap, out int width, out int height)
    {
        width = (int)bitmap.PixelSize.Width;
        height = (int)bitmap.PixelSize.Height;

        int stride = width * 4;
        byte[] bytes = new byte[stride * height];

        GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);

        try
        {
            bitmap.CopyPixels(new Avalonia.PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), bytes.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        return bytes;
    }

    // ── 保存 ──────────────────────────────────────────────────────────────────

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (await SaveAsync().ConfigureAwait(true))
        {
            Close(AnnotationOutcome.Saved);
        }
    }

    private async void OnSaveAndNext(object? sender, RoutedEventArgs e)
    {
        if (await SaveAsync().ConfigureAwait(true))
        {
            Close(AnnotationOutcome.SavedAndContinue);
        }
    }

    private async Task<bool> SaveAsync()
    {
        try
        {
            var document = new AnnotationDocument
            {
                ImagePath = _image.RelativePath,
                ImageWidth = _projectImage.Sample.Width,
                ImageHeight = _projectImage.Sample.Height,
                Shapes = Board.Shapes,
                Meta = new DocumentMeta
                {
                    Id = IdGenerator.NewId(),
                    Revision = 1,
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
            };

            await _store.SaveAsync(_image, document).ConfigureAwait(true);
            return true;
        }
        catch (Exception error)
        {
            HintText.Text = $"保存失败：{error.Message}";
            return false;
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(AnnotationOutcome.Cancelled);

    protected override void OnClosed(EventArgs e)
    {
        _runner?.Dispose();
        _store?.Dispose();
        base.OnClosed(e);
    }
}
