// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.Local.Core.Performance;
using KoLite.Local.Sqlite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Performance
{
    public sealed partial class SqlitePerformanceRepository
    {
        internal static string PendingSql(bool oldestFirst)
        {
            var order = oldestFirst ? "ASC" : "DESC";
            // A mostly collected history must not turn an empty pending lookup into a full history scan.
            return $$"""
                WITH target AS (
                    SELECT cluster_uri, database_name
                    FROM performance_attempts INDEXED BY ix_performance_attempts_pending
                    WHERE collection_status IN ('Pending','Partial') AND status='Succeeded'
                      AND next_lookup_at_utc <= $now AND completed_at_utc >= $cutoff AND completed_at_utc <= $now
                      AND started_at_utc >= $cutoff AND started_at_utc <= completed_at_utc
                      AND duplicate_suppressed IS NOT 1 AND capture_error IS NULL
                      AND cluster_uri IS NOT NULL AND database_name IS NOT NULL AND client_request_id IS NOT NULL
                    ORDER BY completed_at_utc {{order}}, attempt_id {{order}}
                    LIMIT 1
                )
                SELECT p.attempt_id, p.job_id, p.cluster_uri, p.database_name, p.client_request_id,
                       p.started_at_utc, p.completed_at_utc, p.legacy_correlation, p.lookup_count
                FROM performance_attempts p JOIN target t ON p.cluster_uri=t.cluster_uri AND p.database_name=t.database_name
                WHERE p.collection_status IN ('Pending','Partial') AND p.status='Succeeded'
                  AND p.next_lookup_at_utc <= $now AND p.completed_at_utc >= $cutoff AND p.completed_at_utc <= $now
                  AND p.started_at_utc >= $cutoff AND p.started_at_utc <= p.completed_at_utc
                  AND p.duplicate_suppressed IS NOT 1 AND p.capture_error IS NULL AND p.client_request_id IS NOT NULL
                ORDER BY p.completed_at_utc {{order}}, p.attempt_id {{order}}
                LIMIT $take;
                """;
        }

        public IReadOnlyList<PerformancePendingAttempt> GetPendingAttempts(DateTimeOffset nowUtc, int take = 200, bool oldestFirst = false)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
            using var connection = connectionFactory.OpenConnection();
            using (var expire = SqliteStorage.Command(connection, null, """
                UPDATE performance_attempts
                SET collection_status='Expired', next_lookup_at_utc=NULL,
                    last_error='The command is outside Kusto''s thirty-day history window.', updated_at_utc=$now
                WHERE attempt_id IN (
                    SELECT attempt_id FROM performance_attempts
                    WHERE collection_status IN ('Pending','Partial') AND status='Succeeded'
                      AND (completed_at_utc < $cutoff OR started_at_utc < $cutoff)
                    LIMIT 500
                );
                """))
            {
                expire.Add("$cutoff", SqliteStorage.Utc(nowUtc - HistoryWindow));
                expire.Add("$now", SqliteStorage.Utc(nowUtc));
                expire.ExecuteNonQuery();
            }

            using var command = SqliteStorage.Command(connection, null, PendingSql(oldestFirst));
            command.Add("$now", SqliteStorage.Utc(nowUtc));
            command.Add("$cutoff", SqliteStorage.Utc(nowUtc - HistoryWindow));
            command.Add("$take", Math.Min(take, 200));
            using var reader = command.ExecuteReader();
            var pending = new List<PerformancePendingAttempt>();
            while (reader.Read())
            {
                pending.Add(new PerformancePendingAttempt(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    SqliteStorage.ParseUtc(reader.GetString(5)), SqliteStorage.ParseUtc(reader.GetString(6)),
                    reader.GetInt32(7) != 0, reader.GetInt32(8)));
            }

            return pending;
        }

        public void ApplyStatistics(
            IReadOnlyList<PerformancePendingAttempt> attempts,
            IReadOnlyList<KustoCommandStatistics> statistics,
            DateTimeOffset nowUtc)
        {
            ArgumentNullException.ThrowIfNull(attempts);
            ArgumentNullException.ThrowIfNull(statistics);
            if (attempts.Count == 0)
            {
                return;
            }

            var targets = attempts.Select(attempt => (NormalizeCluster(attempt.ClusterUri), attempt.Database)).Distinct().ToArray();
            if (targets.Length != 1 || targets[0].Item1 is null)
            {
                throw new ArgumentException("A statistics result must belong to exactly one recorded cluster and database.", nameof(attempts));
            }

            var distinctStatistics = statistics.Distinct().ToArray();
            var conflicts = distinctStatistics.GroupBy(item => item.ServerActivityId)
                .Where(group => group.Key == Guid.Empty || group.Count() > 1)
                .Select(group => group.Key).ToHashSet();
            var byClient = distinctStatistics.GroupBy(item => item.ClientRequestId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

            using var connection = connectionFactory.OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var requested in attempts.DistinctBy(attempt => attempt.AttemptId, StringComparer.Ordinal))
            {
                var stored = ReadAttempt(connection, transaction, requested.AttemptId);
                if (!MatchesPending(stored, requested))
                {
                    continue;
                }

                var current = AfterLookup(stored!, nowUtc);
                byClient.TryGetValue(requested.ClientRequestId, out var candidates);
                candidates ??= [];
                if (requested.LegacyCorrelation)
                {
                    if (candidates.Any(candidate => candidate.CompletedAtUtc < candidate.StartedAtUtc))
                    {
                        WriteAttempt(connection, transaction, Ambiguous(current, "Legacy command timing is invalid; a unique attempt interval cannot be established."));
                        continue;
                    }

                    candidates = candidates.Where(candidate =>
                        candidate.StartedAtUtc >= requested.StartedAtUtc && candidate.CompletedAtUtc <= requested.CompletedAtUtc).ToArray();
                }

                if (candidates.Length == 0)
                {
                    WriteAttempt(connection, transaction, current with { LastError = "Command statistics are not visible yet; lookup will retry." });
                    continue;
                }

                if (candidates.Length != 1 || conflicts.Contains(candidates[0].ServerActivityId))
                {
                    WriteAttempt(connection, transaction, Ambiguous(current, "Multiple or conflicting server records match this attempt; resource correlation is ambiguous."));
                    continue;
                }

                var candidate = candidates[0];
                if (candidate.CompletedAtUtc < candidate.StartedAtUtc || candidate.ServerActivityId == Guid.Empty)
                {
                    WriteAttempt(connection, transaction, Ambiguous(current, "Command statistics have invalid timing or server identity."));
                    continue;
                }

                if (HasCompetingAttempt(connection, transaction, current.Fact, candidate))
                {
                    WriteAttempt(connection, transaction, Ambiguous(current, "The command also matches another local attempt; resource correlation is ambiguous."));
                    continue;
                }

                var serverId = candidate.ServerActivityId.ToString("N");
                if (ServerOwnedByAnotherAttempt(connection, transaction, current.Fact, serverId)
                    || (current.ServerActivityId is not null && current.ServerActivityId != serverId))
                {
                    WriteAttempt(connection, transaction, Ambiguous(current, "The server identity is already associated with a different attempt or command."));
                    continue;
                }

                if (!string.Equals(candidate.State, "Completed", StringComparison.OrdinalIgnoreCase))
                {
                    WriteAttempt(connection, transaction, current with
                    {
                        LastError = "Kusto has not reported this command as Completed; resource lookup will retry.",
                        ValidationError = CombineErrors(current.ValidationError, candidate.ValidationError)
                    });
                    continue;
                }

                var cpu = ValidSeconds(candidate.CpuSeconds);
                var duration = ValidSeconds(candidate.DurationSeconds);
                var memory = candidate.MemoryPeakBytes is >= 0 ? candidate.MemoryPeakBytes : null;
                var validation = CombineErrors(
                    candidate.ValidationError,
                    candidate.CpuSeconds.HasValue && !cpu.HasValue ? "CPU must be a finite, non-negative Kusto timespan." : null,
                    candidate.DurationSeconds.HasValue && !duration.HasValue ? "Duration must be a finite, non-negative Kusto timespan." : null,
                    candidate.MemoryPeakBytes.HasValue && !memory.HasValue ? "Peak memory must be a non-negative byte count." : null);
                if ((current.CpuSeconds.HasValue && cpu.HasValue && current.CpuSeconds != cpu)
                    || (current.DurationSeconds.HasValue && duration.HasValue && current.DurationSeconds != duration)
                    || (current.MemoryPeakBytes.HasValue && memory.HasValue && current.MemoryPeakBytes != memory)
                    || (current.ServerStartedAtUtc.HasValue && current.ServerStartedAtUtc != candidate.StartedAtUtc)
                    || (current.ServerCompletedAtUtc.HasValue && current.ServerCompletedAtUtc != candidate.CompletedAtUtc))
                {
                    WriteAttempt(connection, transaction, Ambiguous(current, "Command statistics conflict with previously collected values for this server identity."));
                    continue;
                }

                cpu ??= current.CpuSeconds;
                duration ??= current.DurationSeconds;
                memory ??= current.MemoryPeakBytes;
                var complete = cpu.HasValue && duration.HasValue && memory.HasValue;
                WriteAttempt(connection, transaction, current with
                {
                    CpuSeconds = cpu,
                    DurationSeconds = duration,
                    MemoryPeakBytes = memory,
                    ServerActivityId = serverId,
                    ServerStartedAtUtc = candidate.StartedAtUtc,
                    ServerCompletedAtUtc = candidate.CompletedAtUtc,
                    CollectionStatus = complete ? "Collected" : "Partial",
                    NextLookupAtUtc = complete ? null : current.NextLookupAtUtc,
                    ValidationError = complete ? validation : CombineErrors(current.ValidationError, validation),
                    LastError = complete ? null : "Some Kusto resource measurements are unavailable; valid metric families are retained."
                });
            }

            transaction.Commit();
        }

        public void RecordCollectionFailure(IReadOnlyList<PerformancePendingAttempt> attempts, DateTimeOffset nowUtc, string message)
        {
            ArgumentNullException.ThrowIfNull(attempts);
            ArgumentException.ThrowIfNullOrWhiteSpace(message);
            var error = message.Length > 2000 ? message[..2000] : message;
            using var connection = connectionFactory.OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var attempt in attempts.DistinctBy(item => item.AttemptId, StringComparer.Ordinal))
            {
                var stored = ReadAttempt(connection, transaction, attempt.AttemptId);
                if (MatchesPending(stored, attempt))
                {
                    WriteAttempt(connection, transaction, AfterLookup(stored!, nowUtc) with { LastError = error });
                }
            }

            using var command = SqliteStorage.Command(connection, transaction, "UPDATE performance_collection_state SET last_error=$error WHERE singleton=1;");
            command.Add("$error", error);
            command.ExecuteNonQuery();
            transaction.Commit();
        }

        private static bool MatchesPending(StoredAttempt? stored, PerformancePendingAttempt attempt) =>
            stored is not null && Eligibility(stored.Fact) == "Pending"
            && stored.CollectionStatus is "Pending" or "Partial"
            && stored.LookupCount == attempt.LookupCount
            && stored.Fact.JobId == attempt.JobId
            && stored.Fact.ClusterUri == NormalizeCluster(attempt.ClusterUri) && stored.Fact.Database == attempt.Database
            && stored.Fact.ClientRequestId == attempt.ClientRequestId && stored.Fact.LegacyCorrelation == attempt.LegacyCorrelation
            && stored.Fact.StartedAtUtc == attempt.StartedAtUtc && stored.Fact.CompletedAtUtc == attempt.CompletedAtUtc;

        private static StoredAttempt AfterLookup(StoredAttempt attempt, DateTimeOffset nowUtc)
        {
            var count = attempt.LookupCount == int.MaxValue ? int.MaxValue : attempt.LookupCount + 1;
            return attempt with
            {
                LookupCount = count,
                LastLookupAtUtc = nowUtc,
                NextLookupAtUtc = NextLookup(nowUtc, count),
                UpdatedAtUtc = nowUtc
            };
        }

        private static StoredAttempt Ambiguous(StoredAttempt attempt, string message) => attempt with
        {
            CpuSeconds = null,
            DurationSeconds = null,
            MemoryPeakBytes = null,
            ServerActivityId = null,
            ServerStartedAtUtc = null,
            ServerCompletedAtUtc = null,
            CollectionStatus = "Ambiguous",
            NextLookupAtUtc = null,
            LastError = message
        };

        private static double? ValidSeconds(double? seconds) =>
            seconds is { } value && double.IsFinite(value) && value >= 0 && value <= MaxMetricSeconds ? value : null;

        private static bool HasCompetingAttempt(SqliteConnection connection, SqliteTransaction transaction, AttemptFact fact, KustoCommandStatistics command)
        {
            using var query = SqliteStorage.Command(connection, transaction, """
                SELECT 1 FROM performance_attempts
                WHERE client_request_id=$client AND attempt_id<>$id
                """ + (fact.LegacyCorrelation ? """

                  AND (cluster_uri=$cluster OR cluster_uri IS NULL)
                  AND (database_name=$database OR database_name IS NULL)
                  AND (started_at_utc IS NULL OR started_at_utc <= $started)
                  AND (completed_at_utc IS NULL OR completed_at_utc >= $completed)
                LIMIT 1;
                """ : " AND cluster_uri=$cluster AND database_name=$database LIMIT 1;"));
            query.Add("$cluster", fact.ClusterUri);
            query.Add("$database", fact.Database);
            query.Add("$client", fact.ClientRequestId);
            query.Add("$id", fact.AttemptId);
            if (fact.LegacyCorrelation)
            {
                // Missing target evidence cannot rule an overlapping attempt out. A failed local
                // attempt may still own the completed server command after a transport failure.
                query.Add("$started", SqliteStorage.Utc(command.StartedAtUtc));
                query.Add("$completed", SqliteStorage.Utc(command.CompletedAtUtc));
            }

            return query.ExecuteScalar() is not null;
        }

        private static bool ServerOwnedByAnotherAttempt(SqliteConnection connection, SqliteTransaction transaction, AttemptFact fact, string serverId)
        {
            using var query = SqliteStorage.Command(connection, transaction, """
                SELECT 1 FROM performance_attempts
                WHERE cluster_uri=$cluster AND database_name=$database AND server_activity_id=$server AND attempt_id<>$id
                LIMIT 1;
                """);
            query.Add("$cluster", fact.ClusterUri);
            query.Add("$database", fact.Database);
            query.Add("$server", serverId);
            query.Add("$id", fact.AttemptId);
            return query.ExecuteScalar() is not null;
        }
    }
}
