// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Observability;
using KoLite.LocalApp.Http;
using KoLite.LocalApp.Http.AgentApi;
using Microsoft.AspNetCore.Http.HttpResults;

namespace KoLite.LocalApp.Http.Control
{
    public static class ControlEndpoints
    {
        public static void Map(WebApplication app)
        {
            var control = app.MapGroup("/control/v1")
                .AddEndpointFilter<LocalRequestEndpointFilter>()
                .ExcludeFromDescription();

            control.MapGet("/shutdown", (LocalShutdownDrainCoordinator shutdown) =>
                TypedResults.Ok(Map(shutdown.GetSnapshot())));

            control.MapPost("/shutdown/drain", RequestDrain);
        }

        private static Ok<ShutdownStatusResponse> RequestDrain(
            ShutdownDrainRequest? request,
            HttpContext context,
            LocalShutdownDrainCoordinator shutdownDrain,
            IHostApplicationLifetime appLifetime,
            IServiceScopeFactory scopes,
            IClock clock,
            ILoggerFactory loggerFactory)
        {
            var snapshot = shutdownDrain.RequestDrain(
                clock.UtcNow,
                string.IsNullOrWhiteSpace(request?.Reason) ? null : request.Reason.Trim());

            context.Response.OnCompleted(() =>
            {
                if (shutdownDrain.TryStartStopWhenDrained())
                {
                    var logger = loggerFactory.CreateLogger("LocalShutdownDrain");
                    _ = Task.Run(async () =>
                    {
                        await shutdownDrain.WaitForDrainedAsync().ConfigureAwait(false);
                        var stoppingSnapshot = shutdownDrain.MarkStopping(clock.UtcNow);
                        try
                        {
                            using var scope = scopes.CreateScope();
                            var observability = scope.ServiceProvider.GetRequiredService<SqliteOperationalReadModelRepository>();
                            observability.RecordLog(
                                "Information",
                                "Graceful drain completed; stopping Kusto Slice Runner local app.",
                                "shutdown-drain",
                                propertiesJson: JsonSerializer.Serialize(stoppingSnapshot));
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to record graceful drain completion before stopping the app.");
                        }

                        logger.LogInformation("Graceful drain completed; stopping Kusto Slice Runner local app.");
                        appLifetime.StopApplication();
                    });
                }

                return Task.CompletedTask;
            });

            return TypedResults.Ok(Map(snapshot));
        }

        private static ShutdownStatusResponse Map(LocalShutdownDrainSnapshot snapshot)
        {
            return new ShutdownStatusResponse(
                snapshot.Mode,
                snapshot.IsDrainRequested,
                snapshot.ActiveWorkerCount,
                snapshot.RequestedAtUtc,
                snapshot.Reason,
                snapshot.LastActiveWorkerTransitionUtc);
        }
    }
}
