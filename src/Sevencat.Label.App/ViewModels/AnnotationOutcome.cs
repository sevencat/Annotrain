namespace Sevencat.Label.App.ViewModels;

/// <summary>标注窗口的关闭方式。</summary>
public enum AnnotationOutcome
{
    /// <summary>没保存就关了。</summary>
    Cancelled,

    /// <summary>保存后关闭。</summary>
    Saved,

    /// <summary>保存后继续领下一张待标注的图。</summary>
    SavedAndContinue,
}
