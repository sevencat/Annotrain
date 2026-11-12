using FreeSql;
using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Infrastructure;
using Sevencat.Label.Server.Security;

namespace Sevencat.Label.Server.Data;

/// <summary>
/// Web 账号表（<c>annotrain.db</c> 里的 <c>web_users</c>）的读写。
/// ORM 由容器注入（<see cref="TinyLabelDatabase"/> 的 <c>IFreeSql</c> 单例），不自己开库、不负责关。
/// </summary>
public sealed class WebUserStore
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    private readonly TinyLabelDatabase _database;
    private readonly IFreeSql _orm;

    public WebUserStore(TinyLabelDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
        _orm = database.Orm;
        _orm.CodeFirst.SyncStructure<WebUserEntity>();

        MigrateLegacyUsers();
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

    /// <summary>改显示名与角色（登录名不可改，它就是审批记录里的作者）。</summary>
    public WebUserEntity Update(long id, string displayName, string role)
    {
        WebUserEntity entity = FindById(id) ?? throw new InvalidOperationException($"账号不存在：{id}");

        entity.DisplayName = string.IsNullOrWhiteSpace(displayName) ? entity.UserName : displayName.Trim();
        entity.Role = NormalizeRole(role);

        _orm.Update<WebUserEntity>(entity);
        return entity;
    }

    /// <summary>直接换密码（管理员在桌面端替人重置，不经旧密码）。</summary>
    public void ResetPassword(long id, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        WebUserEntity entity = FindById(id) ?? throw new InvalidOperationException($"账号不存在：{id}");

        entity.PasswordHash = PasswordHasher.Hash(password);
        _orm.Update<WebUserEntity>(entity);
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

    /// <summary>从旧版独立的 <c>settings.db</c> 搬账号（新库里已有账号就不动）。</summary>
    private void MigrateLegacyUsers()
    {
        string databasePath = _database.Path;
        string? directory = Path.GetDirectoryName(databasePath);

        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        string legacy = Path.Combine(directory, AppDataPaths.LegacySettingsDatabaseFile);

        if (string.Equals(legacy, databasePath, StringComparison.OrdinalIgnoreCase) || !File.Exists(legacy))
        {
            return;
        }

        if (_orm.Select<WebUserEntity>().Any())
        {
            return;
        }

        try
        {
            using IFreeSql legacyOrm = new FreeSqlBuilder()
                .UseConnectionString(DataType.Sqlite, $"Data Source={legacy};Default Timeout=5;Pooling=False;")
                .UseAutoSyncStructure(false)
                .Build();

            List<WebUserEntity> users = legacyOrm.Select<WebUserEntity>().ToList();

            if (users.Count == 0)
            {
                return;
            }

            // 主键交给新库自增，免得和已有行撞
            foreach (WebUserEntity user in users)
            {
                user.Id = 0;
            }

            int inserted = _orm.Insert(users).ExecuteAffrows();
            Log.Info("已从旧库迁移 {0} 个账号", inserted);
        }
        catch (Exception error)
        {
            Log.Warn(error, "迁移旧账号失败，跳过");
        }
    }

    private static string NormalizeRole(string role)
        => string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase) ? "admin" : "annotator";

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
