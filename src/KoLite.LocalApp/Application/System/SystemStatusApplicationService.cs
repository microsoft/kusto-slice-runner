// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.Local.Core.Orchestration;
using KoLite.Local.Core.Time;
using KoLite.Local.Kusto.Execution;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Queue;
using KoLite.LocalApp.Retention;
using KoLite.LocalApp.Updates;

namespace KoLite.LocalApp.Application.System
{
    internal sealed record SystemStatusApplicationModel(
        string DatabasePath,
        int JobCount,
        string KustoAuthMode,
        LocalBackgroundSchedulerOptions SchedulerOptions,
        LocalBackgroundWorkerPoolOptions WorkerPoolOptions,
        WorkerPoolSnapshot WorkerPool,
        UpdateCheckSnapshot UpdateSnapshot,
        LocalUpdateCheckOptions UpdateOptions,
        RetentionSnapshot RetentionSnapshot,
        LocalRetentionOptions RetentionOptions,
        LocalShutdownDrainSnapshot Shutdown);

    internal sealed class SystemStatusApplicationService
    {
        private readonly KoLiteSqliteConnectionOptions databaseOptions;
        private readonly IKoLiteSqliteConnectionFactory connections;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteWorkQueueRepository queue;
        private readonly SqliteOperationalReadModelRepository observability;
        private readonly LocalBackgroundSchedulerOptions schedulerOptions;
        private readonly LocalBackgroundWorkerPoolOptions workerPoolOptions;
        private readonly LocalWorkerPoolRuntimeState workerPoolState;
        private readonly LocalWorkerOptions localWorkerOptions;
        private readonly KoLiteKustoOptions kustoOptions;
        private readonly LocalShutdownDrainCoordinator shutdownDrain;
        private readonly UpdateCheckRuntimeState updateCheckState;
        private readonly LocalUpdateCheckOptions updateCheckOptions;
        private readonly RetentionRuntimeState retentionState;
        private readonly LocalRetentionOptions retentionOptions;
        private readonly IClock clock;

        public SystemStatusApplicationService(
            KoLiteSqliteConnectionOptions databaseOptions,
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
            IClock clock)
        {
            this.databaseOptions = databaseOptions;
            this.connections = connections;
            this.catalog = catalog;
            this.queue = queue;
            this.observability = observability;
            this.schedulerOptions = schedulerOptions;
            this.workerPoolOptions = workerPoolOptions;
            this.workerPoolState = workerPoolState;
            this.localWorkerOptions = localWorkerOptions;
            this.kustoOptions = kustoOptions;
            this.shutdownDrain = shutdownDrain;
            this.updateCheckState = updateCheckState;
            this.updateCheckOptions = updateCheckOptions;
            this.retentionState = retentionState;
            this.retentionOptions = retentionOptions;
            this.clock = clock;
        }

        public SystemStatusApplicationModel Get()
        {
            using (var connection = connections.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT 1;";
                command.ExecuteScalar();
            }

            var nowUtc = clock.UtcNow;
            var queueStatus = observability.GetQueueStatus(localWorkerOptions.QueueName, nowUtc);
            var claimableBacklog = queue.CountClaimable(
                localWorkerOptions.QueueName,
                nowUtc,
                localWorkerOptions.EnforceJobParallelism,
                localWorkerOptions.EffectiveOrphanReclaimGrace);
            return new SystemStatusApplicationModel(
                databaseOptions.DatabasePath,
                catalog.List().Count,
                kustoOptions.AuthMode.ToString(),
                schedulerOptions,
                workerPoolOptions,
                workerPoolState.GetSnapshot(workerPoolOptions, queueStatus, claimableBacklog),
                updateCheckState.GetSnapshot(),
                updateCheckOptions,
                retentionState.GetSnapshot(),
                retentionOptions,
                shutdownDrain.GetSnapshot());
        }
    }
}
