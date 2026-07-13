using System.Text.Json;
using KoLite.Local.Core.Orchestration;
using KoLite.Local.Core.Time;
using KoLite.Local.Kusto.Execution;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Queue;
using KoLite.LocalApp.Retention;
using KoLite.LocalApp.Updates;

namespace KoLite.LocalApp.Api
{
    // Localhost status surface extracted verbatim from Program.cs: a health probe
    // (/status/health) reporting SQLite, catalog, scheduler, worker-pool, update-check, and
    // shutdown state, plus the graceful-drain controls (/status/shutdown reads the snapshot,
    // /status/shutdown/drain is loopback-guarded and defers StopApplication until the response
    // is flushed).
    public static class StatusEndpoints
    {
        public static void Map(WebApplication app)
        {
            app.MapGet("/status/health", (
                KoLiteSqliteConnectionOptions options,
                IKoLiteSqliteConnectionFactory connections,
                SqliteJobCatalogRepository catalog,
                SqliteWorkQueueRepository queue,
                SqliteOperationalReadModelRepository observability,
                LocalBackgroundSchedulerOptions schedulerOptions,
                LocalBackgroundWorkerPoolOptions workerPoolOptions,
                LocalWorkerPoolRuntimeState workerPoolState,
                LocalWorkerOptions localWorkerOptions,
                KoLiteKustoOptions kustoOptions,
                LocalShutdownDrainCoordinator shutdownDrain,
                UpdateCheckRuntimeState updateCheckState,
                LocalUpdateCheckOptions updateCheckOptions,
                RetentionRuntimeState retentionState,
                LocalRetentionOptions retentionOptions,
                IClock clock) =>
            {
                using var connection = connections.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1;";
                command.ExecuteScalar();
                var nowUtc = clock.UtcNow;
                var queueStatus = observability.GetQueueStatus(localWorkerOptions.QueueName, nowUtc);
                var claimableBacklog = queue.CountClaimable(localWorkerOptions.QueueName, nowUtc, localWorkerOptions.EnforceJobParallelism, localWorkerOptions.EffectiveOrphanReclaimGrace);
                var updateSnapshot = updateCheckState.GetSnapshot();
                var retentionSnapshot = retentionState.GetSnapshot();
                return Results.Json(new
                {
                    status = "Healthy",
                    sqlite = "OK",
                    databasePath = options.DatabasePath,
                    jobCount = catalog.List().Count,
                    liveKustoExecution = "Enabled",
                    kustoAuthMode = kustoOptions.AuthMode.ToString(),
                    scheduler = new
                    {
                        enabled = schedulerOptions.Enabled,
                        tickInterval = schedulerOptions.TickInterval.ToString(),
                        maxWorkerIterations = workerPoolOptions.MaxDispatchStartsPerCycle,
                        workerConcurrency = workerPoolOptions.MaxConcurrencyDisplay,
                        logEveryPass = schedulerOptions.LogEveryPass
                    },
                    workerPool = workerPoolState.GetSnapshot(workerPoolOptions, queueStatus, claimableBacklog),
                    updateCheck = new
                    {
                        status = updateSnapshot.Status.ToString(),
                        reason = updateSnapshot.Reason.ToString(),
                        enabled = updateCheckOptions.Enabled,
                        repository = updateCheckOptions.Repository,
                        channel = "latest-release",
                        interval = updateCheckOptions.Interval.ToString(),
                        builtSha = updateSnapshot.BuiltSha,
                        remoteSha = updateSnapshot.RemoteSha,
                        latestVersion = updateSnapshot.LatestVersion,
                        releaseUrl = updateSnapshot.ReleaseUrl,
                        commitsBehind = updateSnapshot.CommitsBehind,
                        commitsAhead = updateSnapshot.CommitsAhead,
                        lastCheckedUtc = updateSnapshot.LastCheckedUtc,
                        error = updateSnapshot.ErrorMessage
                    },
                    retention = new
                    {
                        enabled = retentionOptions.Enabled,
                        windowDays = retentionOptions.Window.TotalDays,
                        interval = retentionOptions.Interval.ToString(),
                        lastRunUtc = retentionSnapshot.LastRunUtc,
                        lastRunDeleted = retentionSnapshot.TotalDeleted,
                        logsDeleted = retentionSnapshot.LogsDeleted,
                        attemptsDeleted = retentionSnapshot.AttemptsDeleted,
                        scheduledSlicesDeleted = retentionSnapshot.ScheduledSlicesDeleted,
                        ingestionThrottlesDeleted = retentionSnapshot.IngestionThrottlesDeleted,
                        queueRowsDeleted = retentionSnapshot.QueueRowsDeleted,
                        error = retentionSnapshot.LastError
                    },
                    shutdown = shutdownDrain.GetSnapshot()
                });
            });

            app.MapGet("/status/shutdown", (LocalShutdownDrainCoordinator shutdownDrain) => Results.Json(shutdownDrain.GetSnapshot()));

            app.MapPost("/status/shutdown/drain", (
                HttpContext context,
                LocalShutdownDrainCoordinator shutdownDrain,
                IHostApplicationLifetime appLifetime,
                IServiceScopeFactory scopes,
                IClock clock,
                ILoggerFactory loggerFactory) =>
            {
                if (!LocalShutdownDrainCoordinator.IsAllowedDrainRemote(context.Connection.RemoteIpAddress))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                var snapshot = shutdownDrain.RequestDrain(clock.UtcNow, context.Request.Query["reason"].ToString());

                // Defer the drain-wait-then-stop work until the HTTP response has been fully
                // sent. When the app is already drained (worker pool disabled or no active work),
                // WaitForDrainedAsync completes immediately, so calling StopApplication() inline
                // can abort Kestrel's in-flight response before it is flushed and surface as
                // "The response ended prematurely." on the client. The start-once guard is taken
                // inside the callback so a dropped response does not permanently consume it.
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
                                    "Graceful drain completed; stopping KO Lite local app.",
                                    "shutdown-drain",
                                    propertiesJson: JsonSerializer.Serialize(stoppingSnapshot));
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, "Failed to record graceful drain completion before stopping the app.");
                            }

                            logger.LogInformation("Graceful drain completed; stopping KO Lite local app.");
                            appLifetime.StopApplication();
                        });
                    }

                    return Task.CompletedTask;
                });

                return Results.Json(snapshot);
            });
        }
    }
}
