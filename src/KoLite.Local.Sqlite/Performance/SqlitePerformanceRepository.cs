using System.Text.Json;
using KoLite.Local.Core.Performance;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Infrastructure;

namespace KoLite.Local.Sqlite.Performance
{
    public sealed partial class SqlitePerformanceRepository : IPerformanceReportRepository, IPerformanceCollectionStore
    {
        internal static readonly TimeSpan HistoryWindow = TimeSpan.FromDays(30);
        internal const double BytesPerGiB = 1024d * 1024d * 1024d;
        internal static readonly double MaxMetricSeconds = TimeSpan.MaxValue.TotalSeconds;
        private readonly IKoLiteSqliteConnectionFactory connectionFactory;

        public SqlitePerformanceRepository(IKoLiteSqliteConnectionFactory connectionFactory)
        {
            this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        }

        internal static string AggregateSql(bool filtered) => """
            SELECT job_id, chunk_id, status,
                   CASE WHEN typeof(cpu_seconds) IN ('integer','real') AND cpu_seconds BETWEEN 0 AND $maxSeconds THEN cpu_seconds END,
                   CASE WHEN typeof(duration_seconds) IN ('integer','real') AND duration_seconds BETWEEN 0 AND $maxSeconds THEN duration_seconds END,
                   CASE WHEN typeof(memory_peak_bytes) = 'integer' AND memory_peak_bytes >= 0 THEN memory_peak_bytes END,
                   duplicate_suppressed, server_activity_id,
                   CASE WHEN status='Succeeded' AND duplicate_suppressed IS NOT 1
                             AND completed_at_utc < $coverageCutoff THEN 1 ELSE 0 END
            FROM performance_attempts
            WHERE completed_at_utc >= $from AND completed_at_utc < $to
              AND status IN ('Succeeded','FailedRetryable','Failed','DeadLettered','LeaseLost')
            """ + (filtered ? "\nAND job_id IN (SELECT value FROM json_each($jobs));" : ";");

        public IReadOnlyList<PerformanceAggregateRow> GetAggregates(
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            IReadOnlyCollection<string>? jobIds = null,
            DateTimeOffset? coverageCutoffUtc = null)
        {
            if (toUtc < fromUtc)
            {
                throw new ArgumentOutOfRangeException(nameof(toUtc), "The completion window end must not precede its start.");
            }

            if (toUtc == fromUtc || jobIds is { Count: 0 })
            {
                return [];
            }

            using var connection = connectionFactory.OpenConnection();
            using var command = SqliteStorage.Command(connection, null, AggregateSql(jobIds is not null));
            command.Add("$from", SqliteStorage.Utc(fromUtc));
            command.Add("$to", SqliteStorage.Utc(toUtc));
            command.Add("$coverageCutoff", SqliteStorage.Utc(coverageCutoffUtc ?? toUtc));
            command.Add("$maxSeconds", MaxMetricSeconds);
            if (jobIds is not null)
            {
                command.Add("$jobs", JsonSerializer.Serialize(jobIds.Distinct(StringComparer.Ordinal)));
            }

            // One statement holds one SQLite read snapshot. Pool raw samples before sorting, rather
            // than averaging child percentiles or capping each job's attempt population.
            var groups = new Dictionary<(string JobId, int? ChunkId), AggregateSamples>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var jobId = reader.GetString(0);
                    var chunkId = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
                    var succeeded = reader.GetString(2) == "Succeeded";
                    var hasResources = succeeded && (reader.IsDBNull(6) || reader.GetInt32(6) != 1) && !reader.IsDBNull(7);
                    var cpu = hasResources && !reader.IsDBNull(3) ? reader.GetDouble(3) : (double?)null;
                    var duration = hasResources && !reader.IsDBNull(4) ? reader.GetDouble(4) : (double?)null;
                    var memory = hasResources && !reader.IsDBNull(5) ? reader.GetInt64(5) / BytesPerGiB : (double?)null;
                    var coverageEligible = reader.GetInt32(8) == 1;
                    Add((jobId, null), succeeded, cpu, duration, memory, coverageEligible);
                    if (chunkId.HasValue)
                    {
                        Add((jobId, chunkId), succeeded, cpu, duration, memory, coverageEligible);
                    }
                }
            }

            return groups.OrderBy(pair => pair.Key.JobId, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.ChunkId)
                .Select(pair => pair.Value.ToRow(pair.Key.JobId, pair.Key.ChunkId))
                .ToArray();

