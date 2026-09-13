using KoLite.Local.Core.Time;
using KoLite.Local.Core.Performance;

namespace KoLite.LocalApp.Performance
{
    public sealed class PerformanceCollectionBackgroundService : BackgroundService
    {
        private readonly IServiceScopeFactory scopes;
        private readonly LocalBackgroundSchedulerOptions executionOptions;
        private readonly LocalShutdownDrainCoordinator shutdownDrain;
        private readonly PerformanceCollectionSchedule schedule;
        private readonly IClock clock;
        private readonly ILogger<PerformanceCollectionBackgroundService> logger;

        public PerformanceCollectionBackgroundService(
            IServiceScopeFactory scopes,
            LocalBackgroundSchedulerOptions executionOptions,
            LocalShutdownDrainCoordinator shutdownDrain,
            PerformanceCollectionSchedule schedule,
            IClock clock,
            ILogger<PerformanceCollectionBackgroundService> logger)
        {
            ArgumentNullException.ThrowIfNull(schedule);
            schedule.Validate();
            this.scopes = scopes;
            this.executionOptions = executionOptions;
            this.shutdownDrain = shutdownDrain;
            this.schedule = schedule;
            this.clock = clock;
            this.logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!executionOptions.Enabled)
            {
                logger.LogInformation("Performance collection and backfill do not run in this execution-disabled instance.");
                return;
            }

            logger.LogInformation("Automatic performance statistics collection is active; reports read stored SQLite observations.");
            var initialize = true;
            var oldestFirst = false;
            while (!stoppingToken.IsCancellationRequested)
            {
                var historyComplete = true;
                if (!shutdownDrain.IsDrainRequested)
                {
                    try
                    {
                        using var scope = scopes.CreateScope();
                        var pass = scope.ServiceProvider.GetRequiredService<IPerformanceCollectionPass>();
                        historyComplete = await pass.RunAsync(initialize, oldestFirst, stoppingToken).ConfigureAwait(false);
                        initialize = false;
                        if (historyComplete)
                        {
                            oldestFirst = !oldestFirst;
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        var message = PerformanceCollectionPass.SanitizeError(ex);
                        logger.LogWarning("Performance collection pass failed and will retry: {Error}", message);
                        RecordFailure(message);
                    }
                }

                try
                {
                    await Task.Delay(historyComplete ? schedule.PassInterval : schedule.BackfillInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }

        private void RecordFailure(string message)
        {
            try
            {
                using var scope = scopes.CreateScope();
                scope.ServiceProvider.GetRequiredService<IPerformanceCollectionStore>()
                    .RecordCollectionFailure([], clock.UtcNow, message);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Could not persist performance collector failure: {Error}", PerformanceCollectionPass.SanitizeError(ex));
            }
        }
    }
}
