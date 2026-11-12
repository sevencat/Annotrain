namespace Sevencat.Core.Localization;

/// <summary>
/// Core 里抛异常时的文案入口：<c>throw new InvalidOperationException(Text.Format("Error.ProjectNotFound", id));</c>。
/// 走 <see cref="Localizer"/> 的单例，所以切语言后同一处代码给出的原因也跟着变；
/// 界面把异常消息拼进「导入失败：{0}」时不会出现半句中文半句英文。
/// </summary>
public static class Text
{
    /// <summary>取一条不带参数的文案。</summary>
    public static string Get(string key) => Localizer.Instance.Get(key);

    /// <summary>取一条带占位符的文案（<see cref="string.Format(string,object?[])"/> 语法）。</summary>
    public static string Format(string key, params object?[] args) => Localizer.Instance.Format(key, args);
}
