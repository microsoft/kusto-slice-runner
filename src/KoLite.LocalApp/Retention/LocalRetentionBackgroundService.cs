// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KoLite.LocalApp.Retention
{
    // Periodically prunes non-authoritative operational telemetry (operational logs, terminal queue
    // rows, old completed attempts, scheduled-slice and ingestion-throttle records) older than the
    // configured window, so the local SQLite database stops growing without bound. The authoritative
    // window-history (current_slice_state, slice_state_events) and every catalog/lifecycle/audit/
    // rerun/repair row are preserved, so scheduler idempotency, rerun, dependency readiness, and the
    // started-job field guard are unaffected. Mirrors the update-check service's lifecycle: an
    // initial delay, then a periodic loop that pauses while a graceful drain is in progress.
    public sealed class LocalRetentionBackgroundService : BackgroundService
    {
        private readonly IServiceScopeFactory scopes;
        private readonly LocalRetentionOptions options;
        private readonly RetentionRuntimeState runtimeState;
        private readonly LocalShutdownDrainCoordinator shutdownDrain;
        private readonly IClock clock;
        private readonly ILogger<LocalRetentionBackgroundService> logger;

        public LocalRetentionBackgroundService(
            IServiceScopeFactory scopes,
            LocalRetentionOptions options,
            RetentionRuntimeState runtimeState,
            LocalShutdownDrainCoordinator shutdownDrain,
            IClock clock,
            ILogger<LocalRetentionBackgroundService> logger)
        {
            this.scopes = scopes;
            this.options = options;
            this.runtimeState = runtimeState;
            this.shutdownDrain = shutdownDrain;
            this.clock = clock;
            this.logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!options.Enabled)
            {
                logger.LogInformation("Kusto Slice Runner database retention is disabled; the local database will grow unbounded.");
                return;
            }

            logger.LogInformation(
                "Kusto Slice Runner database retention enabled: pruning operational telemetry older than {WindowDays:N0} days every {Interval}.",
                options.Window.TotalDays,
                options.Interval);

            try
            {
                await Task.Delay(options.InitialDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                if (!shutdownDrain.IsDrainRequested)
                {
                    RunRetention();
                }

                try
                {
                    await Task.Delay(options.Interval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private void RunRetention()
        {
            var nowUtc = clock.UtcNow;
            var cutoffUtc = nowUtc - options.Window;
            var protectedCutoffUtc = nowUtc - options.ProtectedWindow;

            try
            {
                using var scope = scopes.CreateScope();
                var observability = scope.ServiceProvider.GetRequiredService<SqliteOperationalReadModelRepository>();
                var result = observability.CleanupOldReadModels(cutoffUtc, protectedCutoffUtc, options.BatchSize);

                runtimeState.Update(RetentionSnapshot.Completed(nowUtc, result));

                if (result.TotalDeleted > 0)
                {
                    logger.LogInformation(
                        "Database retention pruned {Total} telemetry rows older than {Cutoff:O} (logs {Logs}, attempt details {Attempts}, performance facts {PerformanceAttempts}, scheduled {Scheduled}, queue {Queue}).",
                        result.TotalDeleted,
                        cutoffUtc,
                        result.LogsDeleted,
                        result.AttemptsDeleted,
                        result.PerformanceAttemptsDeleted,
                        result.ScheduledSlicesDeleted,
                        result.QueueRowsDeleted);
                }
                else
                {
                    logger.LogDebug("Database retention pass found nothing older than {Cutoff:O}.", cutoffUtc);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Database retention pass failed.");
                var previous = runtimeState.GetSnapshot();
                runtimeState.Update(previous with { LastError = ex.Message });
            }
        }
    }
}
