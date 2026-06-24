using System.Text;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Lifecycle;
using KoLite.LocalApp.Pages.Catalog;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace KoLite.LocalApp.Api
{
    // The Catalog action surface - enable/disable/soft-delete/restore, pause-all/resume-all,
    // bulk pause/resume/soft-delete/export, and the two JSON exports - as one minimal-API module,
    // replacing twelve action-only Razor Pages. Every final URL, redirect, status code, JSON shape,
    // file download, TempData message, and 405-on-GET response is preserved exactly.
    //
    // There is no app.UseAntiforgery() in the pipeline: the browser POST forms were CSRF-validated by
    // Razor Pages' built-in filter. To keep that protection the POST mutation endpoints share a
    // group-level antiforgery filter that validates each request (the __RequestVerificationToken form
    // field for normal posts or the X-CSRF-TOKEN header for the AJAX toggle) and lets
    // AntiforgeryValidationException propagate to the existing friendly-400 middleware in Program.cs.
    // The two GET exports are mapped outside that group so they are never antiforgery-filtered, and the
    // POST-only routes keep returning 405 for GET via default routing.
    public static class CatalogActionsApi
    {
        private const string Actor = "local-web";

        public static void Map(WebApplication app)
        {
            var actions = app.MapGroup("/catalog").AddEndpointFilter<AntiforgeryEndpointFilter>();

            actions.MapPost("/{jobId}/enable", (
                HttpContext http,
                string jobId,
                SqliteJobCatalogRepository catalog,
                DashboardPageQuery dashboard) =>
                CatalogEnabledToggle.Execute(http, catalog, dashboard, jobId, enabled: true, ReadVersion(http)));

            actions.MapPost("/{jobId}/disable", (
                HttpContext http,
                string jobId,
                SqliteJobCatalogRepository catalog,
                DashboardPageQuery dashboard) =>
                CatalogEnabledToggle.Execute(http, catalog, dashboard, jobId, enabled: false, ReadVersion(http)));

            actions.MapPost("/{jobId}/soft-delete", (
                HttpContext http,
                string jobId,
                SqliteJobLifecycleService lifecycle) =>
            {
                try
                {
                    lifecycle.SoftDelete(jobId, ReadVersion(http), actor: Actor, reason: ReadReason(http, "Soft deleted from web UI"), force: ReadForce(http));
                    return Results.Redirect("/");
                }
                catch (DownstreamDependentsException)
                {
                    // Blocked by active downstream dependents: PRG to the confirm page, which lists the
                    // dependents and offers an explicit force ("Soft delete anyway") override.
                    return Results.Redirect($"/catalog/{Uri.EscapeDataString(jobId)}/soft-delete-confirm");
                }
            });

            actions.MapPost("/{jobId}/restore", (
                HttpContext http,
                string jobId,
                SqliteJobLifecycleService lifecycle) =>
            {
                lifecycle.Restore(jobId, ReadVersion(http), actor: Actor, reason: ReadReason(http, "Restored from web UI"));
                return Results.Redirect($"/jobs/{Uri.EscapeDataString(jobId)}");
            });

            actions.MapPost("/pause-all", (
                SqliteJobCatalogRepository catalog,
                LifecycleReadModel lifecycle) =>
            {
                var deleted = SoftDeletedJobIds(lifecycle);
                foreach (var job in catalog.List().Where(j => j.IsEnabled && !deleted.Contains(j.JobId)))
                {
                    catalog.SetEnabled(job.JobId, false, job.CatalogVersion, actor: Actor);
                }

                return Results.Redirect("/");
            });

            actions.MapPost("/resume-all", (
                SqliteJobCatalogRepository catalog,
                LifecycleReadModel lifecycle) =>
            {
                var deleted = SoftDeletedJobIds(lifecycle);
                foreach (var job in catalog.List().Where(j => !j.IsEnabled && !deleted.Contains(j.JobId)))
                {
                    catalog.SetEnabled(job.JobId, true, job.CatalogVersion, actor: Actor);
                }

                return Results.Redirect("/");
            });

            actions.MapPost("/bulk/pause", (
                HttpContext http,
                SqliteJobCatalogRepository catalog,
                LifecycleReadModel lifecycle,
                ITempDataDictionaryFactory tempDataFactory) =>
            {
                var result = CatalogBulkOperations.SetEnabled(catalog, lifecycle, ReadStrings(http, "jobIds"), ReadVersions(http, "expectedVersions"), targetEnabled: false);
                SaveBulkSummary(http, tempDataFactory, result.ToMessage("Paused"));
                return Results.Redirect("/");
            });

            actions.MapPost("/bulk/resume", (
                HttpContext http,
                SqliteJobCatalogRepository catalog,
                LifecycleReadModel lifecycle,
                ITempDataDictionaryFactory tempDataFactory) =>
            {
                var result = CatalogBulkOperations.SetEnabled(catalog, lifecycle, ReadStrings(http, "jobIds"), ReadVersions(http, "expectedVersions"), targetEnabled: true);
                SaveBulkSummary(http, tempDataFactory, result.ToMessage("Resumed"));
                return Results.Redirect("/");
            });

            actions.MapPost("/bulk/soft-delete", (
                HttpContext http,
                SqliteJobCatalogRepository catalog,
                SqliteJobLifecycleService lifecycle,
                LifecycleReadModel lifecycleReadModel,
                ITempDataDictionaryFactory tempDataFactory) =>
            {
                var result = CatalogBulkOperations.SoftDelete(catalog, lifecycle, lifecycleReadModel, ReadStrings(http, "jobIds"), ReadVersions(http, "expectedVersions"));
                SaveBulkSummary(http, tempDataFactory, result.ToMessage("Soft-deleted"));
                return Results.Redirect("/");
            });

            actions.MapPost("/bulk/export", (
                HttpContext http,
                SqliteJobCatalogRepository catalog) =>
            {
                var json = catalog.ExportSelected(ReadStrings(http, "jobIds"));
                return Results.File(Encoding.UTF8.GetBytes(json), "application/json", "ko-lite-jobs.json");
            });

            // GET exports are mapped outside the antiforgery group so they are never CSRF-validated.
            // The jobId is constrained to a GUID (catalog ids are Guid.ToString("N")) so this route does
            // not also match the literal /catalog/bulk/export path; a GET there then falls through to the
            // POST-only bulk endpoint and yields 405, exactly as the former BulkExport page did.
            app.MapGet("/catalog/{jobId:guid}/export", (
                string jobId,
                SqliteJobCatalogRepository catalog) =>
            {
                var record = catalog.Get(jobId);
                return record is null
                    ? Results.NotFound()
                    : Results.Content(AppFormatting.PrettyJson(record.ScheduleJson), "application/json");
            });

            app.MapGet("/catalog/export", (
                SqliteJobCatalogRepository catalog,
                LifecycleReadModel lifecycle) =>
                Results.Content(catalog.ExportAll(SoftDeletedJobIds(lifecycle)), "application/json"));
        }

        private static long ReadVersion(HttpContext http) =>
            long.TryParse(http.Request.Form["expectedVersion"].ToString(), out var version) ? version : 0L;

        private static string ReadReason(HttpContext http, string fallback) =>
            http.Request.Form.TryGetValue("reason", out var reason) ? reason.ToString() : fallback;

        private static bool ReadForce(HttpContext http) =>
            http.Request.Form.TryGetValue("force", out var force) && bool.TryParse(force.ToString(), out var parsed) && parsed;

        private static string[] ReadStrings(HttpContext http, string key) =>
            http.Request.Form[key].Select(value => value ?? string.Empty).ToArray();

        private static long[] ReadVersions(HttpContext http, string key) =>
            http.Request.Form[key].Select(value => long.TryParse(value, out var parsed) ? parsed : 0L).ToArray();

        private static void SaveBulkSummary(HttpContext http, ITempDataDictionaryFactory tempDataFactory, string message)
        {
            // Razor Pages saved TempData automatically; minimal APIs must persist it explicitly so the
            // CookieTempDataProvider writes the cookie that the dashboard reads after the redirect.
            var tempData = tempDataFactory.GetTempData(http);
            tempData[CatalogBulkOperations.TempDataKey] = message;
            tempData.Save();
        }

        private static HashSet<string> SoftDeletedJobIds(LifecycleReadModel lifecycle) =>
            lifecycle.GetLatestStates()
                .Where(state => state.Value.IsSoftDeleted)
                .Select(state => state.Key)
                .ToHashSet(StringComparer.Ordinal);
    }

    // Group-level CSRF guard for the Catalog POST endpoints. Mirrors the per-request validation the
    // Razor Pages antiforgery filter performed: resolve IAntiforgery from the request services and
    // validate before the handler runs. On failure ValidateRequestAsync throws
    // AntiforgeryValidationException, which is intentionally not caught here so the friendly-400
    // middleware in Program.cs renders the same response it does for the rest of the app.
    public sealed class AntiforgeryEndpointFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var antiforgery = context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
            await antiforgery.ValidateRequestAsync(context.HttpContext);
            return await next(context);
        }
    }
}
