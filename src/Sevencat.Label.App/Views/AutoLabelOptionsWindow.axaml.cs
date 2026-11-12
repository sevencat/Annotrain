using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Sevencat.Core.AutoLabel;
using Sevencat.Core.Localization;

namespace Sevencat.Label.App.Views;

/// <summary>AI 标注设置：只收模型路径与置信度，推理参数在标注窗口里用。</summary>
public partial class AutoLabelOptionsWindow : Window
{
    private static Localizer Loc => Localizer.Instance;

    public AutoLabelOptionsWindow() => InitializeComponent();

    public AutoLabelOptionsWindow(string? modelPath, double confidence) : this()
    {
        ModelBox.Text = modelPath ?? string.Empty;
        ConfidenceBox.Value = (decimal)Math.Clamp(confidence, 0.05, 0.95);
    }

    /// <summary>点确定后的参数；取消为 null。</summary>
    public AutoLabelOptions? Result { get; private set; }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return;
        }

        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.Get("AutoLabel.PickTitle"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Loc.Get("AutoLabel.FileType")) { Patterns = ["*.onnx"] },
            ],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { Length: > 0 } path)
        {
            ModelBox.Text = path;
            ErrorText.Text = string.Empty;
        }
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        string path = ModelBox.Text?.Trim() ?? string.Empty;

        if (path.Length == 0)
        {
            ErrorText.Text = Loc.Get("AutoLabel.NeedModel");
            return;
        }

        if (!path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
        {
            ErrorText.Text = Loc.Get("AutoLabel.OnnxOnly");
            return;
        }

        double confidence = (double)(ConfidenceBox.Value ?? 0.25m);
        Result = new AutoLabelOptions(path, confidence);
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
