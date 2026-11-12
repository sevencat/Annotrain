using FreeSql;
using Sevencat.Label.Server.Security;

namespace Sevencat.Label.Server.Data;

/// <summary>Web 账号表（<c>settings.db</c> 里的 <c>web_users</c>）的读写。</summary>
public sealed class WebUserStore : IDisposable
{
    private readonly IFreeSql _orm;

    public WebUserStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        string full = Path.GetFullPath(databasePath);
        string? directory = Path.GetDirectoryName(full);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _orm = new FreeSqlBuilder()
            .UseConnectionString(DataType.Sqlite, $"Data Source={full};Default Timeout=5;Pooling=True;")
            .UseAutoSyncStructure(false)
            .Build();

        _orm.Ado.ExecuteNonQuery("PRAGMA journal_mode=WAL;");
        _orm.CodeFirst.SyncStructure<WebUserEntity>();
    }

    public IReadOnlyList<WebUserEntity> List()
        => [.. _orm.Select<WebUserEntity>().OrderBy(user => user.Id).ToList()];

    public WebUserEntity? FindByUserName(string userName)
        => _orm.Select<WebUserEntity>().Where(user => user.UserName == userName).First();

    public WebUserEntity? FindById(long id)
        => _orm.Select<WebUserEntity>().Where(user => user.Id == id).First();

    public WebUserEntity Create(string userName, string displayName, string password, string role = "annotator")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrEmpty(password);

        string trimmed = userName.Trim();

        if (FindByUserName(trimmed) is not null)
        {
            throw new InvalidOperationException($"账号已存在：{trimmed}");
        }

        var entity = new WebUserEntity
        {
            UserName = trimmed,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? trimmed : displayName.Trim(),
            PasswordHash = PasswordHasher.Hash(password),
            Role = role,
            CreatedAt = DateTime.UtcNow,
        };

        entity.Id = (long)_orm.Insert(entity).ExecuteIdentity();

        return entity;
    }

    /// <summary>
    /// 一个账号都没有时建 <c>admin</c> 并把初始密码写到 <paramref name="passwordFile"/>；
    /// 已有账号则什么都不做（返回 <c>null</c>）。
    /// </summary>
    public string? EnsureBootstrapAdmin(string passwordFile)
    {
        if (_orm.Select<WebUserEntity>().Any())
        {
            return null;
        }

        string password = GeneratePassword();
        Create("admin", "管理员", password, "admin");

        string? directory = Path.GetDirectoryName(passwordFile);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            passwordFile,
            $"账号：admin{Environment.NewLine}密码：{password}{Environment.NewLine}"
            + $"（首次启动生成，登录后请尽快修改；删掉本文件不影响登录）{Environment.NewLine}");

        return password;
    }

    public void Dispose() => _orm.Dispose();

    private static string GeneratePassword()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> buffer = stackalloc char[10];

        for (int index = 0; index < buffer.Length; index++)
        {
            buffer[index] = alphabet[Random.Shared.Next(alphabet.Length)];
        }

        return new string(buffer);
    }
}
