using Avalonia.Media;

namespace Sevencat.Label.App.Controls;

/// <summary>按标签名取稳定配色：同名同色，跨图一致。</summary>
public static class LabelPalette
{
    private static readonly Color[] Colors =
    [
        Color.FromRgb(0x3B, 0x82, 0xF6),
        Color.FromRgb(0x22, 0xC5, 0x5E),
        Color.FromRgb(0xEF, 0x44, 0x44),
        Color.FromRgb(0xF5, 0x9E, 0x0B),
        Color.FromRgb(0x8B, 0x5C, 0xF6),
        Color.FromRgb(0x06, 0xB6, 0xD4),
        Color.FromRgb(0xEC, 0x48, 0x99),
        Color.FromRgb(0x84, 0xCC, 0x16),
    ];

    private static readonly Dictionary<string, IBrush> Cache = new(StringComparer.Ordinal);

    public static Color ColorFor(string? label)
    {
        if (string.IsNullOrEmpty(label))
        {
            return Colors[0];
        }

        int hash = 0;
        foreach (char character in label)
        {
            hash = (hash * 31) + character;
        }

        return Colors[Math.Abs(hash) % Colors.Length];
    }

    public static IBrush BrushFor(string? label)
    {
        string key = label ?? string.Empty;

        if (Cache.TryGetValue(key, out IBrush? cached))
        {
            return cached;
        }

        IBrush brush = new SolidColorBrush(ColorFor(label));
        Cache[key] = brush;
        return brush;
    }
}
