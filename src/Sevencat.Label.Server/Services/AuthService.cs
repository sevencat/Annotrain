using Sevencat.Label.Server.Data;
using Sevencat.Label.Server.Security;

namespace Sevencat.Label.Server;

/// <summary>登录 / 查当前用户；账号存在 <c>settings.db</c> 的 <c>web_users</c>。</summary>
public sealed class AuthService(WebUserStore users, TokenProtector tokens)
{
    /// <summary>用户名或密码不对都返回 <c>null</c>，不告诉调用方是哪一项错了。</summary>
    public LoginResponse? Login(string? userName, string? password)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        WebUserEntity? user = users.FindByUserName(userName.Trim());

        if (user is null || !PasswordHasher.Verify(password, user.PasswordHash))
        {
            return null;
        }

        (string token, DateTimeOffset expiresAt) = tokens.Issue(user.Id);

        return new LoginResponse(token, expiresAt, user.ToDto());
    }

    public UserDto? Find(long userId) => users.FindById(userId)?.ToDto();
}
