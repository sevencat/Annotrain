using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Sevencat.Train.App.ViewModels;

namespace Sevencat.Train.App.Controls;

/// <summary>
/// 训练曲线：横轴 epoch，两条折线分别是 loss（左轴）与 mAP（右轴）。
/// 没有数据时画提示文字——干跑阶段不会有点，避免看起来像坏掉。
/// </summary>
public sealed class TrainingChart : Control
{
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly IBrush GridBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
    private static readonly IBrush AxisBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x6B));
    private static readonly IBrush LossBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly IBrush MapBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly IBrush HintBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));

    private IReadOnlyList<ChartPoint> _points = [];

    public string Hint { get; set; } = "开始训练后显示 loss / mAP 曲线";

    public void SetPoints(IEnumerable<ChartPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        _points = [.. points];
        InvalidateVisual();
    }

    public void SetHint(string hint)
    {
        Hint = hint;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(BackgroundBrush, bounds);

        const double left = 56;
        const double right = 56;
        const double top = 20;
        const double bottom = 32;

        var plot = new Rect(
            left,
            top,
            Math.Max(1, bounds.Width - left - right),
            Math.Max(1, bounds.Height - top - bottom));

        DrawGrid(context, plot);

        if (_points.Count < 2)
        {
            DrawHint(context, bounds);
            return;
        }

        double maxLoss = Math.Max(0.001, _points.Max(point => point.Loss));

        DrawSeries(context, plot, point => point.Loss / maxLoss, LossBrush);
        DrawSeries(context, plot, point => Math.Clamp(point.Map, 0, 1), MapBrush);

        DrawText(context, $"loss ≤ {maxLoss:0.###}", new Point(4, plot.Y - 16), LossBrush, 11);
        DrawText(context, "mAP ≤ 1.0", new Point(plot.Right + 4, plot.Y - 16), MapBrush, 11);
        DrawText(context, $"epoch 1–{_points[^1].Epoch}", new Point(plot.X, plot.Bottom + 8), AxisBrush, 11);
    }

    private static void DrawGrid(DrawingContext context, Rect plot)
    {
        for (int index = 0; index <= 4; index++)
        {
            double y = plot.Y + (plot.Height * index / 4);
            context.DrawLine(new Pen(GridBrush, 1, new DashStyle([4, 4], 0)), new Point(plot.X, y), new Point(plot.Right, y));
        }

        context.DrawLine(new Pen(AxisBrush, 1), new Point(plot.X, plot.Y), new Point(plot.X, plot.Bottom));
        context.DrawLine(new Pen(AxisBrush, 1), new Point(plot.X, plot.Bottom), new Point(plot.Right, plot.Bottom));
    }

    private void DrawSeries(DrawingContext context, Rect plot, Func<ChartPoint, double> selector, IBrush brush)
    {
        var pen = new Pen(brush, 2);
        int count = _points.Count;

        Point? previous = null;

        for (int index = 0; index < count; index++)
        {
            double x = plot.X + (plot.Width * index / Math.Max(1, count - 1));
            double value = Math.Clamp(selector(_points[index]), 0, 1);
            double y = plot.Bottom - (plot.Height * value);
            var current = new Point(x, y);

            if (previous is { } start)
            {
                context.DrawLine(pen, start, current);
            }

            previous = current;
        }
    }

    private void DrawHint(DrawingContext context, Rect bounds)
    {
        var text = new FormattedText(
            Hint,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            13,
            HintBrush);

        context.DrawText(text, new Point(
            Math.Max(8, (bounds.Width - text.Width) / 2),
            Math.Max(8, (bounds.Height - text.Height) / 2)));
    }

    private static void DrawText(DrawingContext context, string text, Point origin, IBrush brush, double size)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            size,
            brush);

        context.DrawText(formatted, origin);
    }
}
