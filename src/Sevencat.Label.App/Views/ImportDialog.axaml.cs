using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Sevencat.Core.Dataset;
using Sevencat.Label;
using Sevencat.Label.App.ViewModels;

namespace Sevencat.Label.App.Views;

/// <summary>
/// 导入对话框：只收集「源目录 + 形态 + 项目名」，实际导入由主窗口执行。
/// 目标是当前数据目录（<c>annotrain.db</c> 所在处），不在这里选。
/// </summary>
public partial class ImportDialog : Window
{
    private readonly LabelDatasetImportService? _importer;

    /// <summary>XAML 预览用。</summary>
    public ImportDialog() => InitializeComponent();

    public ImportDialog(string dataDirectory, LabelDatasetImportService importer) : this()
    {
        _importer = importer;
        TargetText.Text = $"目标：{dataDirectory}（导入成一个新项目）";
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "选择数据集目录", AllowMultiple = false });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            SourceBox.Text = path;
            HintText.Text = string.Empty;
        }
    }

    private async void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (_importer is null)
        {
            Close(null);
            return;
        }

        string source = SourceBox.Text?.Trim() ?? string.Empty;
        if (source.Length == 0)
        {
            HintText.Text = "请先选择源数据集目录";
            return;
        }

        DatasetFormat? forced = FormatBox.SelectedIndex switch
        {
            1 => DatasetFormat.Yolo,
            2 => DatasetFormat.Coco,
            _ => null,
        };

        try
        {
            DatasetProbe probe = await _importer.DetectAsync(source);
            DatasetFormat format = forced ?? probe.Format;

            if (format == DatasetFormat.Ambiguous)
            {
                HintText.Text = "这个目录同时符合多种格式，请在上面选一下形态";
                return;
            }

            if (!LabelDatasetPolicy.CanImport(format))
            {
                HintText.Text = $"「{format.ToDisplayName()}」不能作为导入来源，请选 YOLO 或 RF-DETR";
                return;
            }

            string? name = string.IsNullOrWhiteSpace(NameBox.Text) ? null : NameBox.Text.Trim();
            Close(new ImportRequest(source, name, format));
        }
        catch (Exception error)
        {
            HintText.Text = $"识别失败：{error.Message}";
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
