using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sevencat.Core.Annotation;
using Sevencat.Core.AutoLabel;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Infrastructure;
using Sevencat.Core.Localization;
using Sevencat.Core.Model;
using Sevencat.Core.Storage;
using Sevencat.Label.App.Controls;
using Sevencat.Label.App.Views;

namespace Sevencat.Label.App.ViewModels;

/// <summary>
/// 标注窗口的视图模型：图片加载/保存、形状列表、AI 预标注，界面按 TinyLabel 标注窗口那套排。
/// 画布是自绘控件，历史栈在控件里，所以这里持有一个 <see cref="AnnotationCanvas"/> 引用做转发。
/// </summary>
public sealed partial class AnnotationViewModel : ObservableObject
{
    private readonly DatabaseAnnotationStore _store;
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly IReadOnlyList<AnnotationTarget> _targets;

    private AnnotationCanvas? _canvas;
    private Window? _owner;

    private IBlobStore _blobStore;
    private ImageRef _image;
    private string _displayName = string.Empty;
    private int _imageWidth;
    private int _imageHeight;
    private int _current;

    private Bitmap? _bitmap;
    private IAutoLabelRunner? _runner;
    private string _runnerModel = string.Empty;
    private bool _dirty;
    private bool _refreshingShapes;

    /// <summary>XAML 预览用。</summary>
    public AnnotationViewModel()
    {
        _store = null!;
        _settings = new AppSettings();
        _settingsStore = null!;
        _targets = [];
        _blobStore = null!;
        _image = null!;
    }

    public AnnotationViewModel(
        TinyLabelDatabase database,
        long projectId,
        IReadOnlyList<AnnotationTarget> targets,
        IReadOnlyList<string> classes,
        AppSettings settings,
        SettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentOutOfRangeException.ThrowIfZero(targets.Count);

        _store = new DatabaseAnnotationStore(database, projectId);
        _settings = settings;
        _settingsStore = settingsStore;
        _targets = targets;
        _blobStore = targets[0].Store;
        _image = targets[0].Image;
        _displayName = targets[0].Row.FileName;
        _imageWidth = targets[0].Row.Width;
        _imageHeight = targets[0].Row.Height;

        LabelNames = classes.Count > 0 ? [.. classes] : ["object"];
        ActiveLabel = LabelNames[0];
        Title = $"{Localizer.Instance.Get("Annotate.Title")} · {_displayName}";
        AutoLabelSetupText = Localizer.Instance.Get("Annotate.AISettings");
    }

    /// <summary>窗口关掉后主界面据此把列表落回最后标的那张。</summary>
    public long? LastImageId => _targets.Count == 0 ? null : _targets[Math.Min(_current, _targets.Count - 1)].Row.Id;

    public ObservableCollection<string> LabelNames { get; } = ["object"];

    public ObservableCollection<AnnotationShapeRow> Shapes { get; } = [];

    public ObservableCollection<AiPendingBoxRow> PendingBoxes { get; } = [];

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _autoLabelSetupText = string.Empty;

