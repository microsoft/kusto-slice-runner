// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using Ksr.Local.Core.Orchestration;
using Ksr.Local.Core.Time;
using Ksr.Local.Sqlite.Observability;
using Ksr.Local.Sqlite.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ksr.LocalApp
{
    internal sealed class LocalBackgroundSchedulerService : BackgroundService
    {
        private readonly IServiceScopeFactory scopes;
        private readonly LocalBackgroundSchedulerOptions options;
        private readonly LocalShutdownDrainCoordinator shutdownDrain;
        private readonly ILogger<LocalBackgroundSchedulerService> logger;

        public LocalBackgroundSchedulerService(
            IServiceScopeFactory scopes,
            LocalBackgroundSchedulerOptions options,
            LocalShutdownDrainCoordinator shutdownDrain,
            ILogger<LocalBackgroundSchedulerService> logger)
        {
            this.scopes = scopes;
            this.options = options;
            this.shutdownDrain = shutdownDrain;
            this.logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!options.Enabled) return;

            while (!stoppingToken.IsCancellationRequested)
            {
                if (!shutdownDrain.IsDrainRequested)
                {
                    try
                    {
                        await RunPassAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // A single failed scheduler pass must never stop the host. The SQLite errors
                        // seen here (e.g. a transient SQLITE_ABORT under heavy concurrency) are
                        // transient, so log and continue — the next tick recovers. Without this guard
                        // the default HostOptions.BackgroundServiceExceptionBehavior = StopHost tears
                        // down the whole app (UI, workers, scheduler) on one hiccup. This mirrors the
                        // resilience already present in the worker and retention background services.
                        logger.LogError(ex, "Local scheduler pass failed; the scheduler will continue on the next tick.");
                        TryRecordSchedulerPassFailure(ex);
                    }
                }

                try
                {
                    await Task.Delay(options.TickInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private void TryRecordSchedulerPassFailure(Exception ex)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var observability = scope.ServiceProvider.GetRequiredService<SqliteOperationalReadModelRepository>();
                var propertiesJson = JsonSerializer.Serialize(
                    new
                    {
                        error = ex.Message,
                        exceptionType = ex.GetType().FullName,
                    },
                    SchedulerPassLogJson.Options);
                observability.RecordLog("Error", "Scheduler pass failed.", "scheduler-pass", propertiesJson: propertiesJson);
            }
            catch (Exception recordEx)
            {
                // The failure recorder must itself be resilient: if the database is the thing
                // struggling, RecordLog can fail too. Never let observability bookkeeping crash the loop.
                logger.LogDebug(recordEx, "Failed to record scheduler pass failure to operational logs.");
            }
        }

        private async Task RunPassAsync(CancellationToken cancellationToken)
        {
            using var scope = scopes.CreateScope();
            var scheduler = scope.ServiceProvider.GetRequiredService<SqliteLocalScheduler>();
            var observability = scope.ServiceProvider.GetRequiredService<SqliteOperationalReadModelRepository>();
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();
            var passStartedAtUtc = clock.UtcNow;
            var passStartedTimestamp = Stopwatch.GetTimestamp();

            var tick = scheduler.Tick();
            var passDuration = Stopwatch.GetElapsedTime(passStartedTimestamp);
            var passCompletedAtUtc = clock.UtcNow;

            if (tick.Enqueued > 0)
            {
                logger.LogInformation("Local scheduler pass enqueued {Enqueued} slices.", tick.Enqueued);
            }

            if (options.LogEveryPass)
            {
                LogSchedulerPass(observability, passStartedAtUtc, passCompletedAtUtc, passDuration, tick);
            }
        }

        private void LogSchedulerPass(
            SqliteOperationalReadModelRepository observability,
            DateTimeOffset passStartedAtUtc,
            DateTimeOffset passCompletedAtUtc,
            TimeSpan passDuration,
            LocalSchedulerTickResult tick)
        {
            var durationMs = Math.Round(passDuration.TotalMilliseconds, 3);
            var propertiesJson = JsonSerializer.Serialize(new
            {
                passStartedAtUtc,
                passCompletedAtUtc,
                durationMs,
                configuredTickInterval = options.TickInterval.ToString(),
                enqueued = tick.Enqueued,
                dependencyBlocked = tick.DependencyBlocked,
                skippedCompleted = tick.SkippedCompleted,
                skippedMaxParallelism = tick.SkippedMaxParallelism,
                workerDispatchDecoupled = true
            }, SchedulerPassLogJson.Options);

            observability.RecordLog(
                "Information",
                "Scheduler pass completed.",
                "scheduler-pass",
                propertiesJson: propertiesJson);
            logger.LogInformation(
                "Scheduler pass completed in {DurationMs} ms with interval {ConfiguredTickInterval}: enqueued {Enqueued}, dependency blocked {DependencyBlocked}, skipped completed {SkippedCompleted}, skipped max parallelism {SkippedMaxParallelism}.",
                durationMs,
                options.TickInterval,
                tick.Enqueued,
                tick.DependencyBlocked,
                tick.SkippedCompleted,
                tick.SkippedMaxParallelism);
        }
    }
}
