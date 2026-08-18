using KoLite.Local.Core.Orchestration;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Orchestration;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.State;

namespace KoLite.LocalApp
{
    internal sealed class LocalWorkerFactory
    {
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteSliceStateRepository state;
        private readonly SqliteChunkStateRepository chunkState;
        private readonly SqliteWorkQueueRepository queue;
        private readonly SqliteOperationalReadModelRepository observability;
        private readonly ILocalSliceOutputExecutor executor;
        private readonly IClock clock;
        private readonly LocalWorkerOptions options;
        private readonly ILocalWorkerProgressSink progressSink;

        public LocalWorkerFactory(
            SqliteJobCatalogRepository catalog,
            SqliteSliceStateRepository state,
            SqliteChunkStateRepository chunkState,
            SqliteWorkQueueRepository queue,
            SqliteOperationalReadModelRepository observability,
            ILocalSliceOutputExecutor executor,
            IClock clock,
            LocalWorkerOptions options,
            ILocalWorkerProgressSink progressSink)
        {
            this.catalog = catalog;
            this.state = state;
            this.chunkState = chunkState;
            this.queue = queue;
            this.observability = observability;
            this.executor = executor;
            this.clock = clock;
            this.options = options;
            this.progressSink = progressSink;
        }

        public SqliteLocalWorker Create(string workerId)
        {
            if (string.IsNullOrWhiteSpace(workerId)) throw new InvalidOperationException("Local worker ID must not be empty.");
            return new SqliteLocalWorker(catalog, state, queue, observability, executor, clock, options with { WorkerId = workerId }, progressSink, chunkState);
        }
    }
}
