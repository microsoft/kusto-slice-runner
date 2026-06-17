using System.Text.Json.Nodes;
using KoLite.Local.Sqlite.Catalog;
using KoLite.LocalApp.Ui;

namespace KoLite.LocalApp.Api
{
    public sealed record JobTargetDto(string ClusterUri, string Database);

    public sealed record JobSummaryDto(
        string JobId,
        string DisplayName,
        bool IsEnabled,
        bool IsSoftDeleted,
        bool HasStarted,
        bool IsPaused,
        IReadOnlyList<string> Tags,
        JobTargetDto Target,
        long CatalogVersion,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc);

    // Localhost-only JSON API that lets a same-machine agent read jobs and create/update
    // schedules. Every write goes through SqliteJobCatalogRepository.Import - the exact
    // validated, additive/update-only path the dashboard import uses - so strict schedule
    // parsing, started-job mutation policy, catalog versioning, and audit events all apply.
    // The API intentionally exposes no enable/disable, delete, Kusto, rerun, or repair surface.
    public static class LocalCatalogApi
    {
        private const string Actor = "local-api";

        public static void Map(WebApplication app)
        {
            app.MapGet("/api/jobs", (
                HttpContext http,
                SqliteJobCatalogRepository catalog,
                LifecycleReadModel lifecycle) =>
            {
                if (!LocalApiGuard.IsLoopback(http.Connection.RemoteIpAddress))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                var softDeleted = SoftDeletedJobIds(lifecycle);
                var jobs = catalog.List()
                    .Select(record => BuildSummary(record, catalog.HasStarted(record.JobId), softDeleted.Contains(record.JobId)))
                    .ToList();
                return Results.Json(new { jobs });
            });

            app.MapGet("/api/jobs/{jobId}", (
                string jobId,
                HttpContext http,
                SqliteJobCatalogRepository catalog,
                LifecycleReadModel lifecycle) =>
            {
                if (!LocalApiGuard.IsLoopback(http.Connection.RemoteIpAddress))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                var record = catalog.Get(jobId);
                if (record is null)
                {
                    return Results.Json(new { error = $"Job '{jobId}' does not exist." }, statusCode: StatusCodes.Status404NotFound);
                }

                var isSoftDeleted = SoftDeletedJobIds(lifecycle).Contains(jobId);
                var summary = BuildSummary(record, catalog.HasStarted(jobId), isSoftDeleted);
                return Results.Json(new { job = summary, schedule = JsonNode.Parse(record.ScheduleJson) });
            });

            app.MapGet("/api/jobs/export", (
                HttpContext http,
                SqliteJobCatalogRepository catalog,
                LifecycleReadModel lifecycle) =>
            {
                if (!LocalApiGuard.IsLoopback(http.Connection.RemoteIpAddress))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                // Import-compatible array of every non-soft-deleted job (mirrors the dashboard Export-all page).
                return Results.Text(catalog.ExportAll(SoftDeletedJobIds(lifecycle)), "application/json");
            });

            app.MapPost("/api/jobs/import", async (
                HttpContext http,
                SqliteJobCatalogRepository catalog) =>
            {
                if (!LocalApiGuard.IsLoopback(http.Connection.RemoteIpAddress))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                string body;
                using (var reader = new StreamReader(http.Request.Body))
                {
                    body = await reader.ReadToEndAsync();
                }

                if (string.IsNullOrWhiteSpace(body))
                {
                    return Results.Json(
                        new { error = "Request body must contain schedule JSON (a single object or an array)." },
                        statusCode: StatusCodes.Status400BadRequest);
                }

                try
                {
                    var result = catalog.Import(body, actor: Actor);
                    return Results.Json(result);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
                {
                    return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
                }
            });
        }

        private static HashSet<string> SoftDeletedJobIds(LifecycleReadModel lifecycle) =>
            lifecycle.GetLatestStates()
                .Where(state => state.Value.IsSoftDeleted)
                .Select(state => state.Key)
                .ToHashSet(StringComparer.Ordinal);

        private static JobSummaryDto BuildSummary(JobCatalogRecord record, bool hasStarted, bool isSoftDeleted)
        {
            var definition = record.Definition;
            return new JobSummaryDto(
                record.JobId,
                record.DisplayName,
                record.IsEnabled,
                isSoftDeleted,
                hasStarted,
                definition.IsPaused,
                definition.Tags,
                new JobTargetDto(definition.Target.ClusterUri, definition.Target.Database),
                record.CatalogVersion,
                record.CreatedAtUtc,
                record.UpdatedAtUtc);
        }
    }
}
