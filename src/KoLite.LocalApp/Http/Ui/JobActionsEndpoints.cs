// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using KoLite.Local.Sqlite.Catalog;
using KoLite.LocalApp.Application;
using KoLite.LocalApp.Application.Jobs;
using KoLite.LocalApp.Pages.Catalog;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace KoLite.LocalApp.Http.Ui
{
    public static class JobActionsEndpoints
    {
        private const string Actor = "local-web";

        public static void Map(WebApplication app)
        {
            var single = app.MapGroup("/jobs/{jobId:guid}/actions")
                .AddEndpointFilter<UiAntiforgeryEndpointFilter>()
                .ExcludeFromDescription();

            single.MapPost("/pause", (
                HttpContext http,
                Guid jobId,
                JobApplicationService jobs,
                DashboardPageQuery dashboard,
                ITempDataDictionaryFactory tempDataFactory) =>
                CatalogEnabledToggle.Execute(
                    http,
                    jobs,
                    dashboard,
                    jobId.ToString("N"),
                    enabled: false,
                    ReadVersion(http),
                    tempDataFactory));

            single.MapPost("/resume", (
                HttpContext http,
                Guid jobId,
                JobApplicationService jobs,
                DashboardPageQuery dashboard,
                ITempDataDictionaryFactory tempDataFactory) =>
                CatalogEnabledToggle.Execute(
                    http,
                    jobs,
                    dashboard,
                    jobId.ToString("N"),
                    enabled: true,
                    ReadVersion(http),
                    tempDataFactory));

            single.MapPost("/soft-delete", (
                HttpContext http,
                Guid jobId,
                JobApplicationService jobs,
                ITempDataDictionaryFactory tempDataFactory) =>
            {
                var id = jobId.ToString("N");
                try
                {
                    jobs.SoftDelete(
                        id,
                        ReadVersion(http),
                        Actor,
                        ReadReason(http, "Soft deleted from web UI"),
                        ReadForce(http));
                    return Results.Redirect("/");
                }
                catch (ApplicationProblemException ex) when (ex.Code == "active-dependents")
                {
                    return Results.Redirect($"/jobs/{Uri.EscapeDataString(id)}/soft-delete-confirm");
                }
                catch (ApplicationProblemException ex) when (ex.Code == "etag-mismatch")
                {
                    CatalogConflictFeedback.Save(http, tempDataFactory);
                    return Results.Redirect($"/jobs/{Uri.EscapeDataString(id)}");
                }
            });

            single.MapPost("/restore", (
                HttpContext http,
                Guid jobId,
                JobApplicationService jobs,
                ITempDataDictionaryFactory tempDataFactory) =>
            {
                var id = jobId.ToString("N");
                try
                {
                    jobs.Restore(
                        id,
                        ReadVersion(http),
                        Actor,
                        ReadReason(http, "Restored from web UI"));
                    return Results.Redirect($"/jobs/{Uri.EscapeDataString(id)}");
                }
                catch (ApplicationProblemException ex) when (ex.Code == "etag-mismatch")
                {
                    CatalogConflictFeedback.Save(http, tempDataFactory);
                    return Results.Redirect($"/jobs/{Uri.EscapeDataString(id)}");
                }
            });

            var bulk = app.MapGroup("/jobs/actions")
                .AddEndpointFilter<UiAntiforgeryEndpointFilter>()
                .ExcludeFromDescription();

            bulk.MapPost("/pause-all", (
                HttpContext http,
                SqliteJobCatalogRepository catalog,
                LifecycleReadModel lifecycle,
                ITempDataDictionaryFactory tempDataFactory) =>
            {
                var result = CatalogBulkOperations.SetAllEnabled(catalog, lifecycle, targetEnabled: false);
                if (result.Conflicted > 0)
                {
                    SaveBulkSummary(http, tempDataFactory, result.ToMessage("Paused"));
                }

                return Results.Redirect("/");
            });

            bulk.MapPost("/resume-all", (
                HttpContext http,
                SqliteJobCatalogRepository catalog,
                LifecycleReadModel lifecycle,
                ITempDataDictionaryFactory tempDataFactory) =>
            {
                var result = CatalogBulkOperations.SetAllEnabled(catalog, lifecycle, targetEnabled: true);
                if (result.Conflicted > 0)
                {
                    SaveBulkSummary(http, tempDataFactory, result.ToMessage("Resumed"));
                }

                return Results.Redirect("/");
            });

            bulk.MapPost("/bulk/pause", (
                HttpContext http,
                SqliteJobCatalogRepository catalog,
                LifecycleReadModel lifecycle,
                ITempDataDictionaryFactory tempDataFactory) =>
            {
                var result = CatalogBulkOperations.SetEnabled(
                    catalog,
                    lifecycle,
                    ReadStrings(http, "jobIds"),
                    ReadVersions(http, "expectedVersions"),
                    targetEnabled: false);
                SaveBulkSummary(http, tempDataFactory, result.ToMessage("Paused"));
                return Results.Redirect("/");
            });

            bulk.MapPost("/bulk/resume", (
                HttpContext http,
                SqliteJobCatalogRepository catalog,
                LifecycleReadModel lifecycle,
                ITempDataDictionaryFactory tempDataFactory) =>
            {
                var result = CatalogBulkOperations.SetEnabled(
                    catalog,
                    lifecycle,
                    ReadStrings(http, "jobIds"),
                    ReadVersions(http, "expectedVersions"),
                    targetEnabled: true);
                SaveBulkSummary(http, tempDataFactory, result.ToMessage("Resumed"));
                return Results.Redirect("/");
            });

            bulk.MapPost("/bulk/soft-delete", (
                HttpContext http,
                SqliteJobCatalogRepository catalog,
                KoLite.Local.Sqlite.Lifecycle.SqliteJobLifecycleService lifecycle,
                LifecycleReadModel lifecycleReadModel,
                ITempDataDictionaryFactory tempDataFactory) =>
            {
                var result = CatalogBulkOperations.SoftDelete(
                    catalog,
                    lifecycle,
                    lifecycleReadModel,
                    ReadStrings(http, "jobIds"),
                    ReadVersions(http, "expectedVersions"));
                SaveBulkSummary(http, tempDataFactory, result.ToMessage("Soft-deleted"));
                return Results.Redirect("/");
            });

            bulk.MapPost("/bulk/export", (HttpContext http, SqliteJobCatalogRepository catalog) =>
            {
                var json = catalog.ExportSelected(ReadStrings(http, "jobIds"));
                return Results.File(Encoding.UTF8.GetBytes(json), "application/json", "ko-lite-jobs.json");
            });

            app.MapGet("/jobs/{jobId:guid}/export", (Guid jobId, JobApplicationService jobs) =>
                {
                    try
                    {
                        return Results.Content(
                            AppFormatting.PrettyJson(jobs.Export(jobId.ToString("N"))),
                            "application/json");
                    }
                    catch (ApplicationProblemException)
                    {
                        return Results.NotFound();
                    }
                })
                .ExcludeFromDescription();

            app.MapGet("/jobs/export", (JobApplicationService jobs) =>
                    Results.Content(jobs.ExportAll(), "application/json"))
                .ExcludeFromDescription();
        }

        private static long ReadVersion(HttpContext http)
        {
            return long.TryParse(http.Request.Form["expectedVersion"].ToString(), out var version)
                ? version
                : 0L;
        }

        private static string ReadReason(HttpContext http, string fallback)
        {
            return http.Request.Form.TryGetValue("reason", out var reason)
                && !string.IsNullOrWhiteSpace(reason)
                ? reason.ToString()
                : fallback;
        }

        private static bool ReadForce(HttpContext http)
        {
            return http.Request.Form.TryGetValue("force", out var force)
                && bool.TryParse(force.ToString(), out var parsed)
                && parsed;
        }

        private static string[] ReadStrings(HttpContext http, string key)
        {
            return http.Request.Form[key].Select(value => value ?? string.Empty).ToArray();
        }

        private static long[] ReadVersions(HttpContext http, string key)
        {
            return http.Request.Form[key]
                .Select(value => long.TryParse(value, out var parsed) ? parsed : 0L)
                .ToArray();
        }

        private static void SaveBulkSummary(
            HttpContext http,
            ITempDataDictionaryFactory tempDataFactory,
            string message)
        {
            var tempData = tempDataFactory.GetTempData(http);
            tempData[CatalogBulkOperations.TempDataKey] = message;
            tempData.Save();
        }
    }
}
