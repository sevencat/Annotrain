using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Sevencat.Core.Annotation;
using Sevencat.Core.AutoLabel;
using Sevencat.Core.Model;

namespace Sevencat.Label.App.Controls;

public enum CanvasTool
{
    Select,
    Rectangle,
}

/// <summary>
/// 精简标注画布：画矩形框、选中/拖动/删除、滚轮缩放、适应窗口。
/// 交互参考 TinyLabel 的 <c>AnnotationCanvas</c>，但只保留矩形这一条最常用路径。
/// 形状仍是 <see cref="Shape"/>（不可变 record），每次改动都替换实例。
/// </summary>
public sealed class AnnotationCanvas : Control
{
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<AnnotationCanvas, IImage?>(nameof(Source));

    /// <summary>画布底色：浅色（图片外边留白，别用深色抢眼）。</summary>
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<AnnotationCanvas, IBrush?>(
            nameof(Background),
            new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF7)));

    private readonly List<Shape> _shapes = [];
    private readonly List<AutoLabelCandidate> _candidates = [];

    /// <summary>撤销/重做栈：每条是「改动前的形状快照」，形状本身不可变，直接存列表副本。</summary>
    private readonly List<List<Shape>> _undo = [];
    private readonly List<List<Shape>> _redo = [];

    private double _zoom = 1;
    private Point _origin;
    private bool _userAdjusted;

    private Shape? _selected;

    private bool _creating;
    private PointD _createStart;
    private PointD _createCurrent;

    private bool _moving;
    private int _moveIndex = -1;
    private Shape? _moveOriginal;
    private PointD _moveStart;

    static AnnotationCanvas()
    {
        AffectsRender<AnnotationCanvas>(SourceProperty, BackgroundProperty);
        FocusableProperty.OverrideDefaultValue<AnnotationCanvas>(true);
    }

    public AnnotationCanvas()
    {
        ClipToBounds = true;

        // 窗口首次布局完成或改变大小时保持「适应窗口」，用户滚轮缩放过就不再打扰
        SizeChanged += (_, _) =>
        {
            if (!_userAdjusted)
            {
                FitToWindow();
            }
        };
    }

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>画布底色。</summary>
    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>当前全部框（原图像素坐标）。每次读取返回副本，改动用 <see cref="SetShapes"/>。</summary>
    public IReadOnlyList<Shape> Shapes => [.. _shapes];

    public Shape? Selected => _selected;

    public CanvasTool Tool { get; set; } = CanvasTool.Select;

    public string ActiveLabel { get; set; } = "object";

    /// <summary>放框时向宿主取当前标签（宿主可直接读输入框，避免来回同步）。</summary>
    public Func<string>? ActiveLabelProvider { get; set; }

    public event EventHandler? ShapesChanged;

    /// <summary>选中形状变化（点选、拖动、删除、撤销后）。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>撤销/重做可用性变化。</summary>
    public event EventHandler? HistoryChanged;

    /// <summary>候选框集合变化（重新推理、接受、逐条丢弃）。</summary>
    public event EventHandler? CandidatesChanged;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public void SetShapes(IEnumerable<Shape> shapes)
    {
        ArgumentNullException.ThrowIfNull(shapes);

        _shapes.Clear();
        _shapes.AddRange(shapes);
        _undo.Clear();
        _redo.Clear();
        SetSelected(null);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>把某个形状设为当前选中（列表里点一行也会走这里）。</summary>
    public void SelectShape(Shape? shape)
    {
        if (shape is not null && !_shapes.Contains(shape))
        {
            return;
        }

        SetSelected(shape);
        InvalidateVisual();
    }

    /// <summary>改选中形状的类别；返回是否改动。</summary>
    public bool ChangeLabel(Shape shape, string label)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(label);

        int index = _shapes.IndexOf(shape);
        if (index < 0 || string.Equals(shape.Label, label, StringComparison.Ordinal))
        {
            return false;
        }

        PushHistory();
        Shape updated = shape with { Label = label };
        _shapes[index] = updated;

        if (ReferenceEquals(_selected, shape))
        {
            SetSelected(updated);
        }

        ShapesChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    // ── 撤销 / 重做 ───────────────────────────────────────────────────────────

    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        _redo.Add([.. _shapes]);
        Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
        {
            return false;
        }

        _undo.Add([.. _shapes]);
        Restore(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Restore(List<Shape> snapshot)
    {
        _shapes.Clear();
        _shapes.AddRange(snapshot);
        SetSelected(null);
        ShapesChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void PushHistory()
    {
        _undo.Add([.. _shapes]);

        if (_undo.Count > 64)
        {
            _undo.RemoveAt(0);
        }

        _redo.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetSelected(Shape? shape)
    {
        if (ReferenceEquals(_selected, shape))
        {
            return;
        }

        _selected = shape;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── AI 候选 ───────────────────────────────────────────────────────────────

    /// <summary>AI 候选框（虚线显示，未落库）。</summary>
    public IReadOnlyList<AutoLabelCandidate> Candidates => [.. _candidates];

    public void SetCandidates(IEnumerable<AutoLabelCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        _candidates.Clear();
        _candidates.AddRange(candidates);
        CandidatesChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void ClearCandidates()
    {
        if (_candidates.Count == 0)
        {
            return;
        }

        _candidates.Clear();
        CandidatesChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>接受一条候选（画布上双击或右侧列表点勾）：转成正式框并记一次撤销。</summary>
    public bool AcceptCandidate(AutoLabelCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        int index = IndexOfCandidate(candidate);
        if (index < 0)
        {
            return false;
        }

        PushHistory();

        _shapes.Add(new Shape
        {
            Id = IdGenerator.NewId(),
            Type = ShapeType.Rectangle,
            Points = BoxConvert.ToCorners(candidate.Box),
            Label = candidate.Label,
        });

        _candidates.RemoveAt(index);
        ShapesChanged?.Invoke(this, EventArgs.Empty);
        CandidatesChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>丢掉一条候选（画布上 Delete 或右侧列表点叉）。</summary>
    public bool RejectCandidate(AutoLabelCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        int index = IndexOfCandidate(candidate);
        if (index < 0)
        {
            return false;
        }

        _candidates.RemoveAt(index);
        CandidatesChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    private int IndexOfCandidate(AutoLabelCandidate candidate)
    {
        for (int index = 0; index < _candidates.Count; index++)
        {
            if (ReferenceEquals(_candidates[index], candidate) || _candidates[index] == candidate)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>把候选按分数过滤后转成正式框，返回接受的条数。</summary>
    public int AcceptCandidates(double minScore = 0)
    {
        if (_candidates.Count == 0)
        {
            return 0;
        }

        PushHistory();

        int accepted = 0;

        foreach (AutoLabelCandidate candidate in _candidates)
        {
            if (candidate.Score < minScore)
            {
                continue;
            }

            _shapes.Add(new Shape
            {
                Id = IdGenerator.NewId(),
                Type = ShapeType.Rectangle,
                Points = BoxConvert.ToCorners(candidate.Box),
                Label = candidate.Label,
            });

            accepted++;
        }

        _candidates.Clear();

        if (accepted > 0)
        {
            ShapesChanged?.Invoke(this, EventArgs.Empty);
        }

        CandidatesChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return accepted;
    }

    public bool DeleteSelected()
    {
        if (_selected is null)
        {
            return false;
        }

        PushHistory();
        _shapes.Remove(_selected);
        SetSelected(null);
        ShapesChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>把图片按控件大小居中显示。</summary>
    public void FitToWindow()
    {
        if (Source is not { } image)
        {
            return;
        }

        Size size = image.Size;
        double width = Bounds.Width;
        double height = Bounds.Height;

        if (size.Width <= 0 || size.Height <= 0 || width <= 1 || height <= 1)
        {
            return;
        }

        _zoom = Math.Max(0.01, Math.Min(width / size.Width, height / size.Height) * 0.97);
        _origin = new Point(
            (width - (size.Width * _zoom)) / 2,
            (height - (size.Height * _zoom)) / 2);

        _userAdjusted = false;
        InvalidateVisual();
    }

    // ── 渲染 ──────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Background ?? Brushes.Transparent, bounds);

        if (Source is not { } image)
        {
            return;
        }

        var destination = new Rect(
            _origin,
            new Size(image.Size.Width * _zoom, image.Size.Height * _zoom));

        context.DrawImage(image, new Rect(image.Size), destination);

        foreach (Shape shape in _shapes)
        {
            DrawShape(context, shape);
        }

        foreach (AutoLabelCandidate candidate in _candidates)
        {
            DrawCandidate(context, candidate);
        }

        if (_creating)
        {
            RectD preview = Normalize(_createStart, _createCurrent);

            if (preview.Width > 0 || preview.Height > 0)
            {
                context.DrawRectangle(
                    new SolidColorBrush(LabelPalette.ColorFor(ActiveLabel), 0.15),
                    new Pen(LabelPalette.BrushFor(ActiveLabel), 1.5, new DashStyle([4, 3], 0)),
                    ToScreen(preview));
            }
        }
    }

    private void DrawCandidate(DrawingContext context, AutoLabelCandidate candidate)
    {
        Rect screen = ToScreen(candidate.Box);
        var brush = new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15));

        context.DrawRectangle(
            new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15), 0.08),
            new Pen(brush, 1.5, new DashStyle([4, 3], 0)),
            screen);

        var text = new FormattedText(
            $"{candidate.Label} {candidate.ScoreText}",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            11,
            Brushes.Black);

        var tag = new Rect(screen.X, Math.Max(0, screen.Y - 15), text.Width + 6, 15);
        context.FillRectangle(brush, tag);
        context.DrawText(text, new Point(tag.X + 3, tag.Y + 1));
    }

    private void DrawShape(DrawingContext context, Shape shape)
    {
        Rect screen = ToScreen(shape.Bounds);
        bool selected = ReferenceEquals(shape, _selected);
        IBrush brush = LabelPalette.BrushFor(shape.Label);

        if (selected)
        {
            context.DrawRectangle(new SolidColorBrush(Colors.Black, 0.06), null, screen);
        }

        context.DrawRectangle(null, new Pen(brush, selected ? 3 : 2), screen);

        if (string.IsNullOrEmpty(shape.Label))
        {
            return;
        }

        var text = new FormattedText(
            shape.Label,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            12,
            Brushes.White);
        var tag = new Rect(screen.X, Math.Max(0, screen.Y - 17), text.Width + 8, 17);

        context.FillRectangle(brush, tag);
        context.DrawText(text, new Point(tag.X + 4, tag.Y + 2));
    }

    // ── 交互 ──────────────────────────────────────────────────────────────────

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceProperty)
        {
            SetSelected(null);
            FitToWindow();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (Source is null)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        PointD imagePoint = ToImage(point.Position);

        if (Tool == CanvasTool.Rectangle)
        {
            ActiveLabel = ResolveLabel();
            PushHistory();
            _creating = true;
            _createStart = ClampToImage(imagePoint);
            _createCurrent = _createStart;
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        Shape? hit = HitTest(imagePoint);
        SetSelected(hit);
        InvalidateVisual();

        if (hit is null)
        {
            return;
        }

        PushHistory();
        _moving = true;
        _moveIndex = _shapes.IndexOf(hit);
        _moveOriginal = hit;
        _moveStart = imagePoint;

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_creating && !_moving)
        {
            return;
        }

        PointD point = ToImage(e.GetPosition(this));

        if (_creating)
        {
            _createCurrent = ClampToImage(point);
            InvalidateVisual();
            return;
        }

        if (_moveOriginal is null || _moveIndex < 0 || _moveIndex >= _shapes.Count)
        {
            return;
        }

        Shape moved = _moveOriginal.Translate(point.X - _moveStart.X, point.Y - _moveStart.Y);
        _shapes[_moveIndex] = moved;
        SetSelected(moved);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_creating)
        {
            _creating = false;
            e.Pointer.Capture(null);

            RectD rect = Normalize(_createStart, _createCurrent);

            if (rect.Width >= 3 && rect.Height >= 3)
            {
                var shape = new Shape
                {
                    Id = IdGenerator.NewId(),
                    Type = ShapeType.Rectangle,
                    Points = BoxConvert.ToCorners(rect),
                    Label = ActiveLabel,
                };

                _shapes.Add(shape);
                SetSelected(shape);
                ShapesChanged?.Invoke(this, EventArgs.Empty);
            }

            InvalidateVisual();
            return;
        }

        if (_moving)
        {
            _moving = false;
            _moveIndex = -1;
            _moveOriginal = null;
            e.Pointer.Capture(null);
            ShapesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (Source is null)
        {
            return;
        }

        Point position = e.GetPosition(this);
        PointD before = ToImage(position);

        _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), 0.02, 64);
        _origin = new Point(
            position.X - (before.X * _zoom),
            position.Y - (before.Y * _zoom));

        _userAdjusted = true;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Delete && DeleteSelected())
        {
            e.Handled = true;
            return;
        }

        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (control && e.Key == Key.Z)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                Redo();
            }
            else
            {
                Undo();
            }

            e.Handled = true;
            return;
        }

        if (control && e.Key == Key.Y)
        {
            Redo();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F)
        {
            FitToWindow();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    // ── 坐标 ──────────────────────────────────────────────────────────────────

    private string ResolveLabel()
        => ActiveLabelProvider?.Invoke() is { Length: > 0 } value ? value : ActiveLabel;

    private PointD ToImage(Point point)
        => new((point.X - _origin.X) / _zoom, (point.Y - _origin.Y) / _zoom);

    private Rect ToScreen(RectD rect) => new(
        _origin.X + (rect.X * _zoom),
        _origin.Y + (rect.Y * _zoom),
        rect.Width * _zoom,
        rect.Height * _zoom);

    private PointD ClampToImage(PointD point)
    {
        if (Source is not { } image)
        {
            return point;
        }

        return new PointD(
            Math.Clamp(point.X, 0, image.Size.Width),
            Math.Clamp(point.Y, 0, image.Size.Height));
    }

    private Shape? HitTest(PointD point)
    {
        double tolerance = 4 / Math.Max(_zoom, 0.0001);

        for (int index = _shapes.Count - 1; index >= 0; index--)
        {
            RectD bounds = _shapes[index].Bounds;

            if (point.X >= bounds.X - tolerance
                && point.X <= bounds.Right + tolerance
                && point.Y >= bounds.Y - tolerance
                && point.Y <= bounds.Bottom + tolerance)
            {
                return _shapes[index];
            }
        }

        return null;
    }

    private static RectD Normalize(PointD a, PointD b)
        => RectD.FromLtrb(
            Math.Min(a.X, b.X),
            Math.Min(a.Y, b.Y),
            Math.Max(a.X, b.X),
            Math.Max(a.Y, b.Y));
}
