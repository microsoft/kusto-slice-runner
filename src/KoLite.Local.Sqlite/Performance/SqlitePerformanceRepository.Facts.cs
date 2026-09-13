using KoLite.Local.Core.Performance;
using KoLite.Local.Sqlite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Performance
{
    public sealed partial class SqlitePerformanceRepository
    {
        internal static void RecordLocalAttempt(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string attemptId,
            PerformanceAttemptCapture? capture,
            DateTimeOffset nowUtc)
        {
            AttemptFact fact;
            using (var command = SqliteStorage.Command(connection, transaction, """
                SELECT attempt_id, job_id, slice_start_utc, slice_end_utc, chunk_id, total_chunks, attempt, status,
                       started_at_utc, completed_at_utc
                FROM slice_attempts WHERE attempt_id=$id;
                """))
            {
                command.Add("$id", attemptId);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    throw new InvalidOperationException("An operational attempt must be recorded before its performance fact.");
                }

                fact = new AttemptFact(
                    reader.GetString(0), reader.GetString(1), SqliteStorage.ReadUtc(reader, "slice_start_utc"),
                    SqliteStorage.ReadUtc(reader, "slice_end_utc"),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    reader.GetInt32(6), reader.GetString(7),
                    SqliteStorage.ReadNullableUtc(reader, "started_at_utc"), SqliteStorage.ReadNullableUtc(reader, "completed_at_utc"),
                    capture?.ClusterUri, capture?.Database, capture?.CatalogVersion, capture?.ClientRequestId,
                    capture is null, capture?.DuplicateSuppressed, capture is null ? "Local" : "Dispatch");
            }

            StoreFact(connection, transaction, fact, nowUtc);
        }

        private static void StoreFact(SqliteConnection connection, SqliteTransaction transaction, AttemptFact incoming, DateTimeOffset nowUtc)
        {
            var normalizedCluster = NormalizeCluster(incoming.ClusterUri);
            incoming = incoming with
            {
                ClusterUri = normalizedCluster,
                Database = string.IsNullOrWhiteSpace(incoming.Database) ? null : incoming.Database,
                CaptureError = CombineErrors(incoming.CaptureError,
                    incoming.ClusterUri is not null && normalizedCluster is null ? "The captured target cluster is invalid." : null)
            };
            var existing = ReadAttempt(connection, transaction, incoming.AttemptId);
            var fact = existing is null ? incoming : MergeFact(existing.Fact, incoming);
            var updatedAtUtc = existing is not null && existing.UpdatedAtUtc > nowUtc ? existing.UpdatedAtUtc : nowUtc;
            var eligibility = Eligibility(fact);
            var sameContext = existing is not null
                && existing.Fact.ClusterUri == fact.ClusterUri && existing.Fact.Database == fact.Database
                && existing.Fact.ClientRequestId == fact.ClientRequestId && existing.Fact.LegacyCorrelation == fact.LegacyCorrelation
                && existing.Fact.StartedAtUtc == fact.StartedAtUtc && existing.Fact.CompletedAtUtc == fact.CompletedAtUtc
                && existing.Fact.CaptureError == fact.CaptureError;
            var preserveResources = sameContext && eligibility == "Pending"
                && existing!.CollectionStatus is "Pending" or "Partial" or "Collected" or "Ambiguous" or "Expired";
            var stored = preserveResources
                ? existing! with { Fact = fact, UpdatedAtUtc = updatedAtUtc }
                : new StoredAttempt(fact, eligibility, existing?.RecordedAtUtc ?? nowUtc, updatedAtUtc)
                {
                    NextLookupAtUtc = eligibility == "Pending" ? nowUtc : null
                };
            WriteAttempt(connection, transaction, stored);
        }

        private static AttemptFact MergeFact(AttemptFact existing, AttemptFact incoming)
        {
            if (existing.JobId != incoming.JobId || existing.SliceStartUtc != incoming.SliceStartUtc || existing.SliceEndUtc != incoming.SliceEndUtc
                || (existing.ChunkId.HasValue && incoming.ChunkId.HasValue && existing.ChunkId != incoming.ChunkId)
                || (existing.TotalChunks.HasValue && incoming.TotalChunks.HasValue && existing.TotalChunks != incoming.TotalChunks))
            {
                return existing with { CaptureError = "Conflicting local identities share this attempt ID; resource correlation is unavailable." };
            }

            var incomingWins = SourceRank(incoming.Source) >= SourceRank(existing.Source);
            var replaceOutcome = incoming.CompletedAtUtc.HasValue && PerformanceOutcomes.IsCompleted(incoming.Status)
                && (incomingWins || existing.CompletedAtUtc is null || !PerformanceOutcomes.IsCompleted(existing.Status));
            var replaceContext = existing.LegacyCorrelation
                && (!incoming.LegacyCorrelation || incomingWins || existing.ClientRequestId is null);
            var context = replaceContext ? incoming : existing;
            return existing with
            {
                Status = replaceOutcome ? incoming.Status : existing.Status,
                StartedAtUtc = existing.StartedAtUtc ?? incoming.StartedAtUtc,
                CompletedAtUtc = replaceOutcome ? incoming.CompletedAtUtc : existing.CompletedAtUtc,
                Attempt = incomingWins && incoming.Attempt > 0 ? incoming.Attempt : existing.Attempt,
                ChunkId = existing.ChunkId ?? incoming.ChunkId,
                TotalChunks = existing.TotalChunks ?? incoming.TotalChunks,
                ClusterUri = context.ClusterUri,
                Database = context.Database,
                CatalogVersion = context.CatalogVersion,
                ClientRequestId = context.ClientRequestId,
                LegacyCorrelation = context.LegacyCorrelation,
                DuplicateSuppressed = existing.DuplicateSuppressed == true || incoming.DuplicateSuppressed == true
                    ? true : existing.DuplicateSuppressed ?? incoming.DuplicateSuppressed,
                Source = SourceRank(existing.Source) > SourceRank(context.Source) ? existing.Source : context.Source,
                CaptureError = context.CaptureError
            };
        }

        private static int SourceRank(string source) => source switch
        {
            "Dispatch" => 3,
            "CurrentHistory" or "Local" => 2,
            _ => 1
        };

        private static string Eligibility(AttemptFact fact)
        {
            if (fact.CompletedAtUtc is null || !PerformanceOutcomes.IsCompleted(fact.Status)) return "Incomplete";
            if (fact.Status != "Succeeded") return "NotApplicable";
            if (fact.DuplicateSuppressed == true) return "Suppressed";
            if (fact.StartedAtUtc is null || fact.StartedAtUtc > fact.CompletedAtUtc
                || fact.ClusterUri is null || string.IsNullOrWhiteSpace(fact.Database)
                || string.IsNullOrWhiteSpace(fact.ClientRequestId) || fact.CaptureError is not null)
            {
                return "Unavailable";
            }

            return "Pending";
        }

        private static StoredAttempt? ReadAttempt(SqliteConnection connection, SqliteTransaction transaction, string attemptId)
        {
            using var command = SqliteStorage.Command(connection, transaction, "SELECT * FROM performance_attempts WHERE attempt_id=$id;");
            command.Add("$id", attemptId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadStoredAttempt(reader) : null;
        }

        private static StoredAttempt ReadStoredAttempt(SqliteDataReader reader)
        {
            string? Text(string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetString(reader.GetOrdinal(name));
            int? Integer(string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetInt32(reader.GetOrdinal(name));
            double? Number(string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetDouble(reader.GetOrdinal(name));
            var fact = new AttemptFact(
                reader.GetString(reader.GetOrdinal("attempt_id")), reader.GetString(reader.GetOrdinal("job_id")),
                SqliteStorage.ReadUtc(reader, "slice_start_utc"), SqliteStorage.ReadUtc(reader, "slice_end_utc"),
                Integer("chunk_id"), Integer("total_chunks"), reader.GetInt32(reader.GetOrdinal("attempt")),
                reader.GetString(reader.GetOrdinal("status")),
                SqliteStorage.ReadNullableUtc(reader, "started_at_utc"), SqliteStorage.ReadNullableUtc(reader, "completed_at_utc"),
                Text("cluster_uri"), Text("database_name"),
                reader.IsDBNull(reader.GetOrdinal("catalog_version")) ? null : reader.GetInt64(reader.GetOrdinal("catalog_version")),
                Text("client_request_id"), reader.GetInt32(reader.GetOrdinal("legacy_correlation")) != 0,
                Integer("duplicate_suppressed") is { } suppressed ? suppressed != 0 : null,
                reader.GetString(reader.GetOrdinal("capture_source")), Text("capture_error"));
            return new StoredAttempt(
                fact, reader.GetString(reader.GetOrdinal("collection_status")),
                SqliteStorage.ReadUtc(reader, "recorded_at_utc"), SqliteStorage.ReadUtc(reader, "updated_at_utc"))
            {
                CpuSeconds = Number("cpu_seconds"),
                DurationSeconds = Number("duration_seconds"),
                MemoryPeakBytes = reader.IsDBNull(reader.GetOrdinal("memory_peak_bytes")) ? null : reader.GetInt64(reader.GetOrdinal("memory_peak_bytes")),
                ServerActivityId = Text("server_activity_id"),
                ServerStartedAtUtc = SqliteStorage.ReadNullableUtc(reader, "server_started_at_utc"),
                ServerCompletedAtUtc = SqliteStorage.ReadNullableUtc(reader, "server_completed_at_utc"),
                LookupCount = reader.GetInt32(reader.GetOrdinal("lookup_count")),
                NextLookupAtUtc = SqliteStorage.ReadNullableUtc(reader, "next_lookup_at_utc"),
                LastLookupAtUtc = SqliteStorage.ReadNullableUtc(reader, "last_lookup_at_utc"),
                ValidationError = Text("validation_error"),
                LastError = Text("last_error")
            };
        }

        private static void WriteAttempt(SqliteConnection connection, SqliteTransaction transaction, StoredAttempt stored)
        {
            using var command = SqliteStorage.Command(connection, transaction, """
                INSERT INTO performance_attempts (
                    attempt_id,job_id,slice_start_utc,slice_end_utc,chunk_id,total_chunks,attempt,status,
                    started_at_utc,completed_at_utc,cluster_uri,database_name,catalog_version,client_request_id,
                    legacy_correlation,duplicate_suppressed,capture_source,capture_error,cpu_seconds,duration_seconds,
                    memory_peak_bytes,server_activity_id,server_started_at_utc,server_completed_at_utc,
                    collection_status,lookup_count,next_lookup_at_utc,last_lookup_at_utc,validation_error,last_error,
                    recorded_at_utc,updated_at_utc)
                VALUES (
                    $id,$job,$start,$end,$chunk,$chunks,$attempt,$status,$started,$completed,$cluster,$database,$version,
                    $client,$legacy,$suppressed,$source,$captureError,$cpu,$duration,$memory,$server,$serverStarted,
                    $serverCompleted,$collection,$lookups,$next,$lastLookup,$validation,$error,$recorded,$updated)
                ON CONFLICT(attempt_id) DO UPDATE SET
                    status=excluded.status,attempt=excluded.attempt,started_at_utc=excluded.started_at_utc,completed_at_utc=excluded.completed_at_utc,
                    chunk_id=excluded.chunk_id,total_chunks=excluded.total_chunks,
                    cluster_uri=excluded.cluster_uri,database_name=excluded.database_name,catalog_version=excluded.catalog_version,
                    client_request_id=excluded.client_request_id,legacy_correlation=excluded.legacy_correlation,
                    duplicate_suppressed=excluded.duplicate_suppressed,capture_source=excluded.capture_source,capture_error=excluded.capture_error,
                    cpu_seconds=excluded.cpu_seconds,duration_seconds=excluded.duration_seconds,memory_peak_bytes=excluded.memory_peak_bytes,
                    server_activity_id=excluded.server_activity_id,server_started_at_utc=excluded.server_started_at_utc,
                    server_completed_at_utc=excluded.server_completed_at_utc,collection_status=excluded.collection_status,
                    lookup_count=excluded.lookup_count,next_lookup_at_utc=excluded.next_lookup_at_utc,
                    last_lookup_at_utc=excluded.last_lookup_at_utc,validation_error=excluded.validation_error,
                    last_error=excluded.last_error,updated_at_utc=excluded.updated_at_utc;
                """);
            var fact = stored.Fact;
            command.Add("$id", fact.AttemptId);
            command.Add("$job", fact.JobId);
            command.Add("$start", SqliteStorage.Utc(fact.SliceStartUtc));
            command.Add("$end", SqliteStorage.Utc(fact.SliceEndUtc));
            command.Add("$chunk", fact.ChunkId);
            command.Add("$chunks", fact.TotalChunks);
            command.Add("$attempt", fact.Attempt);
            command.Add("$status", fact.Status);
            command.Add("$started", UtcOrNull(fact.StartedAtUtc));
            command.Add("$completed", UtcOrNull(fact.CompletedAtUtc));
            command.Add("$cluster", fact.ClusterUri);
            command.Add("$database", fact.Database);
            command.Add("$version", fact.CatalogVersion);
            command.Add("$client", fact.ClientRequestId);
            command.Add("$legacy", fact.LegacyCorrelation ? 1 : 0);
            command.Add("$suppressed", fact.DuplicateSuppressed is { } suppressed ? suppressed ? 1 : 0 : null);
            command.Add("$source", fact.Source);
            command.Add("$captureError", fact.CaptureError);
            command.Add("$cpu", stored.CpuSeconds);
            command.Add("$duration", stored.DurationSeconds);
            command.Add("$memory", stored.MemoryPeakBytes);
            command.Add("$server", stored.ServerActivityId);
            command.Add("$serverStarted", UtcOrNull(stored.ServerStartedAtUtc));
            command.Add("$serverCompleted", UtcOrNull(stored.ServerCompletedAtUtc));
            command.Add("$collection", stored.CollectionStatus);
            command.Add("$lookups", stored.LookupCount);
            command.Add("$next", UtcOrNull(stored.NextLookupAtUtc));
            command.Add("$lastLookup", UtcOrNull(stored.LastLookupAtUtc));
            command.Add("$validation", stored.ValidationError);
            command.Add("$error", stored.LastError);
            command.Add("$recorded", SqliteStorage.Utc(stored.RecordedAtUtc));
            command.Add("$updated", SqliteStorage.Utc(stored.UpdatedAtUtc));
            command.ExecuteNonQuery();
        }

        private static string? UtcOrNull(DateTimeOffset? value) => value.HasValue ? SqliteStorage.Utc(value.Value) : null;

        private sealed record AttemptFact(
            string AttemptId, string JobId, DateTimeOffset SliceStartUtc, DateTimeOffset SliceEndUtc,
            int? ChunkId, int? TotalChunks, int Attempt, string Status,
            DateTimeOffset? StartedAtUtc, DateTimeOffset? CompletedAtUtc,
            string? ClusterUri, string? Database, long? CatalogVersion, string? ClientRequestId,
            bool LegacyCorrelation, bool? DuplicateSuppressed, string Source, string? CaptureError = null);

        private sealed record StoredAttempt(AttemptFact Fact, string CollectionStatus, DateTimeOffset RecordedAtUtc, DateTimeOffset UpdatedAtUtc)
        {
            public double? CpuSeconds { get; init; }
            public double? DurationSeconds { get; init; }
            public long? MemoryPeakBytes { get; init; }
            public string? ServerActivityId { get; init; }
            public DateTimeOffset? ServerStartedAtUtc { get; init; }
            public DateTimeOffset? ServerCompletedAtUtc { get; init; }
            public int LookupCount { get; init; }
            public DateTimeOffset? NextLookupAtUtc { get; init; }
            public DateTimeOffset? LastLookupAtUtc { get; init; }
            public string? ValidationError { get; init; }
            public string? LastError { get; init; }
        }
    }
}
