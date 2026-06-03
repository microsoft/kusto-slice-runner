using KoLite.Local.Sqlite.Connections;

namespace KoLite.LocalApp.Ui
{
    public sealed record SliceAttemptReadout(
        string AttemptId,
        string JobId,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        int Attempt,
        string Status,
        string? WorkerId,
        DateTimeOffset? StartedAtUtc,
        DateTimeOffset? CompletedAtUtc,
        string? ErrorCode,
        string? ErrorMessage);

    public sealed record OperationalLogReadout(
        string LogId,
        string? JobId,
        DateTimeOffset? SliceStartUtc,
        DateTimeOffset? SliceEndUtc,
        string Level,
        string Message,
        string? Category,
        string? Exception,
        DateTimeOffset RecordedAtUtc);

    public sealed record SliceEventReadout(
        string EventId,
        string JobId,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        string EventType,
        string? State,
        int? Attempt,
        string? Reason,
        string? Actor,
        DateTimeOffset RecordedAtUtc);

    public sealed class OperationalDetailsReadModel
    {
        private readonly IKoLiteSqliteConnectionFactory connectionFactory;

        public OperationalDetailsReadModel(IKoLiteSqliteConnectionFactory connectionFactory)
        {
            this.connectionFactory = connectionFactory;
        }

        public IReadOnlyList<SliceAttemptReadout> GetAttempts(string jobId, DateTimeOffset? sliceStartUtc = null, DateTimeOffset? sliceEndUtc = null, int take = 50)
        {
            using var connection = connectionFactory.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT attempt_id, job_id, slice_start_utc, slice_end_utc, attempt, status, worker_id, started_at_utc, completed_at_utc, error_code, error_message
                FROM slice_attempts
                WHERE job_id=$jobId
                  AND ($sliceStart IS NULL OR slice_start_utc=$sliceStart)
                  AND ($sliceEnd IS NULL OR slice_end_utc=$sliceEnd)
                ORDER BY COALESCE(completed_at_utc, started_at_utc, slice_start_utc) DESC, attempt DESC
                LIMIT $take;
                """;
            command.Add("$jobId", jobId);
            command.Add("$sliceStart", sliceStartUtc is null ? null : SqliteUi.FormatUtc(sliceStartUtc.Value));
            command.Add("$sliceEnd", sliceEndUtc is null ? null : SqliteUi.FormatUtc(sliceEndUtc.Value));
            command.Add("$take", take);

            using var reader = command.ExecuteReader();
            var rows = new List<SliceAttemptReadout>();
            while (reader.Read())
            {
                rows.Add(new SliceAttemptReadout(
                    reader.GetString(0),
                    reader.GetString(1),
                    SqliteUi.ParseUtc(reader.GetString(2)),
                    SqliteUi.ParseUtc(reader.GetString(3)),
                    reader.GetInt32(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : SqliteUi.ParseUtc(reader.GetString(7)),
                    reader.IsDBNull(8) ? null : SqliteUi.ParseUtc(reader.GetString(8)),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10)));
            }

            return rows;
        }

        public IReadOnlyList<OperationalLogReadout> GetLogs(string jobId, DateTimeOffset? sliceStartUtc = null, DateTimeOffset? sliceEndUtc = null, int take = 50)
        {
            using var connection = connectionFactory.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT log_id, job_id, slice_start_utc, slice_end_utc, level, message, category, exception, recorded_at_utc
                FROM operational_logs
                WHERE job_id=$jobId
                  AND ($sliceStart IS NULL OR slice_start_utc=$sliceStart)
                  AND ($sliceEnd IS NULL OR slice_end_utc=$sliceEnd)
                ORDER BY recorded_at_utc DESC, log_id DESC
                LIMIT $take;
                """;
            command.Add("$jobId", jobId);
            command.Add("$sliceStart", sliceStartUtc is null ? null : SqliteUi.FormatUtc(sliceStartUtc.Value));
            command.Add("$sliceEnd", sliceEndUtc is null ? null : SqliteUi.FormatUtc(sliceEndUtc.Value));
            command.Add("$take", take);

            using var reader = command.ExecuteReader();
            var rows = new List<OperationalLogReadout>();
            while (reader.Read())
            {
                rows.Add(new OperationalLogReadout(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : SqliteUi.ParseUtc(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : SqliteUi.ParseUtc(reader.GetString(3)),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    SqliteUi.ParseUtc(reader.GetString(8))));
            }

            return rows;
        }

        public IReadOnlyList<SliceEventReadout> GetEvents(string jobId, DateTimeOffset? sliceStartUtc = null, DateTimeOffset? sliceEndUtc = null, int take = 50)
        {
            using var connection = connectionFactory.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT event_id, job_id, slice_start_utc, slice_end_utc, event_type, state, attempt, reason, actor, recorded_at_utc
                FROM slice_state_events
                WHERE job_id=$jobId
                  AND ($sliceStart IS NULL OR slice_start_utc=$sliceStart)
                  AND ($sliceEnd IS NULL OR slice_end_utc=$sliceEnd)
                ORDER BY recorded_at_utc DESC, event_id DESC
                LIMIT $take;
                """;
            command.Add("$jobId", jobId);
            command.Add("$sliceStart", sliceStartUtc is null ? null : SqliteUi.FormatUtc(sliceStartUtc.Value));
            command.Add("$sliceEnd", sliceEndUtc is null ? null : SqliteUi.FormatUtc(sliceEndUtc.Value));
            command.Add("$take", take);

            using var reader = command.ExecuteReader();
            var rows = new List<SliceEventReadout>();
            while (reader.Read())
            {
                rows.Add(new SliceEventReadout(
                    reader.GetString(0),
                    reader.GetString(1),
                    SqliteUi.ParseUtc(reader.GetString(2)),
                    SqliteUi.ParseUtc(reader.GetString(3)),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    SqliteUi.ParseUtc(reader.GetString(9))));
            }

            return rows;
        }
    }
}
