using KoLite.Local.Sqlite.Observability;

namespace KoLite.LocalApp.Ui
{
    public sealed record JobLifecycleProjection(string JobId, string? LastEventType, string? Reason, DateTimeOffset? RecordedAtUtc)
    {
        public bool IsSoftDeleted => string.Equals(LastEventType, "SoftDeleted", StringComparison.Ordinal);
    }

    public sealed record JobLifecycleHistoryRow(string EventType, string? Reason, DateTimeOffset RecordedAtUtc);

    public sealed class LifecycleReadModel
    {
        private readonly SqliteLifecycleReadModelRepository repository;

        public LifecycleReadModel(SqliteLifecycleReadModelRepository repository)
        {
            this.repository = repository;
        }

        public IReadOnlyDictionary<string, JobLifecycleProjection> GetLatestStates()
        {
            var results = new Dictionary<string, JobLifecycleProjection>(StringComparer.Ordinal);
            foreach (var row in repository.GetLatestStateRows())
            {
                if (results.ContainsKey(row.JobId))
                {
                    continue;
                }

                results[row.JobId] = new JobLifecycleProjection(row.JobId, row.EventType, row.Reason, row.RecordedAtUtc);
            }

            return results;
        }

        public IReadOnlyList<JobLifecycleHistoryRow> GetHistory(string jobId) =>
            repository.GetHistory(jobId)
                .Select(row => new JobLifecycleHistoryRow(row.EventType, row.Reason, row.RecordedAtUtc))
                .ToList();
    }
}

