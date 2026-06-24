using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Observability
{
    public sealed record JobLifecycleEventRow(string JobId, string EventType, string? Reason, DateTimeOffset RecordedAtUtc);

    // Read-only access to the job_lifecycle_events log. The UI lifecycle read model projects these raw
    // rows into its latest-state dictionary and per-job history view models; this type performs no writes.
    public sealed class SqliteLifecycleReadModelRepository
    {
        private readonly IKoLiteSqliteConnectionFactory connectionFactory;

        public SqliteLifecycleReadModelRepository(IKoLiteSqliteConnectionFactory connectionFactory)
        {
            this.connectionFactory = connectionFactory;
        }

        // Every lifecycle event ordered by job then newest-first, so the caller keeps the first row per
        // job as that job's latest lifecycle state.
        public IReadOnlyList<JobLifecycleEventRow> GetLatestStateRows()
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT job_id, event_type, reason, recorded_at_utc
                FROM job_lifecycle_events
                ORDER BY job_id, recorded_at_utc DESC, lifecycle_event_id DESC;
                """);
            using var r = cmd.ExecuteReader();
            var rows = new List<JobLifecycleEventRow>();
            while (r.Read())
            {
                rows.Add(new JobLifecycleEventRow(
                    r.GetString(0),
                    r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2),
                    SqliteStorage.ReadUtc(r, "recorded_at_utc")));
            }

            return rows;
        }

        // Full lifecycle history for one job, newest first. JobId is carried from the parameter because
        // the WHERE clause fixes it for every returned row.
        public IReadOnlyList<JobLifecycleEventRow> GetHistory(string jobId)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT event_type, reason, recorded_at_utc
                FROM job_lifecycle_events
                WHERE job_id=$jobId
                ORDER BY recorded_at_utc DESC, lifecycle_event_id DESC;
                """);
            cmd.Add("$jobId", jobId);
            using var r = cmd.ExecuteReader();
            var rows = new List<JobLifecycleEventRow>();
            while (r.Read())
            {
                rows.Add(new JobLifecycleEventRow(
                    jobId,
                    r.GetString(0),
                    r.IsDBNull(1) ? null : r.GetString(1),
                    SqliteStorage.ReadUtc(r, "recorded_at_utc")));
            }

            return rows;
        }
    }
}
