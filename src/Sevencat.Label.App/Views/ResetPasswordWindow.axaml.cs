using Avalonia.Controls;
using Avalonia.Interactivity;
using Sevencat.Core.Localization;
using Sevencat.Label.Server.Data;

namespace Sevencat.Label.App.Views;

/// <summary>管理员替别人重置登录密码（不用旧密码）。</summary>
public partial class ResetPasswordWindow : Window
{
    private readonly WebUserStore? _store;
    private readonly WebUserEntity? _user;

    /// <summary>XAML 预览用。</summary>
    public ResetPasswordWindow()
    {
        InitializeComponent();
        HintText.Text = Text.Get("Users.ResetPassword");
    }

    public ResetPasswordWindow(WebUserStore store, WebUserEntity user) : this()
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(user);

        _store = store;
        _user = user;
        HintText.Text = $"{Text.Get("Users.ResetPassword")}：{user.UserName}";
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_store is null || _user is null)
        {
            Close(false);
            return;
        }

        string password = NewPasswordBox.Text ?? string.Empty;

        if (password.Length < 6)
        {
            StatusText.Text = Text.Get("Users.PasswordTooShort");
            return;
        }

        if (!string.Equals(password, ConfirmPasswordBox.Text, StringComparison.Ordinal))
        {
            StatusText.Text = Text.Get("Users.PasswordMismatch");
            return;
        }

        try
        {
            _store.ResetPassword(_user.Id, password);
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
