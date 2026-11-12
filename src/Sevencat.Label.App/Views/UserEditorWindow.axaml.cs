using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Sevencat.Core.Localization;
using Sevencat.Label.Server.Data;

namespace Sevencat.Label.App.Views;

/// <summary>新建 / 编辑账号（<c>annotrain.db</c> 的 <c>web_users</c>），布局照 TinyLabel 的用户编辑器。</summary>
public partial class UserEditorWindow : Window
{
    private static readonly Regex UserNamePattern = new("^[a-z0-9._-]+$", RegexOptions.Compiled);

    private readonly WebUserStore? _store;
    private readonly WebUserEntity? _user;

    /// <summary>XAML 预览用。</summary>
    public UserEditorWindow()
    {
        InitializeComponent();
        Title = Text.Get("Users.New");
    }

    public UserEditorWindow(WebUserStore store, WebUserEntity? user) : this()
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _user = user;

        bool isNew = user is null;
        Title = Text.Get(isNew ? "Users.New" : "Users.Edit");
        PasswordPanel.IsVisible = isNew;
        UserNameBox.IsReadOnly = !isNew;

        if (user is not null)
        {
            UserNameBox.Text = user.UserName;
            DisplayNameBox.Text = user.DisplayName;
            RoleBox.SelectedIndex = string.Equals(user.Role, "admin", StringComparison.Ordinal) ? 1 : 0;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_store is null)
        {
            Close(false);
            return;
        }

        string userName = (UserNameBox.Text ?? string.Empty).Trim();
        string displayName = (DisplayNameBox.Text ?? string.Empty).Trim();
        string role = (RoleBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "annotator";

        try
        {
            if (_user is null)
            {
                string password = PasswordBox.Text ?? string.Empty;

                if (!UserNamePattern.IsMatch(userName))
                {
                    StatusText.Text = Text.Get("Users.UserNameRule");
                    return;
                }

                if (password.Length < 6)
                {
                    StatusText.Text = Text.Get("Users.PasswordTooShort");
                    return;
                }

                _store.Create(userName, displayName, password, role);
            }
            else
            {
                _store.Update(_user.Id, displayName, role);
            }

            Close(true);
        }
        catch (InvalidOperationException error)
        {
            StatusText.Text = error.Message;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = Text.Format("Users.SaveFailed", error.Message);
        }
    }
}
