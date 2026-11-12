using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Sevencat.Core.Localization;

namespace Sevencat.Label.App.Localization;

/// <summary>
/// XAML 里的本地化标记：<c>Text="{l:Loc Label.NewProject}"</c>。
/// 返回的是对 <see cref="Localizer"/> 索引器的绑定，切换语言时界面自动刷新，不用重建窗口。
/// </summary>
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    /// <summary>文案键（见 <see cref="Localizer"/> 里的字典）。</summary>
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding($"[{Key}]")
        {
            Source = Localizer.Instance,
            Mode = BindingMode.OneWay,
        };
}