            void Add((string JobId, int? ChunkId) key, bool succeeded, double? cpu, double? duration, double? memory, bool coverageEligible)
            {
                if (!groups.TryGetValue(key, out var samples))
                {
                    samples = new AggregateSamples();
                    groups.Add(key, samples);
                }

                samples.Add(succeeded, cpu, duration, memory, coverageEligible);
            }
        }

        public PerformanceCollectionReadout GetCollectionStatus()
        {
            using var connection = connectionFactory.OpenConnection();
            using var command = SqliteStorage.Command(connection, null, """
                SELECT history_initialized, last_history_sync_utc, last_collection_utc,
                       COALESCE(last_error, history_error,
                           (SELECT COALESCE(capture_error, validation_error, last_error)
                            FROM performance_attempts
                            WHERE status='Succeeded' AND (capture_error IS NOT NULL OR validation_error IS NOT NULL OR last_error IS NOT NULL)
                            ORDER BY updated_at_utc DESC LIMIT 1)),
                       (SELECT COUNT(*) FROM performance_attempts
                        WHERE collection_status IN ('Pending','Partial') AND status='Succeeded'),
                       (SELECT COUNT(*) FROM performance_attempts
                        WHERE status='Succeeded' AND duplicate_suppressed IS NOT 1 AND server_activity_id IS NOT NULL
                          AND (cpu_seconds IS NOT NULL OR duration_seconds IS NOT NULL OR memory_peak_bytes IS NOT NULL))
                FROM performance_collection_state WHERE singleton=1;
                """);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException("The performance collection schema has not been initialized.");
            }

            return new PerformanceCollectionReadout(
                reader.GetInt32(0) != 0,
                reader.IsDBNull(1) ? null : SqliteStorage.ParseUtc(reader.GetString(1)),
                reader.IsDBNull(2) ? null : SqliteStorage.ParseUtc(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5));
        }

        public void RecordPassSuccess(DateTimeOffset nowUtc)
        {
            using var connection = connectionFactory.OpenConnection();
            using var command = SqliteStorage.Command(connection, null, """
                UPDATE performance_collection_state SET last_collection_utc=$now, last_error=NULL WHERE singleton=1;
                """);
            command.Add("$now", SqliteStorage.Utc(nowUtc));
            command.ExecuteNonQuery();
        }

        private static DateTimeOffset NextLookup(DateTimeOffset nowUtc, int lookupCount)
        {
            var delay = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Clamp(lookupCount - 1, 0, 6))));
            return DateTimeOffset.MaxValue - nowUtc < delay ? DateTimeOffset.MaxValue : nowUtc + delay;
        }

        private static string? NormalizeCluster(string? cluster)
        {
            if (!Uri.TryCreate(cluster, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
                || uri.AbsolutePath.Trim('/').Length != 0)
            {
                return null;
            }

            return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        }

        private static string? CombineErrors(params string?[] errors)
        {
            var distinct = errors.Where(error => !string.IsNullOrWhiteSpace(error)).Distinct(StringComparer.Ordinal).ToArray();
            return distinct.Length == 0 ? null : string.Join(" ", distinct);
        }

        private sealed class AggregateSamples
        {
            private long completed;
            private long succeeded;
            private long eligibleForCoverage;
            private long missingForCoverage;
            private readonly List<double> cpu = [];
            private readonly List<double> duration = [];
            private readonly List<double> memory = [];

            public void Add(bool success, double? cpuSeconds, double? durationSeconds, double? memoryGiB, bool coverageEligible)
            {
                completed++;
                if (success)
                {
                    succeeded++;
                }

                if (cpuSeconds.HasValue) cpu.Add(cpuSeconds.Value);
                if (durationSeconds.HasValue) duration.Add(durationSeconds.Value);
                if (memoryGiB.HasValue) memory.Add(memoryGiB.Value);
                if (coverageEligible)
                {
                    eligibleForCoverage++;
                    if (!cpuSeconds.HasValue || !durationSeconds.HasValue || !memoryGiB.HasValue)
                    {
                        missingForCoverage++;
                    }
                }
            }

            public PerformanceAggregateRow ToRow(string jobId, int? chunkId) =>
                new(jobId, chunkId, !chunkId.HasValue, completed, succeeded, Percentiles(cpu), Percentiles(duration), Percentiles(memory))
                {
                    Coverage = new PerformanceCoverageCounts(eligibleForCoverage, missingForCoverage)
                };

            private static PerformancePercentiles Percentiles(List<double> values)
            {
                if (values.Count == 0)
                {
                    return PerformancePercentiles.Empty;
                }

                values.Sort();
                return new PerformancePercentiles(
                    values.Count,
                    values[(int)((values.Count * 50L + 99) / 100) - 1],
                    values[(int)((values.Count * 90L + 99) / 100) - 1],
                    values[(int)((values.Count * 95L + 99) / 100) - 1]);
            }
        }
    }
}
