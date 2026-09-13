using System.Globalization;
using System.Text.Json;
using KoLite.Local.Core.Performance;
using KoLite.Local.Sqlite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Performance
{
    public sealed partial class SqlitePerformanceRepository
    {
        public void BeginHistoryReconciliation(DateTimeOffset nowUtc)
        {
            using var connection = connectionFactory.OpenConnection();
            using var transaction = connection.BeginTransaction();
            BeginHistory(connection, transaction, nowUtc);
            transaction.Commit();
        }

        private static void BeginHistory(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset nowUtc)
        {
            using var command = SqliteStorage.Command(connection, transaction, """
                UPDATE performance_collection_state
                SET reconciliation_active=1, history_from_utc=MAX($from, COALESCE(retained_from_utc, $from)),
                    history_to_utc=$to, history_phase='Current',
                    current_cursor_utc=NULL, current_cursor_id=NULL,
                    archive_cursor_utc=NULL, archive_cursor_id=NULL,
                    archive_active_id=NULL, archive_attempt_offset=0,
                    history_rows_processed=0, history_error=NULL
                WHERE singleton=1 AND reconciliation_active=0;
                """);
            command.Add("$from", SqliteStorage.Utc(nowUtc - HistoryWindow));
            command.Add("$to", SqliteStorage.Utc(nowUtc));
            command.ExecuteNonQuery();
        }

        public bool BackfillBatch(DateTimeOffset nowUtc, int batchSize = 500)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
            batchSize = Math.Min(batchSize, 5000);
            using var connection = connectionFactory.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var checkpoint = ReadHistoryCheckpoint(connection, transaction);
            if (!checkpoint.Active)
            {
                if (checkpoint.Initialized)
                {
                    transaction.Commit();
                    return true;
                }

                BeginHistory(connection, transaction, nowUtc);
                checkpoint = ReadHistoryCheckpoint(connection, transaction);
            }

            var from = new[] { checkpoint.FromUtc, nowUtc - HistoryWindow, checkpoint.RetainedFromUtc ?? DateTimeOffset.MinValue }.Max();
            checkpoint = checkpoint with { FromUtc = from };
            if (checkpoint.Phase == "Current")
            {
                var rows = ReadCurrentHistoryBatch(connection, transaction, checkpoint, batchSize);
                foreach (var row in rows)
                {
                    var fact = ReadHistoryFact(row.Json, row.JobId, row.SliceStartUtc, row.SliceEndUtc, "CurrentHistory");
                    ImportHistoryFact(connection, transaction, fact, from, checkpoint.ToUtc, nowUtc);
                }

                checkpoint = checkpoint with
                {
                    CurrentCursorUtc = rows.Count == 0 ? checkpoint.CurrentCursorUtc : rows[^1].CursorUtc,
                    CurrentCursorId = rows.Count == 0 ? checkpoint.CurrentCursorId : rows[^1].AttemptId,
                    RowsProcessed = checkpoint.RowsProcessed + rows.Count,
                    Phase = rows.Count < batchSize ? "Archives" : "Current"
                };
                SaveHistoryCheckpoint(connection, transaction, checkpoint, nowUtc);
                transaction.Commit();
                return false;
            }

            var budget = batchSize;
            while (budget > 0)
            {
                var archive = ReadArchive(connection, transaction, checkpoint, nowUtc);
                if (archive is null)
                {
                    if (checkpoint.ActiveArchiveId is not null)
                    {
                        checkpoint = checkpoint with { ActiveArchiveId = null, ArchiveOffset = 0 };
                        budget--;
                        continue;
                    }

                    checkpoint = checkpoint with { Active = false, Initialized = true };
                    break;
                }

                checkpoint = checkpoint with { ActiveArchiveId = archive.Id };
                if (!archive.ValidJson || !archive.AttemptsArray)
                {
                    if (!archive.ValidJson || archive.AttemptCount > 0)
                    {
                        RecordHistoryError(connection, transaction, "A retained rerun snapshot has unreadable attempt history; some older attempts are unavailable.");
                    }

                    checkpoint = FinishArchive(checkpoint, archive);
                    budget--;
                    continue;
                }

                var rows = ReadArchiveAttemptBatch(connection, transaction, archive.Id, checkpoint.ArchiveOffset, budget);
                foreach (var row in rows)
                {
                    var fact = ReadHistoryFact(row.Json, archive.JobId, archive.SliceStartUtc, archive.SliceEndUtc, "RerunHistory");
                    ImportHistoryFact(connection, transaction, fact, from, checkpoint.ToUtc, nowUtc);
                }

                checkpoint = checkpoint with
                {
                    ArchiveOffset = rows.Count == 0 ? checkpoint.ArchiveOffset : rows[^1].Offset + 1,
                    RowsProcessed = checkpoint.RowsProcessed + rows.Count
                };
                if (rows.Count < budget)
                {
                    checkpoint = FinishArchive(checkpoint, archive);
                }

                budget -= Math.Max(rows.Count, 1);
            }

            SaveHistoryCheckpoint(connection, transaction, checkpoint, nowUtc);
            transaction.Commit();
            return !checkpoint.Active;
        }

        private static HistoryCheckpoint FinishArchive(HistoryCheckpoint checkpoint, ArchiveSource archive) => checkpoint with
        {
            ArchiveCursorUtc = archive.CursorUtc,
            ArchiveCursorId = archive.Id,
            ActiveArchiveId = null,
            ArchiveOffset = 0
        };

        private static void ImportHistoryFact(
            SqliteConnection connection, SqliteTransaction transaction, AttemptFact? fact,
            DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset nowUtc)
        {
            if (fact is null)
            {
                return;
            }

            if (fact.CompletedAtUtc < fromUtc || fact.CompletedAtUtc > toUtc)
            {
                return;
            }

            var existing = ReadAttempt(connection, transaction, fact.AttemptId);
            if (existing is not null && !existing.Fact.LegacyCorrelation)
            {
                // A captured dispatch knows the actual target and physical request. Archive/catalog
                // inference must never downgrade that evidence or overwrite resource enrichment.
                StoreFact(connection, transaction, fact, nowUtc);
                return;
            }

            var resolved = ResolveLegacyFact(connection, transaction, fact);
            StoreFact(connection, transaction, resolved, nowUtc);
        }

        private const string CurrentHistoryColumns = """
            SELECT attempt_id, job_id, slice_start_utc, slice_end_utc, completed_at_utc,
                   json_object(
                       'attempt_id',attempt_id,'job_id',job_id,'slice_start_utc',slice_start_utc,'slice_end_utc',slice_end_utc,
                       'chunk_id',chunk_id,'total_chunks',total_chunks,'attempt',attempt,'status',status,
                       'started_at_utc',started_at_utc,'completed_at_utc',completed_at_utc,'metrics_json',metrics_json)
            FROM slice_attempts
            """;

        internal static string CurrentHistorySql(bool singleSlice = false) => CurrentHistoryColumns
            + "\nWHERE " + (singleSlice ? "job_id=$job AND slice_start_utc=$sliceStart AND slice_end_utc=$sliceEnd AND " : string.Empty)
            + """
            completed_at_utc >= $from AND completed_at_utc < $to
              AND status IN ('Succeeded','FailedRetryable','Failed','DeadLettered','LeaseLost')
              AND (completed_at_utc,attempt_id) > ($cursor,$id)
            ORDER BY completed_at_utc, attempt_id
            LIMIT $take;
            """;

        internal static void PreserveRerunAttempts(
            SqliteConnection connection, SqliteTransaction transaction, string jobId,
            DateTimeOffset startUtc, DateTimeOffset endUtc, DateTimeOffset nowUtc)
        {
            var checkpoint = ReadHistoryCheckpoint(connection, transaction);
            var from = nowUtc - HistoryWindow;
            if (checkpoint.RetainedFromUtc > from)
            {
                from = checkpoint.RetainedFromUtc.Value;
            }

            string? cursor = null;
            string? id = null;
            while (true)
            {
                using var command = SqliteStorage.Command(connection, transaction, CurrentHistorySql(singleSlice: true));
                command.Add("$job", jobId);
                command.Add("$sliceStart", SqliteStorage.Utc(startUtc));
                command.Add("$sliceEnd", SqliteStorage.Utc(endUtc));
                command.Add("$from", SqliteStorage.Utc(from.AddSeconds(-1)));
                command.Add("$to", SqliteStorage.Utc(nowUtc.AddSeconds(1)));
                command.Add("$cursor", cursor ?? string.Empty);
                command.Add("$id", id ?? string.Empty);
                command.Add("$take", 500);
                var rows = ReadCurrentHistoryRows(command);
                foreach (var row in rows)
                {
                    ImportHistoryFact(connection, transaction,
                        ReadHistoryFact(row.Json, jobId, row.SliceStartUtc, row.SliceEndUtc, "CurrentHistory"),
                        from, nowUtc, nowUtc);
                }

                if (rows.Count < 500)
                {
                    return;
                }

                cursor = rows[^1].CursorUtc;
                id = rows[^1].AttemptId;
            }
        }

        private static IReadOnlyList<CurrentHistoryRow> ReadCurrentHistoryBatch(
            SqliteConnection connection, SqliteTransaction transaction, HistoryCheckpoint checkpoint, int take)
        {
            using var command = SqliteStorage.Command(connection, transaction, CurrentHistorySql());
            // Older versions used both millisecond and seven-digit UTC text. A one-second seek
            // margin keeps the indexed scan inclusive; parsed timestamps enforce the exact window.
            command.Add("$from", SqliteStorage.Utc(checkpoint.FromUtc.AddSeconds(-1)));
            command.Add("$to", SqliteStorage.Utc(checkpoint.ToUtc.AddSeconds(1)));
            command.Add("$cursor", checkpoint.CurrentCursorUtc ?? string.Empty);
            command.Add("$id", checkpoint.CurrentCursorId ?? string.Empty);
            command.Add("$take", take);
            return ReadCurrentHistoryRows(command);
        }

        private static IReadOnlyList<CurrentHistoryRow> ReadCurrentHistoryRows(SqliteCommand command)
        {
            using var reader = command.ExecuteReader();
            var rows = new List<CurrentHistoryRow>();
            while (reader.Read())
            {
                rows.Add(new CurrentHistoryRow(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5)));
            }

            return rows;
        }

        private static ArchiveSource? ReadArchive(SqliteConnection connection, SqliteTransaction transaction, HistoryCheckpoint checkpoint, DateTimeOffset nowUtc)
        {
            var predicate = checkpoint.ActiveArchiveId is not null ? "rerun_slice_id=$active" : """
                COALESCE(reset_at_utc, updated_at_utc, created_at_utc) >= $from
                AND COALESCE(reset_at_utc, updated_at_utc, created_at_utc) < $to
                AND (COALESCE(reset_at_utc, updated_at_utc, created_at_utc),rerun_slice_id) > ($cursor,$id)
                """;
            using var command = SqliteStorage.Command(connection, transaction, $$"""
                SELECT rerun_slice_id, job_id, slice_start_utc, slice_end_utc,
                       COALESCE(reset_at_utc, updated_at_utc, created_at_utc),
                       json_valid(snapshot_json),
                       CASE WHEN json_valid(snapshot_json) THEN json_type(snapshot_json,'$.attempts') END,
                       CASE WHEN json_valid(snapshot_json) THEN json_extract(snapshot_json,'$.counts.attemptRows') END
                FROM rerun_slices
                WHERE {{predicate}}
                ORDER BY COALESCE(reset_at_utc, updated_at_utc, created_at_utc), rerun_slice_id
                LIMIT 1;
                """);
            if (checkpoint.ActiveArchiveId is not null)
            {
                command.Add("$active", checkpoint.ActiveArchiveId);
            }
            else
            {
                command.Add("$from", SqliteStorage.Utc(checkpoint.FromUtc.AddSeconds(-1)));
                command.Add("$to", SqliteStorage.Utc(nowUtc.AddSeconds(1)));
                command.Add("$cursor", checkpoint.ArchiveCursorUtc ?? string.Empty);
                command.Add("$id", checkpoint.ArchiveCursorId ?? string.Empty);
            }

            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new ArchiveSource(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetInt32(5) != 0, !reader.IsDBNull(6) && reader.GetString(6) == "array",
                    reader.IsDBNull(7) || reader.GetValue(7) is not long count ? 0 : count)
                : null;
        }

        private static IReadOnlyList<ArchiveAttemptRow> ReadArchiveAttemptBatch(
            SqliteConnection connection, SqliteTransaction transaction, string archiveId, long offset, int take)
        {
            using var command = SqliteStorage.Command(connection, transaction, """
                SELECT CAST(a.key AS INTEGER), CASE WHEN a.type='object' THEN a.value ELSE '{}' END
                FROM rerun_slices r, json_each(r.snapshot_json,'$.attempts') a
                WHERE r.rerun_slice_id=$id AND CAST(a.key AS INTEGER) >= $offset
                ORDER BY CAST(a.key AS INTEGER)
                LIMIT $take;
                """);
            command.Add("$id", archiveId);
            command.Add("$offset", offset);
            command.Add("$take", take);
            using var reader = command.ExecuteReader();
            var rows = new List<ArchiveAttemptRow>();
            while (reader.Read())
            {
                rows.Add(new ArchiveAttemptRow(reader.GetInt64(0), reader.GetString(1)));
            }

            return rows;
        }

        private static AttemptFact? ReadHistoryFact(string json, string jobId, string sliceStart, string sliceEnd, string source)
        {
            using var document = JsonDocument.Parse(json);
            var row = document.RootElement;
            var id = JsonText(row, "attempt_id");
            var status = JsonText(row, "status");
            var completed = JsonUtc(row, "completed_at_utc");
            if (string.IsNullOrWhiteSpace(id) || !PerformanceOutcomes.IsCompleted(status) || completed is null)
            {
                return null;
            }

            var start = JsonUtc(row, "slice_start_utc") ?? TryUtc(sliceStart);
            var end = JsonUtc(row, "slice_end_utc") ?? TryUtc(sliceEnd);
            if (start is null || end is null)
            {
                return null;
            }

            var suppression = ReadLegacySuppression(row);
            var fact = new AttemptFact(
                id, jobId, start.Value, end.Value, JsonInt(row, "chunk_id"), JsonInt(row, "total_chunks"),
                JsonInt(row, "attempt") ?? 0, status!, JsonUtc(row, "started_at_utc"), completed,
                null, null, null, null, true, suppression.Value, source, suppression.Error);
            if (JsonText(row, "job_id") is { } recordedJob && recordedJob != jobId)
            {
                fact = fact with { CaptureError = CombineErrors(fact.CaptureError, "The archived attempt's job identity conflicts with its containing snapshot.") };
            }

            return fact;
        }

        private static (bool? Value, string? Error) ReadLegacySuppression(JsonElement row)
        {
            if (JsonText(row, "metrics_json") is not { } metrics)
            {
                return (null, null);
            }

            try
            {
                using var document = JsonDocument.Parse(metrics);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("duplicateSuppressed", out var value)
                    && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return (value.GetBoolean(), null);
                }
            }
            catch (JsonException)
            {
                return (null, "Legacy diagnostic JSON is invalid; duplicate-suppression evidence is unavailable.");
            }

            return (null, null);
        }

        private static string? JsonText(JsonElement row, string name) =>
            row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;

        private static int? JsonInt(JsonElement row, string name) =>
            row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

        private static DateTimeOffset? JsonUtc(JsonElement row, string name) => TryUtc(JsonText(row, name));

        private static DateTimeOffset? TryUtc(string? value) =>
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed : null;

        private static HistoryCheckpoint ReadHistoryCheckpoint(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var command = SqliteStorage.Command(connection, transaction, "SELECT * FROM performance_collection_state WHERE singleton=1;");
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException("The performance collection schema has not been initialized.");
            }

            string? Text(string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetString(reader.GetOrdinal(name));
            return new HistoryCheckpoint(
                reader.GetInt32(reader.GetOrdinal("history_initialized")) != 0,
                reader.GetInt32(reader.GetOrdinal("reconciliation_active")) != 0,
                SqliteStorage.ReadNullableUtc(reader, "history_from_utc") ?? DateTimeOffset.MinValue,
                SqliteStorage.ReadNullableUtc(reader, "history_to_utc") ?? DateTimeOffset.MinValue,
                reader.GetString(reader.GetOrdinal("history_phase")),
                Text("current_cursor_utc"), Text("current_cursor_id"), Text("archive_cursor_utc"), Text("archive_cursor_id"),
                Text("archive_active_id"), reader.GetInt64(reader.GetOrdinal("archive_attempt_offset")),
                reader.GetInt64(reader.GetOrdinal("history_rows_processed")),
                SqliteStorage.ReadNullableUtc(reader, "retained_from_utc"));
        }

        private static void SaveHistoryCheckpoint(SqliteConnection connection, SqliteTransaction transaction, HistoryCheckpoint checkpoint, DateTimeOffset nowUtc)
        {
            using var command = SqliteStorage.Command(connection, transaction, """
                UPDATE performance_collection_state
                SET history_initialized=$initialized,reconciliation_active=$active,history_from_utc=$from,
                    history_phase=$phase,current_cursor_utc=$currentUtc,current_cursor_id=$currentId,
                    archive_cursor_utc=$archiveUtc,archive_cursor_id=$archiveId,archive_active_id=$activeId,
                    archive_attempt_offset=$offset,history_rows_processed=$rows,
                    last_history_sync_utc=CASE WHEN $active=0 THEN $now ELSE last_history_sync_utc END
                WHERE singleton=1;
                """);
            command.Add("$initialized", checkpoint.Initialized ? 1 : 0);
            command.Add("$active", checkpoint.Active ? 1 : 0);
            command.Add("$from", SqliteStorage.Utc(checkpoint.FromUtc));
            command.Add("$phase", checkpoint.Phase);
            command.Add("$currentUtc", checkpoint.CurrentCursorUtc);
            command.Add("$currentId", checkpoint.CurrentCursorId);
            command.Add("$archiveUtc", checkpoint.ArchiveCursorUtc);
            command.Add("$archiveId", checkpoint.ArchiveCursorId);
            command.Add("$activeId", checkpoint.ActiveArchiveId);
            command.Add("$offset", checkpoint.ArchiveOffset);
            command.Add("$rows", checkpoint.RowsProcessed);
            command.Add("$now", SqliteStorage.Utc(nowUtc));
            command.ExecuteNonQuery();
        }

        private static void RecordHistoryError(SqliteConnection connection, SqliteTransaction transaction, string message)
        {
            using var command = SqliteStorage.Command(connection, transaction, "UPDATE performance_collection_state SET history_error=$error WHERE singleton=1;");
            command.Add("$error", message);
            command.ExecuteNonQuery();
        }

        private sealed record CurrentHistoryRow(string AttemptId, string JobId, string SliceStartUtc, string SliceEndUtc, string CursorUtc, string Json);
        private sealed record ArchiveSource(string Id, string JobId, string SliceStartUtc, string SliceEndUtc, string CursorUtc, bool ValidJson, bool AttemptsArray, long AttemptCount);
        private sealed record ArchiveAttemptRow(long Offset, string Json);
        private sealed record HistoryCheckpoint(
            bool Initialized, bool Active, DateTimeOffset FromUtc, DateTimeOffset ToUtc, string Phase,
            string? CurrentCursorUtc, string? CurrentCursorId, string? ArchiveCursorUtc, string? ArchiveCursorId,
            string? ActiveArchiveId, long ArchiveOffset, long RowsProcessed, DateTimeOffset? RetainedFromUtc);
    }
}
