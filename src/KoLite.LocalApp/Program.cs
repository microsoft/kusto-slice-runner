using System.Net;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Schema;
using KoLite.LocalApp.Api;
using Microsoft.AspNetCore.Antiforgery;

namespace KoLite.LocalApp
{
    public partial class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddRazorPages().AddRazorPagesOptions(options =>
            {
                // Preserve the historical GET entry URLs after merging the New/Create and
                // Edit/Update page splits into single self-posting pages.
                options.Conventions.AddPageRoute("/Catalog/Create", "/catalog/new");
                options.Conventions.AddPageRoute("/Catalog/Update", "/catalog/{jobId}/edit");
            });
            builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
            builder.Services.AddKoLiteServices(builder.Configuration);
            builder.WebHost.UseUrls(builder.Configuration["KoLite:Urls"] ?? "http://127.0.0.1:5057");

            var app = builder.Build();

            var databasePath = app.Services.GetRequiredService<KoLiteSqliteConnectionOptions>().DatabasePath;
            SingleInstanceGuard.Enforce(databasePath, app.Configuration, app.Logger);

            app.Services.GetRequiredService<KoLiteSqliteSchema>().EnsureSchema();

            app.Logger.LogInformation(
                "KO Lite local SQLite database resolved to {DatabasePath}.",
                databasePath);

            app.Use(async (context, next) =>
            {
                try
                {
                    await next(context);
                }
                catch (AntiforgeryValidationException ex) when (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    context.Response.ContentType = "text/html; charset=utf-8";
                    await context.Response.WriteAsync($"""
                    <!doctype html>
                    <html lang="en">
                    <head><meta charset="utf-8"><title>Invalid request</title></head>
                    <body><h1>Invalid request</h1><p>{WebUtility.HtmlEncode(ex.Message)}</p></body>
                    </html>
                    """);
                }
            });

            // Bookmark continuity after the activityId -> GUID re-key: a GET to /jobs/{x} or
            // /catalog/{x}/... where {x} is not a known job id but matches a job's activityId
            // redirects to the canonical GUID URL.
            app.Use(async (context, next) =>
            {
                var path = context.Request.Path.Value;
                if (HttpMethods.IsGet(context.Request.Method) && path is not null)
                {
                    var segments = path.Split('/');
                    if (segments.Length >= 3 && (segments[1] == "jobs" || segments[1] == "catalog") && segments[2].Length > 0)
                    {
                        var candidate = Uri.UnescapeDataString(segments[2]);
                        var catalog = context.RequestServices.GetRequiredService<SqliteJobCatalogRepository>();
                        if (catalog.Get(candidate) is null && catalog.GetByActivityId(candidate) is { } resolved)
                        {
                            segments[2] = Uri.EscapeDataString(resolved.JobId);
                            context.Response.Redirect(string.Join('/', segments) + context.Request.QueryString.Value, permanent: false);
                            return;
                        }
                    }
                }

                await next(context);
            });

            app.UseStaticFiles();

            StatusEndpoints.Map(app);

            // One loopback guard for every /api endpoint (current and future) via a single group filter,
            // instead of repeating the check in each handler. Endpoints register relative to "/api".
            var api = app.MapGroup("/api").AddEndpointFilter<LoopbackEndpointFilter>();

            LocalCatalogApi.Map(api);

            LocalDiagnosticsApi.Map(api);

            LocalRepairApi.Map(api);

            KustoConsumersApi.Map(api);

            FailureAnalysisApi.Map(api);

            // The Catalog action endpoints (the former action-only Razor Pages) as a minimal-API group.
            // Mapped at root (not under /api) so the existing /catalog/... URLs are preserved exactly.
            CatalogActionsApi.Map(app);

            app.MapRazorPages();

            app.Run();
        }
    }
}
