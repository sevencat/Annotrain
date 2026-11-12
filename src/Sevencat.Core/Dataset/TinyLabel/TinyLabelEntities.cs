using FreeSql.DataAnnotations;

namespace Sevencat.Core.Dataset.TinyLabel;

// 表结构与 F:\code\TinyLabel\src\TinyLabel.Server\Data 下的实体保持一致（2026-11 版本）：
// 主键是 SQLite 自增 long，不是字符串；Annotrain 只额外要求在 images 上多一列 Split。
// users / project_members / settings / autolabel_schemes 属于服务端多人协作，桌面端不建模，
// TinyLabel.Server 启动时会自己 SyncStructure 补齐这些表。

/// <summary>项目表。图片统一放在 <c>数据目录/projects/{Id}/</c> 下。</summary>
[Table(Name = "projects")]
public sealed class ProjectEntity
{
    /// <summary>自增主键；同时作为 <c>数据目录/projects/{Id}/</c> 的目录名。</summary>
    [Column(IsPrimary = true, IsIdentity = true, IsNullable = false)]
    public long Id { get; set; }

    [Column(StringLength = 128, IsNullable = false)]
    public string Name { get; set; } = string.Empty;

    [Column(StringLength = 512, IsNullable = false)]
    public string Description { get; set; } = string.Empty;

    /// <summary><c>import</c>（图片复制进项目目录）或 <c>scan</c>（引用原图）。</summary>
    [Column(StringLength = 16, IsNullable = false)]
    public string StorageMode { get; set; } = "import";

    /// <summary>创建者用户 Id；桌面端单机没有用户体系，固定 0。</summary>
    [Column(IsNullable = false)]
    public long CreatedBy { get; set; }

    [Column(IsNullable = false)]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>为 <c>null</c> 表示项目仍在进行中。</summary>
    [Column(IsNullable = true)]
    public DateTime? ClosedAt { get; set; }
}

/// <summary>图片清单。二进制不进库：只存路径、尺寸与 sha256。</summary>
[Table(Name = "images")]
public sealed class ImageEntity
{
    [Column(IsPrimary = true, IsIdentity = true, IsNullable = false)]
    public long Id { get; set; }

    [Column(IsNullable = false)]
    public long ProjectId { get; set; }

    [Column(StringLength = 260, IsNullable = false)]
    public string FileName { get; set; } = string.Empty;

    /// <summary>展示用相对路径（导入模式相对项目图片目录，扫描模式相对扫描根）。</summary>
    [Column(StringLength = 512, IsNullable = false)]
    public string RelPath { get; set; } = string.Empty;

    /// <summary>实际磁盘路径（绝对路径）。</summary>
    [Column(StringLength = 512, IsNullable = false)]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>EXIF 归一后的宽度（与标注文档坐标系一致）。</summary>
    [Column(IsNullable = false)]
    public int Width { get; set; }

    [Column(IsNullable = false)]
    public int Height { get; set; }

    [Column(IsNullable = false)]
    public long FileSizeBytes { get; set; }

    /// <summary>内容哈希（小写十六进制）。</summary>
    [Column(StringLength = 64, IsNullable = false)]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>pending / labeled / reviewed / excluded / skipped。</summary>
    [Column(StringLength = 16, IsNullable = false)]
    public string State { get; set; } = "pending";

    /// <summary>标注版本，从 0 开始；每次保存 +1，用作乐观锁。</summary>
    [Column(IsNullable = false)]
    public int Revision { get; set; }

    /// <summary>
    /// 训练划分：train / val / test；<c>null</c> 表示未分配，默认不参与训练。
    /// 这一列是 Annotrain 新增的，打开旧库时由 <see cref="TinyLabelDatabase"/> 自动补齐。
    /// </summary>
    [Column(StringLength = 16, IsNullable = true)]
    public string? Split { get; set; }

    /// <summary>租约持有者（用户 Id）；为空表示无人锁定。</summary>
    [Column(IsNullable = true)]
    public long? LockedBy { get; set; }

    [Column(IsNullable = true)]
    public DateTime? LockExpiresAt { get; set; }

    /// <summary>任务负责人（用户 Id）；与租约正交。</summary>
    [Column(IsNullable = true)]
    public long? AssigneeId { get; set; }

    /// <summary>备注：驳回原因 / 跳过原因 / 管理员说明共用这一个字段。</summary>
    [Column(StringLength = 512, IsNullable = true)]
    public string? Remark { get; set; }

    [Column(IsNullable = true)]
    public long? RemarkBy { get; set; }

    [Column(IsNullable = true)]
    public DateTime? RemarkAt { get; set; }

    [Column(IsNullable = false)]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column(IsNullable = false)]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>标注：一图一行，<see cref="DocJson"/> 为 labelme JSON 原文，服务端不做结构改写。</summary>
[Table(Name = "annotations")]
public sealed class AnnotationEntity
{
    /// <summary>一图一行，直接以图片 Id 作主键。</summary>
    [Column(IsPrimary = true, IsNullable = false)]
    public long ImageId { get; set; }

    [Column(IsNullable = false)]
    public long ProjectId { get; set; }

    /// <summary>与 <c>images.Revision</c> 同步递增，冗余一份便于导出时免 join。</summary>
    [Column(IsNullable = false)]
    public int Revision { get; set; }

    [Column(StringLength = -1, IsNullable = false)]
    public string DocJson { get; set; } = string.Empty;

    /// <summary>最后保存者用户 Id；桌面端单机固定 0。</summary>
    [Column(IsNullable = false)]
    public long UpdatedBy { get; set; }

    [Column(IsNullable = false)]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 项目类别表。<see cref="OrderIndex"/> 由服务端分配且不复用，是导出 YOLO <c>names</c> 的唯一索引来源；
/// 删除类别只做逻辑删除才能保住历史导出的索引含义。
/// </summary>
[Table(Name = "project_classes")]
public sealed class ProjectClassEntity
{
    [Column(IsPrimary = true, IsIdentity = true, IsNullable = false)]
    public long Id { get; set; }

    [Column(IsNullable = false)]
    public long ProjectId { get; set; }

    [Column(StringLength = 128, IsNullable = false)]
    public string Name { get; set; } = string.Empty;

    [Column(IsNullable = false)]
    public int OrderIndex { get; set; }

    /// <summary>#RRGGBB，仅用于界面显示。</summary>
    [Column(StringLength = 16, IsNullable = false)]
    public string Color { get; set; } = "#3B82F6";
}
