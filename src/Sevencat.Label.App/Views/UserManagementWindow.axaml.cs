using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Sevencat.Core.Localization;
using Sevencat.Label.Server.Data;

namespace Sevencat.Label.App.Views;

/// <summary>「用户管理」：列出标注 Web 端的账号（<c>settings.db</c> 的 <c>web_users</c>）。</summary>
public partial class UserManagementWindow : Window
{
    /// <summary>XAML 预览用。</summary>
    public UserManagementWindow() : this([])
    {
    }

    public UserManagementWindow(IReadOnlyList<WebUserEntity> users)
    {
        Users =
        [
            .. users.Select(user => new UserRow(
                user.UserName,
                user.DisplayName,
                user.Role == "admin" ? Text.Get("Users.RoleAdmin") : Text.Get("Users.RoleAnnotator"),
                user.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))),
        ];

        InitializeComponent();
        DataContext = this;
    }

    public IReadOnlyList<UserRow> Users { get; }

    public bool HasNoUser => Users.Count == 0;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

/// <summary>列表里的一行（角色的显示名已按当前语言翻好）。</summary>
public sealed record UserRow(string UserName, string DisplayName, string RoleText, string CreatedText);
