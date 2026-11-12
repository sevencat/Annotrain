using FreeSql;
using Microsoft.Extensions.Logging;
using Sevencat.Core.Localization;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Dataset.TinyLabel;

/// <summary>
/// 项目级写操作：新建 / 编辑 / 结束（归档）/ 重开 / 删除。
/// <para>
/// 「新建项目」只往当前工作库（启动时开好的 <c>annotrain.db</c>）里插一行，不会创建数据库。
/// </para>
/// </summary>
public sealed class TinyLabelProjectService(
    IFreeSql orm,
    ILogger<TinyLabelProjectService>? logger = null)
{
    public const int MaxNameLength = 128;

    /// <summary>
    /// 在当前数据目录的 <c>annotrain.db</c> 里新建一个空项目，返回项目 Id；
    /// 顺手把项目目录 <c>projects/{id}/</c> 建好，后面导图直接往里面复制。
    /// </summary>
    public Task<long> CreateAsync(
        StorageLocation root,
        string name,
        string? description = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ct.ThrowIfCancellationRequested();

        string trimmed = ValidateName(name);
        long projectId = (long)orm.Insert(new ProjectEntity
        {
            Name = trimmed,
            Description = description?.Trim() ?? string.Empty,
            StorageMode = "copy",
            CreatedBy = 0,
            CreatedAt = DateTime.UtcNow,
        }).ExecuteIdentity();

        string projectDirectory = TinyLabelWorkspace.EnsureProjectDirectory(root, projectId);

        logger?.LogInformation(
            "新建项目 {Name}（Id={Id}）于 {Root}，目录 {Directory}",
            trimmed,
            projectId,
            root,
            projectDirectory);

        return Task.FromResult(projectId);
    }

    /// <summary>改项目名与备注；不动图片、不动类别索引。</summary>
    public Task UpdateAsync(
        StorageLocation root,
        long projectId,
        string name,
        string? description = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ct.ThrowIfCancellationRequested();

        string trimmed = ValidateName(name);
        int affected = orm.Update<ProjectEntity>()
            .Where(project => project.Id == projectId)
            .Set(project => project.Name, trimmed)
            .Set(project => project.Description, description?.Trim() ?? string.Empty)
            .ExecuteAffrows();

        if (affected == 0)
        {
            throw new InvalidOperationException(Text.Format("Error.ProjectNotFound", projectId));
        }

        logger?.LogInformation("编辑项目 {Id}：{Name}", projectId, trimmed);

        return Task.CompletedTask;
    }

    /// <summary>结束（归档）/ 重新打开项目：只动 <c>ClosedAt</c>，图片与标注都不动。</summary>
    public Task SetClosedAsync(
        StorageLocation root,
        long projectId,
        bool closed,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ct.ThrowIfCancellationRequested();

        int affected = orm.Update<ProjectEntity>()
            .Where(project => project.Id == projectId)
            .Set(project => project.ClosedAt, closed ? DateTime.UtcNow : null)
            .ExecuteAffrows();

        if (affected == 0)
        {
            throw new InvalidOperationException(Text.Format("Error.ProjectNotFound", projectId));
        }

        logger?.LogInformation("{Action}项目 {Id}", closed ? "结束" : "重新打开", projectId);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 删除项目：项目行 + 图片行 + 标注行 + 类别行一起清掉；
    /// <paramref name="deleteImages"/> 为 true 时再删 <c>projects/{Id}/</c> 目录（扫描模式的原图不动）。
    /// </summary>
    public Task DeleteAsync(
        StorageLocation root,
        long projectId,
        bool deleteImages = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ct.ThrowIfCancellationRequested();

        orm.Delete<AnnotationEntity>().Where(row => row.ProjectId == projectId).ExecuteAffrows();
        orm.Delete<ImageEntity>().Where(row => row.ProjectId == projectId).ExecuteAffrows();
        orm.Delete<ProjectClassEntity>().Where(row => row.ProjectId == projectId).ExecuteAffrows();
        orm.Delete<ProjectEntity>().Where(row => row.Id == projectId).ExecuteAffrows();

        if (deleteImages)
        {
            string folder = TinyLabelWorkspace.ProjectDirectory(root, projectId);

            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        logger?.LogWarning("已删除项目 {Id}（含图片与标注，删图片目录={DeleteImages}）", projectId, deleteImages);

        return Task.CompletedTask;
    }

    private static string ValidateName(string? name)
    {
        string trimmed = (name ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            throw new ArgumentException(Text.Get("Project.NameRequired"), nameof(name));
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException(Text.Format("Project.NameTooLong", MaxNameLength), nameof(name));
        }

        return trimmed;
    }
}
