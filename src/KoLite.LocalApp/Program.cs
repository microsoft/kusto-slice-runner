// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Schema;
using KoLite.LocalApp.Http;
using KoLite.LocalApp.Http.AgentApi;
using KoLite.LocalApp.Http.Control;
using KoLite.LocalApp.Http.Ui;
using Microsoft.AspNetCore.Antiforgery;

namespace KoLite.LocalApp
{
    public partial class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddRazorPages();
            builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
            builder.Services.AddOpenApi("v1");
            builder.Services.AddValidation();
            builder.Services.AddProblemDetails(options =>
            {
                options.CustomizeProblemDetails = context =>
                {
                    if (context.ProblemDetails.Extensions.ContainsKey("code"))
                    {
                        return;
                    }

                    context.ProblemDetails.Extensions["code"] = context.ProblemDetails is HttpValidationProblemDetails
                        ? "validation-failed"
                        : context.ProblemDetails.Status switch
                        {
                            StatusCodes.Status400BadRequest => "bad-request",
                            StatusCodes.Status403Forbidden => "forbidden",
                            StatusCodes.Status404NotFound => "not-found",
                            StatusCodes.Status405MethodNotAllowed => "method-not-allowed",
                            _ => "http-error"
                        };
                };
            });
            builder.Services.AddExceptionHandler<ApiExceptionHandler>();
            builder.Services.AddKoLiteServices(builder.Configuration);
            builder.WebHost.UseUrls(builder.Configuration["KoLite:Urls"] ?? "http://127.0.0.1:5057");

            var app = builder.Build();

            var databasePath = app.Services.GetRequiredService<KoLiteSqliteConnectionOptions>().DatabasePath;
            SingleInstanceGuard.Enforce(databasePath, app.Configuration, app.Logger);

            app.Services.GetRequiredService<KoLiteSqliteSchema>().EnsureSchema();

            app.Logger.LogInformation(
                "Kusto Slice Runner local SQLite database resolved to {DatabasePath}.",
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

            app.UseWhen(
                context => context.Request.Path.StartsWithSegments("/api/v1")
                    || context.Request.Path.StartsWithSegments("/ui-api/v1")
                    || context.Request.Path.StartsWithSegments("/control/v1"),
                branch =>
                {
                    branch.UseExceptionHandler();
                    branch.UseStatusCodePages();
                });
            app.UseWhen(
                context => context.Request.Path.StartsWithSegments("/api/v1/openapi"),
                branch => branch.UseMiddleware<LocalRequestMiddleware>());

            app.UseStaticFiles();

            HealthEndpoints.Map(app);
            app.MapAgentApi();
            app.MapOpenApi("/api/v1/openapi/{documentName}.json")
                .ExcludeFromDescription();
            ControlEndpoints.Map(app);
            FailureAnalysisEndpoints.Map(app);
            JobActionsEndpoints.Map(app);

            app.MapRazorPages();

            app.Run();
        }
    }
}
