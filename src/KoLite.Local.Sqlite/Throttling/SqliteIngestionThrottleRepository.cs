using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Throttling
{
    // A single recorded Kusto ingestion-capacity throttle hit by a slice attempt.
    public sealed record IngestionThrottleObservation(
        string JobId,
        string ClusterUri,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        int Attempt,
        int? ReportedCapacity,
        DateTimeOffset ObservedAtUtc);

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
                    (observation_id, job_id, cluster_uri, slice_start_utc, slice_end_utc, attempt, reported_capacity, observed_at_utc)
                VALUES ($id, $job, $cluster, $s, $e, $attempt, $capacity, $observed);
                """);
            cmd.Add("$id", Guid.NewGuid().ToString("N"));
            cmd.Add("$job", observation.JobId);
            cmd.Add("$cluster", observation.ClusterUri);
            cmd.Add("$s", SqliteStorage.Utc(observation.SliceStartUtc));
            cmd.Add("$e", SqliteStorage.Utc(observation.SliceEndUtc));
            cmd.Add("$attempt", observation.Attempt);
            cmd.Add("$capacity", observation.ReportedCapacity.HasValue ? observation.ReportedCapacity.Value : (object?)null);
            cmd.Add("$observed", SqliteStorage.Utc(observation.ObservedAtUtc));
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

        // Deletes observations older than the cutoff. Used by read-model retention to bound growth;
        // the rolling-window trigger uses a far shorter window, so pruning never affects detection.
        public int Prune(DateTimeOffset olderThanUtc)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, "DELETE FROM ingestion_throttle_observations WHERE observed_at_utc < $cutoff;");
            cmd.Add("$cutoff", SqliteStorage.Utc(olderThanUtc));
            return cmd.ExecuteNonQuery();
        }
    }
}