    [ObservableProperty]
    private Bitmap? _currentImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectTool))]
    [NotifyPropertyChangedFor(nameof(IsRectangleTool))]
    private CanvasTool _tool = CanvasTool.Select;

    [ObservableProperty]
    private string _activeLabel = "object";

    [ObservableProperty]
    private AnnotationShapeRow? _selectedShape;

    [ObservableProperty]
    private string? _selectedShapeLabel;

    [ObservableProperty]
    private bool _hasSelectedShape;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private string _dirtyText = string.Empty;

    [ObservableProperty]
    private string _shapeCountText = string.Empty;

    [ObservableProperty]
    private string _pendingSummary = string.Empty;

    [ObservableProperty]
    private bool _hasPendingBoxes;

    public bool IsSelectTool => Tool == CanvasTool.Select;

    public bool IsRectangleTool => Tool == CanvasTool.Rectangle;

    /// <summary>窗口把画布和自身交进来：之后所有历史/选择/AI 操作都转发到画布。</summary>
    public void Attach(AnnotationCanvas canvas, Window owner)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(owner);

        _canvas = canvas;
        _owner = owner;

        canvas.ActiveLabelProvider = () => string.IsNullOrWhiteSpace(ActiveLabel) ? "object" : ActiveLabel.Trim();
        canvas.ShapesChanged += (_, _) =>
        {
            RefreshShapes();
            MarkDirty();
        };
        canvas.SelectionChanged += (_, _) => SyncSelection();
        canvas.HistoryChanged += (_, _) =>
        {
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        };
        canvas.CandidatesChanged += (_, _) => RefreshPending();
    }

    public async Task InitializeAsync()
    {
        await LoadAsync().ConfigureAwait(true);
    }

    partial void OnSelectedShapeChanged(AnnotationShapeRow? value)
    {
        if (_canvas is null)
        {
            return;
        }

        if (value is null)
        {
            _canvas.SelectShape(null);
            SelectedShapeLabel = null;
            HasSelectedShape = false;
            return;
        }

        _canvas.SelectShape(value.Shape);
        SelectedShapeLabel = value.Shape.Label;
        HasSelectedShape = true;
    }

    partial void OnSelectedShapeLabelChanged(string? value)
    {
        if (_canvas is null || SelectedShape is null || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (_canvas.ChangeLabel(SelectedShape.Shape, value))
        {
            RefreshShapes();
        }
    }

    partial void OnToolChanged(CanvasTool value)
    {
        if (_canvas is not null)
        {
            _canvas.Tool = value;
        }
    }

    // ── 加载 ──────────────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        if (_canvas is null)
        {
            return;
        }

        // 换图前先让画布松开旧位图，再释放
        CurrentImage = null;
        _bitmap?.Dispose();
        _bitmap = null;

        _canvas.ClearCandidates();
        _canvas.SetShapes([]);

        try
        {
            await using Stream input = await _blobStore
                .OpenReadAsync(new StorageKey(_image.Location, _image.RelativePath))
                .ConfigureAwait(true);

            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer).ConfigureAwait(true);
            buffer.Position = 0;

            _bitmap = new Bitmap(buffer);
            CurrentImage = _bitmap;
            Title = $"{Localizer.Instance.Get("Annotate.Title")} · {_displayName}";

            AnnotationEnvelope? envelope = await _store.LoadAsync(_image).ConfigureAwait(true);
            _canvas.SetShapes(envelope?.Document.Shapes ?? []);

            _canvas.FitToWindow();
            RefreshShapes();
            RefreshPending();
            _dirty = false;
            UpdateDirtyText();

            Status = Localizer.Instance.Format(
                "Annotate.Hint",
                _displayName,
                _imageWidth,
                _imageHeight,
                Shapes.Count,
                ModelText());
        }
        catch (Exception error)
        {
            Status = Localizer.Instance.Format("Annotate.LoadFailed", error.Message);
        }
    }

    private string ModelText()
    {
        Localizer loc = Localizer.Instance;

        return string.IsNullOrWhiteSpace(_settings.AutoLabelModelPath)
            ? loc.Get("Annotate.NoModel")
            : Path.GetFileName(_settings.AutoLabelModelPath);
    }

    private void MarkDirty()
    {
        _dirty = true;
        UpdateDirtyText();
    }

    private void UpdateDirtyText()
        => DirtyText = _dirty ? Localizer.Instance.Get("Annotate.Dirty") : string.Empty;

    private void RefreshShapes()
    {
        // 重建列表会让 ListBox 改 SelectedItem，进而回调 SyncSelection 再进这里：加个守卫防重入
        if (_canvas is null || _refreshingShapes)
        {
            return;
        }

        _refreshingShapes = true;

        try
        {
            RebuildShapes();
        }
        finally
        {
            _refreshingShapes = false;
        }
    }

    private void RebuildShapes()
    {
        IReadOnlyList<Shape> shapes = _canvas!.Shapes;
        Shapes.Clear();

        for (int index = 0; index < shapes.Count; index++)
        {
            Shapes.Add(new AnnotationShapeRow(index + 1, shapes[index]));
        }

        // 以画布上的选中为准：拖动/撤销后形状是新实例，不能拿旧行去比
        Shape? current = _canvas.Selected;
        AnnotationShapeRow? selected = current is null
            ? null
            : Shapes.FirstOrDefault(row => ReferenceEquals(row.Shape, current));

        SelectedShape = selected;
        HasSelectedShape = selected is not null;
        SelectedShapeLabel = selected?.Shape.Label;
        ShapeCountText = Localizer.Instance.Format("Annotate.ShapeCount", shapes.Count);
    }

    private void SyncSelection() => RefreshShapes();

    private void RefreshPending()
    {
        IReadOnlyList<AutoLabelCandidate> candidates = _canvas?.Candidates ?? [];
        PendingBoxes.Clear();

        foreach (AutoLabelCandidate candidate in candidates)
        {
            PendingBoxes.Add(new AiPendingBoxRow(candidate, this));
        }

        HasPendingBoxes = PendingBoxes.Count > 0;
        PendingSummary = HasPendingBoxes
            ? Localizer.Instance.Format("Annotate.Candidates", PendingBoxes.Count)
            : string.Empty;
    }

    // ── 工具 / 历史 ───────────────────────────────────────────────────────────

    [RelayCommand]
    private void SetTool(string? tool)
        => Tool = string.Equals(tool, "rect", StringComparison.OrdinalIgnoreCase)
            ? CanvasTool.Rectangle
            : CanvasTool.Select;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        _canvas?.Undo();
        RefreshShapes();
    }

    private bool CanUndo() => _canvas?.CanUndo == true;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        _canvas?.Redo();
        RefreshShapes();
    }

    private bool CanRedo() => _canvas?.CanRedo == true;

    [RelayCommand]
    private void DeleteShape()
    {
        if (_canvas?.DeleteSelected() == true)
        {
            RefreshShapes();
            return;
        }

        Status = Localizer.Instance.Get("Annotate.DeleteHint");
    }

    // ── AI 预标注 ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SetupAutoLabelAsync()
    {
        if (_owner is null)
        {
            return;
        }

        var dialog = new AutoLabelOptionsWindow(_settings.AutoLabelModelPath, _settings.AutoLabelConfidence);

        if (!await dialog.ShowDialog<bool>(_owner) || dialog.Result is null)
        {
            return;
        }

        _settings.AutoLabelModelPath = dialog.Result.ModelPath;
        _settings.AutoLabelConfidence = dialog.Result.Confidence;
        SaveSettings();
        Status = Localizer.Instance.Format("Annotate.ModelSet", ModelText());
    }

    [RelayCommand]
    private async Task AutoLabelAsync()
    {
        if (_canvas is null || _bitmap is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.AutoLabelModelPath))
        {
            Status = Localizer.Instance.Get("Annotate.AINoModel");
            return;
        }

        Status = Localizer.Instance.Get("Annotate.AIRunning");

        try
        {
            IAutoLabelRunner runner = GetRunner(_settings.AutoLabelModelPath);
            byte[] bgra = ToBgra(_bitmap, out int width, out int height);

            var options = new AutoLabelOptions(
                _settings.AutoLabelModelPath,
                _settings.AutoLabelConfidence,
                ClassNames: LabelNames.Count > 0 ? [.. LabelNames] : null);

            IReadOnlyList<AutoLabelCandidate> candidates =
                await Task.Run(() => runner.Predict(bgra, width, height, options)).ConfigureAwait(true);

            _canvas.SetCandidates(candidates);
            RefreshPending();

            Status = Localizer.Instance.Get(candidates.Count == 0
                ? "Annotate.NoCandidatesHint"
                : "Annotate.CandidateHint");
        }
        catch (Exception error)
        {
            _runner?.Dispose();
            _runner = null;
            _runnerModel = string.Empty;
            _canvas.ClearCandidates();
            RefreshPending();
            Status = Localizer.Instance.Format("Annotate.AIFailed", error.Message);
        }
    }

    [RelayCommand]
    private void AcceptAllPending() => AcceptPending(0);

    [RelayCommand]
    private void AcceptHighConfidencePending() => AcceptPending(0.5);

    private void AcceptPending(double minScore)
    {
        if (_canvas is null)
        {
            return;
        }

        int accepted = _canvas.AcceptCandidates(minScore);
        RefreshShapes();
        RefreshPending();
        Status = Localizer.Instance.Format("Annotate.Accepted", accepted);
    }

    [RelayCommand]
    private void DiscardAllPending()
    {
        _canvas?.ClearCandidates();
        RefreshPending();
        Status = Localizer.Instance.Get("Annotate.DiscardedHint");
    }

    /// <summary>逐条接受/丢掉：列表行按钮调这里。</summary>
    internal void AcceptPending(AutoLabelCandidate candidate)
    {
        if (_canvas?.AcceptCandidate(candidate) != true)
        {
            return;
        }

        RefreshShapes();
        RefreshPending();
        Status = Localizer.Instance.Format("Annotate.AcceptedOne", candidate.Label, candidate.ScoreText);
    }

    internal void RejectPending(AutoLabelCandidate candidate)
    {
        if (_canvas?.RejectCandidate(candidate) != true)
        {
            return;
        }

        RefreshPending();
        Status = Localizer.Instance.Format("Annotate.RejectedOne", candidate.Label, candidate.ScoreText);
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
            Status = Localizer.Instance.Format("Annotate.SettingsSaveFailed", error.Message);
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

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (await SaveDocumentAsync().ConfigureAwait(true))
        {
            RequestClose(AnnotationOutcome.Saved);
        }
    }

    /// <summary>保存后不关窗：队列里还有待标注的就地换成下一张。</summary>
    [RelayCommand]
    private async Task SaveAndContinueAsync()
    {
        if (!await SaveDocumentAsync().ConfigureAwait(true))
        {
            return;
        }

        if (_current + 1 >= _targets.Count)
        {
            Status = Localizer.Instance.Get("Annotate.NoMorePending");
            return;
        }

        _current++;
        AnnotationTarget target = _targets[_current];

        _blobStore = target.Store;
        _image = target.Image;
        _displayName = target.Row.FileName;
        _imageWidth = target.Row.Width;
        _imageHeight = target.Row.Height;

        SelectedShape = null;
        HasSelectedShape = false;
        SelectedShapeLabel = null;

        await LoadAsync().ConfigureAwait(true);
        Status = Localizer.Instance.Format("Annotate.MovedTo", _displayName);
    }

    private async Task<bool> SaveDocumentAsync()
    {
        try
        {
            var document = new AnnotationDocument
            {
                ImagePath = _image.RelativePath,
                ImageWidth = _imageWidth,
                ImageHeight = _imageHeight,
                Shapes = _canvas?.Shapes ?? [],
                Meta = new DocumentMeta
                {
                    Id = IdGenerator.NewId(),
                    Revision = 1,
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
            };

            await _store.SaveAsync(_image, document).ConfigureAwait(true);

            _dirty = false;
            UpdateDirtyText();
            Status = Localizer.Instance.Get("Annotate.Saved");
            return true;
        }
        catch (Exception error)
        {
            Status = Localizer.Instance.Format("Annotate.SaveFailed", error.Message);
            return false;
        }
    }

    /// <summary>窗口在 Attach 时挂上自己的 Close，VM 不直接依赖 Window.Close 的结果码。</summary>
    public Action<AnnotationOutcome>? CloseRequested { get; set; }

    private void RequestClose(AnnotationOutcome outcome) => CloseRequested?.Invoke(outcome);

    public void Shutdown()
    {
        CurrentImage = null;
        _bitmap?.Dispose();
        _bitmap = null;
        _runner?.Dispose();
        _runner = null;
        _store?.Dispose();
    }
}

/// <summary>右侧形状列表的一行：序号 + 形状。</summary>
public sealed class AnnotationShapeRow(int index, Shape shape)
{
    public int Index { get; } = index;

    public Shape Shape { get; } = shape;

    public string Summary => string.IsNullOrEmpty(Shape.Label)
        ? $"#{Index} {Shape.Type}"
        : $"#{Index} {Shape.Label}";
}

/// <summary>AI 候选列表的一行：类别、分数 + 接受/丢掉。</summary>
public sealed partial class AiPendingBoxRow(AutoLabelCandidate candidate, AnnotationViewModel owner)
{
    public AutoLabelCandidate Candidate { get; } = candidate;

    public string Label => Candidate.Label;

    public string ScoreText => Candidate.ScoreText;

    public bool IsHighConfidence => Candidate.Score >= 0.5;

    [RelayCommand]
    private void Accept() => owner.AcceptPending(Candidate);

    [RelayCommand]
    private void Reject() => owner.RejectPending(Candidate);
}
