using System.Text.Json;
using System.Text.Json.Nodes;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Lifecycle;
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
    // schedules. Every schedule write goes through SqliteJobCatalogRepository.Import - the exact
    // validated, additive/update-only path the dashboard import uses - so strict schedule
    // parsing, started-job mutation policy, catalog versioning, and audit events all apply.
    // It also exposes soft-delete and restore (POST /api/jobs/{jobId}/soft-delete|restore), wired to
    // SqliteJobLifecycleService with the same required optimistic-concurrency (expectedVersion) and
    // downstream-dependents confirmation the dashboard uses. The API intentionally still exposes no
    // hard-delete, enable/disable, Kusto, rerun, cleanup, or repair surface.
    public static class LocalCatalogApi
    {
        private const string Actor = "local-api";

        public static void Map(IEndpointRouteBuilder api)
        {
            api.MapGet("/jobs", (
                SqliteJobCatalogRepository catalog,
                LifecycleReadModel lifecycle) =>
            {
                var softDeleted = SoftDeletedJobIds(lifecycle);
                var jobs = catalog.List()
                    .Select(record => BuildSummary(record, catalog.HasStarted(record.JobId), softDeleted.Contains(record.JobId)))
                    .ToList();
                return Results.Json(new { jobs });
            });

            api.MapGet("/jobs/{jobId}", (
                string jobId,
                SqliteJobCatalogRepository catalog,
                LifecycleReadModel lifecycle) =>
            {
                var record = catalog.Get(jobId);
                if (record is null)
                {
                    return Results.Json(new { error = $"Job '{jobId}' does not exist." }, statusCode: StatusCodes.Status404NotFound);
                }

                var isSoftDeleted = SoftDeletedJobIds(lifecycle).Contains(jobId);
                var summary = BuildSummary(record, catalog.HasStarted(jobId), isSoftDeleted);
                return Results.Json(new { job = summary, schedule = JsonNode.Parse(record.ScheduleJson) });
            });

            api.MapGet("/jobs/export", (
                SqliteJobCatalogRepository catalog,
                LifecycleReadModel lifecycle) =>
            {
                // Import-compatible array of every non-soft-deleted job (mirrors the dashboard Export-all page).
                return Results.Text(catalog.ExportAll(SoftDeletedJobIds(lifecycle)), "application/json");
            });

            api.MapPost("/jobs/import", async (
                HttpContext http,
                SqliteJobCatalogRepository catalog) =>
            {
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

            // Soft-delete a job: hides it from the active catalog/export (SetEnabled(false) + a
            // "SoftDeleted" lifecycle event) but keeps its rows, so it is fully reversible via restore.
            // Requires expectedVersion (the job's current catalogVersion) for optimistic concurrency, and
            // blocks by default when active downstream dependents would break - pass "force": true to
            // override (mirrors the dashboard's "Soft delete anyway" confirm).
            api.MapPost("/jobs/{jobId}/soft-delete", async (
                string jobId,
                HttpContext http,
                SqliteJobCatalogRepository catalog,
                SqliteJobLifecycleService lifecycle,
                LifecycleReadModel lifecycleReadModel) =>
            {
                if (catalog.Get(jobId) is null)
                {
                    return JobNotFound(jobId);
                }

                var (request, error) = await ReadLifecycleRequest(http);
                if (request is null)
                {
                    return Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);
                }

                try
                {
                    lifecycle.SoftDelete(jobId, request.ExpectedVersion, actor: Actor, reason: request.Reason ?? "Soft deleted via local API", force: request.Force);
                }
                catch (DownstreamDependentsException ex)
                {
                    // Blocked by active downstream dependents and force was not requested: 409 with the
                    // blocking dependents so the caller can name them or retry with "force": true.
                    return Results.Json(
                        new
                        {
                            error = ex.Message,
                            dependents = ex.Dependents.Select(dependent => new { jobId = dependent.JobId, activityId = dependent.ActivityId }),
                        },
                        statusCode: StatusCodes.Status409Conflict);
                }
                catch (InvalidOperationException ex)
                {
                    // Optimistic-concurrency conflict (expectedVersion mismatch) or a lost existence race.
                    return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
                }

                return Results.Json(new { job = BuildSummaryFor(catalog, lifecycleReadModel, jobId) });
            });

            // Restore a soft-deleted job (SetEnabled(true) + a "Restored" lifecycle event). The inverse of
            // soft-delete; also requires expectedVersion. No dependents check applies to re-activation.
            api.MapPost("/jobs/{jobId}/restore", async (
                string jobId,
                HttpContext http,
                SqliteJobCatalogRepository catalog,
                SqliteJobLifecycleService lifecycle,
                LifecycleReadModel lifecycleReadModel) =>
            {
                if (catalog.Get(jobId) is null)
                {
                    return JobNotFound(jobId);
                }

                var (request, error) = await ReadLifecycleRequest(http);
                if (request is null)
                {
                    return Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);
                }

                try
                {
                    lifecycle.Restore(jobId, request.ExpectedVersion, actor: Actor, reason: request.Reason ?? "Restored via local API");
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
                }

                return Results.Json(new { job = BuildSummaryFor(catalog, lifecycleReadModel, jobId) });
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

        // Web defaults give camelCase, case-insensitive property matching so a body like
        // { "expectedVersion": 3, "reason": "...", "force": true } binds to LifecycleRequestDto.
        private static readonly JsonSerializerOptions LifecycleJsonOptions = new(JsonSerializerDefaults.Web);

        private static IResult JobNotFound(string jobId) =>
            Results.Json(new { error = $"Job '{jobId}' does not exist." }, statusCode: StatusCodes.Status404NotFound);

        private static JobSummaryDto BuildSummaryFor(SqliteJobCatalogRepository catalog, LifecycleReadModel lifecycle, string jobId)
        {
            // The job still exists after soft-delete/restore (both only flip is_enabled + append a
            // lifecycle event), so re-read it to return the same summary shape as GET /api/jobs/{jobId}.
            var record = catalog.Get(jobId)!;
            var isSoftDeleted = SoftDeletedJobIds(lifecycle).Contains(jobId);
            return BuildSummary(record, catalog.HasStarted(jobId), isSoftDeleted);
        }

        // Reads and validates the soft-delete/restore body. Parsed manually (like /jobs/import) so a bad
        // or incomplete body returns the same { "error": ... } shape rather than a framework 400.
        // expectedVersion is required (the caller reads it from GET /api/jobs/{jobId} first).
        private static async Task<(LifecycleRequest? Request, string? Error)> ReadLifecycleRequest(HttpContext http)
        {
            string body;
            using (var reader = new StreamReader(http.Request.Body))
            {
                body = await reader.ReadToEndAsync();
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                return (null, "Request body must be a JSON object with a required numeric 'expectedVersion' (the job's current catalogVersion).");
            }

            LifecycleRequestDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<LifecycleRequestDto>(body, LifecycleJsonOptions);
            }
            catch (JsonException ex)
            {
                return (null, $"Request body is not valid JSON: {ex.Message}");
            }

            if (dto?.ExpectedVersion is null)
            {
                return (null, "Request body must include a numeric 'expectedVersion' (the job's current catalogVersion).");
            }

            return (new LifecycleRequest(dto.ExpectedVersion.Value, dto.Reason, dto.Force ?? false), null);
        }

        // ExpectedVersion is nullable so an omitted value is rejected (rather than silently defaulting to
        // 0 and colliding with a real version). Force defaults to false when omitted.
        private sealed record LifecycleRequestDto(long? ExpectedVersion, string? Reason, bool? Force);

        private sealed record LifecycleRequest(long ExpectedVersion, string? Reason, bool Force);
    }
}
