using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Label.Server.Data;

namespace Sevencat.Label.Server;

// Web 端 DTO。前端镜像在 src/Sevencat.Label.Web/src/types/api.ts，改契约时两边一起改。

public sealed record UserDto(long Id, string UserName, string DisplayName);

public sealed record LoginRequest(string UserName, string Password);

public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt, UserDto User);

public sealed record ProjectDto(
    long Id,
    string Name,
    string Description,
    string StorageMode,
    DateTime CreatedAt,
    int ImageCount,
    int LabeledCount,
    int ExcludedCount,
    DateTime? ClosedAt,
    int MemberCount,
    bool IsClosed,
    bool IsRestricted);

public sealed record ProjectClassDto(string Name, int OrderIndex, string Color);

public sealed record ProjectDetailDto(ProjectDto Project, IReadOnlyList<ProjectClassDto> Classes);

public sealed record UserWorkloadDto(long UserId, string DisplayName, int Assigned, int Labeled, int Reviewed);

public sealed record ProjectStatsDto(
    int Total,
    int Pending,
    int Labeled,
    int Reviewed,
    int Excluded,
    int Unassigned,
    int Skipped,
    IReadOnlyList<UserWorkloadDto> Workload);

public sealed record ImageDto(
    long Id,
    long ProjectId,
    string FileName,
    string RelPath,
    int Width,
    int Height,
    long FileSizeBytes,
    string Sha256,
    string State,
    int Revision,
    bool Locked,
    long? LockedBy,
    DateTime? LockExpiresAt,
    DateTime UpdatedAt,
    long? AssigneeId,
    string? AssigneeName,
    string? Remark,
    bool HasAnnotation);

public sealed record AnnotationDto(long ImageId, int Revision, string DocJson, DateTime UpdatedAt, long UpdatedBy);

public sealed record SaveAnnotationRequest(int Revision, string DocJson);

public sealed record RemarkRequest(string? Remark);

public sealed record ApiError(string Code, string Message);

public sealed record ApiConflict(ApiError Error, ImageDto? Current);

public static class ApiContractMapping
{
    public static UserDto ToDto(this WebUserEntity user) => new(user.Id, user.UserName, user.DisplayName);

    public static ImageDto ToDto(this TinyLabelImageTask task, string? assigneeName = null) => new(
        task.Id,
        task.ProjectId,
        task.FileName,
        task.RelPath,
        task.Width,
        task.Height,
        task.FileSizeBytes,
        task.Sha256,
        task.State,
        task.Revision,
        task.IsLockedAt(DateTime.UtcNow),
        task.LockedBy,
        task.LockExpiresAt,
        task.UpdatedAt,
        task.AssigneeId,
        assigneeName,
        task.Remark,
        task.HasAnnotation);

    public static ProjectClassDto ToDto(this TinyLabelClassRow row) => new(row.Name, row.OrderIndex, row.Color);
}
