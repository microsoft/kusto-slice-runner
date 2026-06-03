using KoLite.Local.Sqlite.Connections;

namespace KoLite.LocalApp.Ui
{
    public sealed record JobLifecycleProjection(string JobId, string? LastEventType, string? Reason, DateTimeOffset? RecordedAtUtc)
    {
        public bool IsSoftDeleted => string.Equals(LastEventType, "SoftDeleted", StringComparison.Ordinal);
    }

    public sealed record JobLifecycleHistoryRow(string EventType, string? Reason, DateTimeOffset RecordedAtUtc);

    public sealed class LifecycleReadModel
    {
        private readonly IKoLiteSqliteConnectionFactory connectionFactory;

        public LifecycleReadModel(IKoLiteSqliteConnectionFactory connectionFactory)
        {
            this.connectionFactory = connectionFactory;
        }

        public IReadOnlyDictionary<string, JobLifecycleProjection> GetLatestStates()
        {
            using var connection = connectionFactory.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT job_id, event_type, reason, recorded_at_utc
                FROM job_lifecycle_events
                ORDER BY job_id, recorded_at_utc DESC, lifecycle_event_id DESC;
                """;

            using var reader = command.ExecuteReader();
            var results = new Dictionary<string, JobLifecycleProjection>(StringComparer.Ordinal);
            while (reader.Read())
            {
                var jobId = reader.GetString(0);
                if (results.ContainsKey(jobId))
                {
                    continue;
                }

                results[jobId] = new JobLifecycleProjection(
                    jobId,
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    SqliteUi.ParseUtc(reader.GetString(3)));
            }

            return results;
        }

        public IReadOnlyList<JobLifecycleHistoryRow> GetHistory(string jobId)
        {
            using var connection = connectionFactory.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT event_type, reason, recorded_at_utc
                FROM job_lifecycle_events
                WHERE job_id=$jobId
                ORDER BY recorded_at_utc DESC, lifecycle_event_id DESC;
                """;
            command.Add("$jobId", jobId);

            using var reader = command.ExecuteReader();
            var rows = new List<JobLifecycleHistoryRow>();
            while (reader.Read())
            {
                rows.Add(new JobLifecycleHistoryRow(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    SqliteUi.ParseUtc(reader.GetString(2))));
            }

            return rows;
        }
    }
}
