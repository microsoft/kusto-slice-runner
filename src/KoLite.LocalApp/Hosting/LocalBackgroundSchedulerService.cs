using System.Diagnostics;
using System.Text.Json;
using KoLite.Local.Core.Orchestration;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KoLite.LocalApp
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
                    await RunPassAsync(stoppingToken);
                }

                await Task.Delay(options.TickInterval, stoppingToken);
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
