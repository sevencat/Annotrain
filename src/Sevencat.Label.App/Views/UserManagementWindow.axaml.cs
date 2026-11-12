using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Sevencat.Core.Localization;
using Sevencat.Label.Server.Data;

namespace Sevencat.Label.App.Views;

/// <summary>
/// 「用户管理」：列出、新建、编辑标注 Web 端的账号（<c>annotrain.db</c> 的 <c>web_users</c>）。
/// 界面按 TinyLabel 的 UsersView 排：上方图标工具条、中间表格、下方状态栏。
/// </summary>
public partial class UserManagementWindow : Window
{
    private readonly WebUserStore? _store;
    private readonly ObservableCollection<UserRow> _rows = [];

    /// <summary>XAML 预览用。</summary>
    public UserManagementWindow()
    {
        InitializeComponent();
        UserList.ItemsSource = _rows;
        CountText.Text = Text.Format("Users.CreatedCount", 0);
    }

    public UserManagementWindow(WebUserStore store) : this()
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        Reload();
    }

    private void Reload()
    {
        if (_store is null)
        {
            return;
        }

        _rows.Clear();

        foreach (WebUserEntity user in _store.List())
        {
            _rows.Add(new UserRow(
                user.Id,
                user.UserName,
                user.DisplayName,
                string.Equals(user.Role, "admin", StringComparison.Ordinal)
                    ? Text.Get("Users.RoleAdmin")
                    : Text.Get("Users.RoleAnnotator"),
                user.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        CountText.Text = Text.Format("Users.CreatedCount", _rows.Count);
    }

    private async void OnNewUser(object? sender, RoutedEventArgs e)
    {
        if (_store is null)
        {
            return;
        }

        var dialog = new UserEditorWindow(_store, user: null);

        if (await dialog.ShowDialog<bool>(this))
        {
            Reload();
        }
    }

    private async void OnEditUser(object? sender, RoutedEventArgs e)
    {
        if (_store is null || sender is not Button { DataContext: UserRow row })
        {
            return;
        }

        WebUserEntity? user = _store.FindById(row.Id);

        if (user is null)
        {
            Reload();
            return;
        }

        var dialog = new UserEditorWindow(_store, user);

        if (await dialog.ShowDialog<bool>(this))
        {
            Reload();
            StatusText.Text = Text.Format("Users.Edited", row.UserName);
        }
    }

    private async void OnResetPassword(object? sender, RoutedEventArgs e)
    {
        if (_store is null || sender is not Button { DataContext: UserRow row })
        {
            return;
        }

        WebUserEntity? user = _store.FindById(row.Id);

        if (user is null)
        {
            Reload();
            return;
        }

        var dialog = new ResetPasswordWindow(_store, user);

        if (await dialog.ShowDialog<bool>(this))
        {
            StatusText.Text = Text.Format("Users.PasswordChanged", row.UserName);
        }
    }

    private void OnRefresh(object? sender, RoutedEventArgs e)
    {
        Reload();
        StatusText.Text = string.Empty;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

/// <summary>列表里的一行（角色的显示名已按当前语言翻好）。</summary>
public sealed record UserRow(long Id, string UserName, string DisplayName, string RoleText, string CreatedText);
