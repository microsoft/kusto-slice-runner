using KoLite.Local.Sqlite.Catalog;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Http;

namespace KoLite.LocalApp.Pages.Catalog
{
    internal static class CatalogEnabledToggle
    {
        public static IResult Execute(
            HttpContext http,
            SqliteJobCatalogRepository catalog,
            DashboardPageQuery dashboard,
            string jobId,
            bool enabled,
            long expectedVersion)
        {
            if (!IsAjaxRequest(http.Request))
            {
                catalog.SetEnabled(jobId, enabled, expectedVersion, actor: "local-web");
                return Results.Redirect($"/jobs/{Uri.EscapeDataString(jobId)}");
            }

            JobCatalogRecord updated;
            try
            {
                updated = catalog.SetEnabled(jobId, enabled, expectedVersion, actor: "local-web");
            }
            catch (InvalidOperationException ex)
            {
                // Optimistic-concurrency conflict (or the job changed/was removed). Resync the row from the
                // current projection so the dashboard button stays actionable instead of being stuck on a
                // stale catalog version.
                return Json(StatusCodes.Status409Conflict, jobId, dashboard.GetJob(jobId), fallback: null, conflict: true, error: ex.Message);
            }

            return Json(StatusCodes.Status200OK, jobId, dashboard.GetJob(jobId), fallback: updated, conflict: false, error: null);
        }

        private static bool IsAjaxRequest(HttpRequest request) =>
            string.Equals(request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

        private static IResult Json(int statusCode, string jobId, JobListItem? job, JobCatalogRecord? fallback, bool conflict, string? error) =>
            Results.Json(new
            {
                jobId,
                enabled = job?.Record.IsEnabled ?? fallback?.IsEnabled,
                version = job?.Record.CatalogVersion ?? fallback?.CatalogVersion,
                primaryKey = job is null ? null : AppFormatting.PrimaryStatusKey(job.PrimaryState),
                healthTooltip = job?.HealthTooltip,
                showCompleteness = job?.ShowCompleteness ?? false,
                completenessCss = job is null ? null : AppFormatting.CompletenessCss(job.GapCount),
                completenessTooltip = job?.CompletenessTooltip,
                nextText = job?.NextSlice.Text,
                nextDetail = job?.NextSlice.Detail,
                conflict,
                error
            }, statusCode: statusCode);
    }
}
