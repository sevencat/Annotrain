using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Localization;
using Sevencat.Core.Storage;

namespace Sevencat.Label.App.Views;

/// <summary>
/// 类别管理：列表 + 追加 + 改名 + 删除。
/// <para>
/// <c>OrderIndex</c> 就是 YOLO 的 classId，所以这里只允许追加（新增分配 max+1）；
/// 删除只在"没有标注用到"时才允许，避免历史标注与 classId 对不上。
/// </para>
/// </summary>
public partial class ClassEditorWindow : Window
{
    private readonly TinyLabelClassService _service;
    private readonly StorageLocation _root;
    private readonly long _projectId;
    private readonly ObservableCollection<string> _rows = [];
    private List<TinyLabelClassRow> _classes = [];
    private bool _changed;

    private static Localizer Loc => Localizer.Instance;

    /// <summary>XAML 预览用。</summary>
    public ClassEditorWindow()
    {
        InitializeComponent();

        _service = null!;
        _root = StorageLocation.FromPath(".");
        _projectId = 0;
    }

    public ClassEditorWindow(TinyLabelClassService service, StorageLocation root, long projectId) : this()
    {
        _service = service;
        _root = root;
        _projectId = projectId;

        Items.ItemsSource = _rows;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync(long? keepId = null)
    {
        try
        {
            _classes = [.. await _service.ListAsync(_root, _projectId)];

            _rows.Clear();

            foreach (TinyLabelClassRow row in _classes)
            {
                _rows.Add($"{row.IndexText}  {row.Name}");
            }

            if (_classes.Count > 0)
            {
                Items.SelectedIndex = keepId is { } id
                    ? Math.Max(0, _classes.FindIndex(row => row.Id == id))
                    : Math.Min(Items.SelectedIndex < 0 ? 0 : Items.SelectedIndex, _classes.Count - 1);
            }

            CountText.Text = Loc.Format("Classes.Count", _classes.Count);
            ErrorText.Text = string.Empty;
        }
        catch (Exception error)
        {
            ErrorText.Text = Loc.Format("Classes.LoadFailed", error.Message);
        }
    }

    private async void OnAdd(object? sender, RoutedEventArgs e)
    {
        string name = NameBox.Text?.Trim() ?? string.Empty;

        if (name.Length == 0)
        {
            ErrorText.Text = Loc.Get("Classes.NameRequired");
            return;
        }

        try
        {
            int index = await _service.AddAsync(_root, _projectId, name);
            _changed = true;
            NameBox.Text = string.Empty;

            await ReloadAsync();
            Items.SelectedIndex = _classes.FindIndex(row => row.OrderIndex == index);
            ErrorText.Text = Loc.Format("Classes.Added", name, index);
        }
        catch (Exception error)
        {
            ErrorText.Text = error.Message;
        }
    }

    private async void OnRename(object? sender, RoutedEventArgs e)
    {
        if (Selected() is not { } row)
        {
            ErrorText.Text = Loc.Get("Classes.PickFirst");
            return;
        }

        string name = NameBox.Text?.Trim() ?? string.Empty;

        if (name.Length == 0)
        {
            ErrorText.Text = Loc.Get("Classes.NameRequired");
            return;
        }

        try
        {
            await _service.RenameAsync(_root, _projectId, row.Id, name);
            _changed = true;

            await ReloadAsync(row.Id);
            ErrorText.Text = Loc.Format("Classes.Renamed", name);
        }
        catch (Exception error)
        {
            ErrorText.Text = error.Message;
        }
    }

    private async void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (Selected() is not { } row)
        {
            ErrorText.Text = Loc.Get("Classes.PickFirst");
            return;
        }

        try
        {
            await _service.RemoveAsync(_root, _projectId, row.Id);
            _changed = true;

            await ReloadAsync();
            ErrorText.Text = Loc.Format("Classes.Removed", row.Name);
        }
        catch (Exception error)
        {
            ErrorText.Text = error.Message;
        }
    }

    private TinyLabelClassRow? Selected()
        => Items.SelectedIndex >= 0 && Items.SelectedIndex < _classes.Count
            ? _classes[Items.SelectedIndex]
            : null;

    private void OnClose(object? sender, RoutedEventArgs e) => Close(_changed);
}
