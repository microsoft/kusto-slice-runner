using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Throttling
{
    // A single recorded Kusto ingestion-capacity throttle hit by a slice attempt. Terminal is true
    // when this hit was the slice's dead-letter attempt (it gave up after consecutive throttled
    // attempts), the worst throttling outcome.
    public sealed record IngestionThrottleObservation(
        string JobId,
        string ClusterUri,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        int Attempt,
        int? ReportedCapacity,
        DateTimeOffset ObservedAtUtc,
        bool Terminal = false);

    // Per-cluster aggregation of throttle observations within a rolling window. Drives the
    // sustained-throttle trigger (ThrottledSliceCount counts distinct slices, so retries of a single
    // hot slice do not by themselves look like broad cluster pressure).
    public sealed record ClusterThrottleWindowSummary(
        string ClusterUri,
        int ThrottledSliceCount,
        int ObservationCount,
        int? LatestReportedCapacity,
        DateTimeOffset FirstObservedUtc,
        DateTimeOffset LatestObservedUtc);

    // A slice that has a terminal (dead-letter) ingestion-throttle observation and is still in a
    // failed state. ThrottledAttempts is how many times the slice was throttled; CurrentState is the
    // slice's present durable state. JobId is resolved to an ActivityId by the read model.
    public sealed record TerminalThrottleSlice(
        string JobId,
        string ClusterUri,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        int ThrottledAttempts,
        DateTimeOffset LastObservedUtc,
        string CurrentState);

    // Append-only store of ingestion-capacity throttle observations. Stateless over the singleton
    // connection factory, so it is safe to register as a singleton and call from the (singleton)
    // worker progress sink as well as scoped UI read models.
    public sealed class SqliteIngestionThrottleRepository
    {
        private readonly IKoLiteSqliteConnectionFactory connectionFactory;

        public SqliteIngestionThrottleRepository(IKoLiteSqliteConnectionFactory connectionFactory)
        {
            this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        }

        public void Record(IngestionThrottleObservation observation)
        {
            ArgumentNullException.ThrowIfNull(observation);
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                INSERT INTO ingestion_throttle_observations
                    (observation_id, job_id, cluster_uri, slice_start_utc, slice_end_utc, attempt, reported_capacity, observed_at_utc, terminal)
                VALUES ($id, $job, $cluster, $s, $e, $attempt, $capacity, $observed, $terminal);
                """);
            cmd.Add("$id", Guid.NewGuid().ToString("N"));
            cmd.Add("$job", observation.JobId);
            cmd.Add("$cluster", observation.ClusterUri);
            cmd.Add("$s", SqliteStorage.Utc(observation.SliceStartUtc));
            cmd.Add("$e", SqliteStorage.Utc(observation.SliceEndUtc));
            cmd.Add("$attempt", observation.Attempt);
            cmd.Add("$capacity", observation.ReportedCapacity.HasValue ? observation.ReportedCapacity.Value : (object?)null);
            cmd.Add("$observed", SqliteStorage.Utc(observation.ObservedAtUtc));
            cmd.Add("$terminal", observation.Terminal ? 1 : 0);
            cmd.ExecuteNonQuery();
        }

        // Summarizes throttle pressure per cluster over [sinceUtc, now]. Clusters with no observations
        // in the window are absent from the result.
        public IReadOnlyList<ClusterThrottleWindowSummary> SummarizeWindow(DateTimeOffset sinceUtc)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT o.cluster_uri AS cluster_uri,
                       COUNT(DISTINCT o.job_id || '|' || o.slice_start_utc || '|' || o.slice_end_utc) AS throttled_slice_count,
                       COUNT(*) AS observation_count,
                       MIN(o.observed_at_utc) AS first_observed,
                       MAX(o.observed_at_utc) AS latest_observed,
                       (SELECT o2.reported_capacity FROM ingestion_throttle_observations o2
                        WHERE o2.cluster_uri = o.cluster_uri AND o2.observed_at_utc >= $since
                        ORDER BY o2.observed_at_utc DESC LIMIT 1) AS latest_capacity
                FROM ingestion_throttle_observations o
                WHERE o.observed_at_utc >= $since
                GROUP BY o.cluster_uri
                ORDER BY throttled_slice_count DESC, cluster_uri;
                """);
            cmd.Add("$since", SqliteStorage.Utc(sinceUtc));
            using var r = cmd.ExecuteReader();
            var results = new List<ClusterThrottleWindowSummary>();
            while (r.Read())
            {
                var capacityOrdinal = r.GetOrdinal("latest_capacity");
                results.Add(new ClusterThrottleWindowSummary(
                    r.GetString(r.GetOrdinal("cluster_uri")),
                    r.GetInt32(r.GetOrdinal("throttled_slice_count")),
                    r.GetInt32(r.GetOrdinal("observation_count")),
                    r.IsDBNull(capacityOrdinal) ? null : r.GetInt32(capacityOrdinal),
                    SqliteStorage.ReadUtc(r, "first_observed"),
                    SqliteStorage.ReadUtc(r, "latest_observed")));
            }

            return results;
        }

        // Distinct job ids that hit an ingestion throttle on a cluster within the window. Used to keep
        // the throttled job itself in the advisory even when it is momentarily between in-flight slices.
        public IReadOnlySet<string> ListThrottledJobIds(string clusterUri, DateTimeOffset sinceUtc)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, "SELECT DISTINCT job_id FROM ingestion_throttle_observations WHERE cluster_uri = $cluster AND observed_at_utc >= $since;");
            cmd.Add("$cluster", clusterUri);
            cmd.Add("$since", SqliteStorage.Utc(sinceUtc));
            using var r = cmd.ExecuteReader();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            while (r.Read())
            {
                ids.Add(r.GetString(0));
            }

            return ids;
        }

        // Slices that terminally failed (dead-lettered) on consecutive ingestion-throttle attempts and
        // are still unresolved (current state Failed/DeadLettered), with their latest throttle within
        // sinceUtc. Grouping per slice and gating on MAX(terminal)=1 keeps only slices whose final
        // attempt was a throttle. Ordered most-recent first.
        public IReadOnlyList<TerminalThrottleSlice> ListUnresolvedTerminalFailures(DateTimeOffset sinceUtc, int cap = 100)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT o.job_id AS job_id,
                       o.cluster_uri AS cluster_uri,
                       o.slice_start_utc AS slice_start_utc,
                       o.slice_end_utc AS slice_end_utc,
                       COUNT(*) AS throttled_attempts,
                       MAX(o.observed_at_utc) AS last_observed,
                       css.state AS state
                FROM ingestion_throttle_observations o
                JOIN current_slice_state css
                    ON css.job_id = o.job_id
                   AND css.slice_start_utc = o.slice_start_utc
                   AND css.slice_end_utc = o.slice_end_utc
                WHERE css.state IN ('Failed', 'DeadLettered')
                GROUP BY o.job_id, o.cluster_uri, o.slice_start_utc, o.slice_end_utc, css.state
                HAVING MAX(o.terminal) = 1 AND MAX(o.observed_at_utc) >= $since
                ORDER BY last_observed DESC
                LIMIT $cap;
                """);
            cmd.Add("$since", SqliteStorage.Utc(sinceUtc));
            cmd.Add("$cap", cap);
            using var r = cmd.ExecuteReader();
            var results = new List<TerminalThrottleSlice>();
            while (r.Read())
            {
                results.Add(new TerminalThrottleSlice(
                    r.GetString(r.GetOrdinal("job_id")),
                    r.GetString(r.GetOrdinal("cluster_uri")),
                    SqliteStorage.ReadUtc(r, "slice_start_utc"),
                    SqliteStorage.ReadUtc(r, "slice_end_utc"),
                    r.GetInt32(r.GetOrdinal("throttled_attempts")),
                    SqliteStorage.ReadUtc(r, "last_observed"),
                    r.GetString(r.GetOrdinal("state"))));
            }

            return results;
        }
    }
}
