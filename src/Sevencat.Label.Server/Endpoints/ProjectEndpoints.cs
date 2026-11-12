using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Storage;
using Sevencat.Label.Server.Data;
using Sevencat.Label.Server.Security;

namespace Sevencat.Label.Server;

/// <summary>项目列表 / 项目详情 / 统计 / 取下一张待标图。</summary>
public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects", async (
            ServerOptions options,
            TinyLabelProjectCatalog catalog,
            TinyLabelTaskService tasks,
            CancellationToken ct) =>
        {
            StorageLocation root = StorageLocation.FromPath(options.DataDirectory);
            IReadOnlyList<TinyLabelProjectSummary> summaries = await catalog.ListAsync(root, ct);

            var projects = new List<ProjectDto>(summaries.Count);

            foreach (TinyLabelProjectSummary summary in summaries)
            {
                TinyLabelProjectStats? stats = await tasks.GetStatsAsync(root, summary.Id, ct);
                projects.Add(ToDto(summary, stats));
            }

            return Results.Ok(projects);
        });

        app.MapGet("/api/projects/{id:long}", async (
            long id,
            ServerOptions options,
            TinyLabelProjectCatalog catalog,
            TinyLabelClassService classes,
            TinyLabelTaskService tasks,
            CancellationToken ct) =>
        {
            StorageLocation root = StorageLocation.FromPath(options.DataDirectory);
            IReadOnlyList<TinyLabelProjectSummary> summaries = await catalog.ListAsync(root, ct);

            if (summaries.FirstOrDefault(item => item.Id == id) is not { } summary)
            {
                return EndpointResults.NotFound("project");
            }

            TinyLabelProjectStats? stats = await tasks.GetStatsAsync(root, id, ct);
            IReadOnlyList<TinyLabelClassRow> rows = await classes.ListAsync(root, id, ct);

            return Results.Ok(new ProjectDetailDto(
                ToDto(summary, stats),
                [.. rows.Select(row => row.ToDto())]));
        });

        app.MapGet("/api/projects/{id:long}/stats", async (
            long id,
            ServerOptions options,
            TinyLabelTaskService tasks,
            WebUserStore users,
            CancellationToken ct) =>
        {
            StorageLocation root = StorageLocation.FromPath(options.DataDirectory);
            TinyLabelProjectStats? stats = await tasks.GetStatsAsync(root, id, ct);

            if (stats is null)
            {
                return EndpointResults.NotFound("project");
            }

            Dictionary<long, string> names = users.List().ToDictionary(user => user.Id, user => user.DisplayName);

            return Results.Ok(new ProjectStatsDto(
                stats.Total,
                stats.Pending,
                stats.Labeled,
                stats.Reviewed,
                stats.Excluded,
                stats.Unassigned,
                stats.Skipped,
                [.. stats.Workload
                    .Where(load => load.UserId is not null)
                    .Select(load => new UserWorkloadDto(
                        load.UserId!.Value,
                        names.TryGetValue(load.UserId.Value, out string? name) ? name : $"#{load.UserId}",
                        load.Assigned,
                        load.Labeled,
                        load.Reviewed))]));
        });

        // 取下一张：after 通常是当前图的 Id，顺着往下走，到底再回头。
        app.MapPost("/api/projects/{id:long}/next-image", async (
            long id,
            HttpContext context,
            ServerOptions options,
            TinyLabelTaskService tasks,
            WebUserStore users,
            CancellationToken ct,
            long? after = null) =>
        {
            StorageLocation root = StorageLocation.FromPath(options.DataDirectory);
            long userId = CurrentUser.Require(context);

            TinyLabelImageTask? task = await tasks.TakeNextAsync(root, id, userId, after, ct: ct);

            if (task is null)
            {
                return Results.NoContent();
            }

            return Results.Ok(task.ToDto(users.FindById(task.AssigneeId ?? 0)?.DisplayName));
        });

        return app;
    }

    private static ProjectDto ToDto(TinyLabelProjectSummary summary, TinyLabelProjectStats? stats) => new(
        summary.Id,
        summary.Name,
        summary.Description,
        summary.StorageMode,
        summary.CreatedAt,
        summary.ImageCount,
        summary.LabeledCount,
        stats?.Excluded ?? 0,
        summary.ClosedAt,
        stats?.Workload.Count(load => load.UserId is not null) ?? 0,
        summary.IsClosed,
        false);
}
