using FreeSql.DataAnnotations;

namespace Sevencat.Label.Server.Data;

/// <summary>
/// Web 账号：与桌面端配置库（<c>settings.db</c>）同库不同表，桌面端不建模，
/// 由本程序首次启动时 <c>SyncStructure</c> 建表。
/// </summary>
[Table(Name = "web_users")]
public sealed class WebUserEntity
{
    [Column(IsPrimary = true, IsIdentity = true, IsNullable = false)]
    public long Id { get; set; }

    [Column(StringLength = 64, IsNullable = false)]
    public string UserName { get; set; } = string.Empty;

    [Column(StringLength = 64, IsNullable = false)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary><c>pbkdf2-sha256$迭代$盐$哈希</c>，见 PasswordHasher。</summary>
    [Column(StringLength = 256, IsNullable = false)]
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>admin / annotator；目前只用于「能不能管账号」的粗粒度区分。</summary>
    [Column(StringLength = 16, IsNullable = false)]
    public string Role { get; set; } = "annotator";

    [Column(IsNullable = false)]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
