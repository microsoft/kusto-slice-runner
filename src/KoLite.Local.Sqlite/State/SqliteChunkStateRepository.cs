using System.Globalization;
using System.Text.Json;
using KoLite.Local.Core.Scheduling;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.State
{
    public sealed record DurableChunkState(
        string JobId,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        int ChunkId,
        int TotalChunks,
        DurableSliceStatus Status,
        int Attempt,
        string? LeaseOwner,
        DateTimeOffset? LeaseExpiresAtUtc,
        string? LastEventId,
        string? LastErrorCode,
        string? LastErrorMessage,
        DateTimeOffset UpdatedAtUtc)
    {
        public SliceExecutionUnit Execution => SliceExecutionUnit.Chunk(
            new SliceRange(JobId, SliceStartUtc, SliceEndUtc),
            ChunkId,
            TotalChunks);

        public string? LeaseToken => Status == DurableSliceStatus.Running ? LastEventId : null;
    }

    public sealed record ChunkStateEventReadout(
        string EventId,
        string JobId,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        int ChunkId,
        int TotalChunks,
        DurableSliceStatus Status,
        string? Reason,
        int Attempt,
        string? Actor,
        DateTimeOffset RecordedAtUtc);

    public sealed record ChunkCompletionProgress(
        string JobId,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        int CompletedChunks,
        int TotalChunks);

    public sealed class SqliteChunkStateRepository
    {
        private readonly IKoLiteSqliteConnectionFactory connectionFactory;

        public SqliteChunkStateRepository(IKoLiteSqliteConnectionFactory connectionFactory)
        {
            this.connectionFactory = connectionFactory;
        }

        public IReadOnlyList<DurableChunkState> EnsureWindow(SliceRange slice, int totalChunks, string actor)
        {
            _ = SliceExecutionUnit.Chunk(slice, 0, totalChunks);
            using var connection = connectionFactory.OpenConnection();
            using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
            var now = DateTimeOffset.UtcNow;
            EnsureParent(connection, transaction, slice, actor, now);

            for (var chunkId = 0; chunkId < totalChunks; chunkId++)
            {
                using var insert = SqliteStorage.Command(connection, transaction, """
                    INSERT INTO current_slice_chunk_state (
                        job_id,slice_start_utc,slice_end_utc,chunk_id,total_chunks,state,attempt,updated_at_utc)
                    VALUES ($job,$start,$end,$chunk,$chunks,'Missing',0,$now)
                    ON CONFLICT(job_id,slice_start_utc,slice_end_utc,chunk_id) DO NOTHING;
                    """);
                BindExecution(insert, SliceExecutionUnit.Chunk(slice, chunkId, totalChunks));
                insert.Add("$now", SqliteStorage.Utc(now));
                insert.ExecuteNonQuery();
            }

            using (var validate = SqliteStorage.Command(connection, transaction, """
                SELECT COUNT(*), MIN(total_chunks), MAX(total_chunks)
                FROM current_slice_chunk_state
                WHERE job_id=$job AND slice_start_utc=$start AND slice_end_utc=$end;
                """))
            {
                BindSlice(validate, slice);
                using var reader = validate.ExecuteReader();
                reader.Read();
                var count = reader.GetInt32(0);
                var min = reader.GetInt32(1);
                var max = reader.GetInt32(2);
                if (count != totalChunks || min != totalChunks || max != totalChunks)
                {
                    throw new InvalidOperationException(
                        $"Chunk state for '{slice.ToKey().Value}' does not match configured chunk count {totalChunks.ToString(CultureInfo.InvariantCulture)}.");
                }
            }

            RefreshParent(connection, transaction, slice, actor, now);
            transaction.Commit();
            return List(slice);
        }

        public IReadOnlyList<DurableChunkState> List(SliceRange slice)
        {
            using var connection = connectionFactory.OpenConnection();
            using var command = SqliteStorage.Command(connection, null, """
                SELECT *
                FROM current_slice_chunk_state
                WHERE job_id=$job AND slice_start_utc=$start AND slice_end_utc=$end
                ORDER BY chunk_id;
                """);
            BindSlice(command, slice);
            using var reader = command.ExecuteReader();
            var results = new List<DurableChunkState>();
            while (reader.Read())
            {
                results.Add(Read(reader));
            }

            return results;
        }

        public IReadOnlyList<ChunkCompletionProgress> ListCompletionProgress(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                throw new ArgumentException("Job id is required.", nameof(jobId));
            }

            using var connection = connectionFactory.OpenConnection();
            using var command = SqliteStorage.Command(connection, null, """
                SELECT job_id,
                       slice_start_utc,
                       slice_end_utc,
                       SUM(CASE WHEN state='Completed' THEN 1 ELSE 0 END) AS completed_chunks,
                       MIN(total_chunks) AS min_total_chunks,
                       MAX(total_chunks) AS max_total_chunks,
                       COUNT(*) AS child_count
                FROM current_slice_chunk_state
                WHERE job_id=$job
                GROUP BY job_id,slice_start_utc,slice_end_utc
                ORDER BY slice_start_utc,slice_end_utc;
                """);
            command.Add("$job", jobId);
            using var reader = command.ExecuteReader();
            var results = new List<ChunkCompletionProgress>();
            while (reader.Read())
            {
                var completed = Convert.ToInt32(reader.GetInt64(3), CultureInfo.InvariantCulture);
                var minTotal = reader.GetInt32(4);
                var maxTotal = reader.GetInt32(5);
                var childCount = Convert.ToInt32(reader.GetInt64(6), CultureInfo.InvariantCulture);
                if (minTotal != maxTotal || childCount != maxTotal || completed < 0 || completed > maxTotal)
                {
                    throw new InvalidOperationException(
                        $"Chunk state cardinality is inconsistent for job '{jobId}', slice " +
                        $"{SqliteStorage.ReadUtc(reader, "slice_start_utc"):O} to {SqliteStorage.ReadUtc(reader, "slice_end_utc"):O}.");
                }

                results.Add(new ChunkCompletionProgress(
                    reader.GetString(0),
                    SqliteStorage.ReadUtc(reader, "slice_start_utc"),
                    SqliteStorage.ReadUtc(reader, "slice_end_utc"),
                    completed,
                    maxTotal));
            }

            return results;
        }

        public DurableChunkState? Get(SliceExecutionUnit execution)
        {
            RequireChunked(execution);
            using var connection = connectionFactory.OpenConnection();
            return Get(connection, null, execution);
        }

        public IReadOnlyList<ChunkStateEventReadout> ListEvents(SliceRange slice, int take = 100)
        {
            using var connection = connectionFactory.OpenConnection();
            using var command = SqliteStorage.Command(connection, null, """
                SELECT event_id,job_id,slice_start_utc,slice_end_utc,chunk_id,total_chunks,state,reason,attempt,actor,recorded_at_utc
                FROM slice_chunk_state_events
                WHERE job_id=$job AND slice_start_utc=$start AND slice_end_utc=$end
                ORDER BY recorded_at_utc DESC, event_id DESC
                LIMIT $take;
                """);
            BindSlice(command, slice);
            command.Add("$take", Math.Max(1, take));
            using var reader = command.ExecuteReader();
            var results = new List<ChunkStateEventReadout>();
            while (reader.Read())
            {
                results.Add(new ChunkStateEventReadout(
                    reader.GetString(0),
                    reader.GetString(1),
                    SqliteStorage.ReadUtc(reader, "slice_start_utc"),
                    SqliteStorage.ReadUtc(reader, "slice_end_utc"),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    Enum.Parse<DurableSliceStatus>(reader.GetString(6)),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetInt32(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    SqliteStorage.ReadUtc(reader, "recorded_at_utc")));
            }

            return results;
        }

        public DurableChunkState MarkQueued(string operationId, SliceExecutionUnit execution, string? reason = null, string? actor = null, string payloadJson = "{}")
        {
            RequireChunked(execution);
            using var connection = connectionFactory.OpenConnection();
            using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
            var current = GetRequired(connection, transaction, execution);
            if (current.Status is DurableSliceStatus.Completed or DurableSliceStatus.Running)
            {
                transaction.Commit();
                return current;
            }

            var now = DateTimeOffset.UtcNow;
            InsertEvent(connection, transaction, operationId, execution, DurableSliceStatus.Queued, reason, current.Attempt, payloadJson, actor, now);
            UpdateCurrent(connection, transaction, execution, DurableSliceStatus.Queued, current.Attempt, null, null, operationId, null, null, now);
            RefreshParent(connection, transaction, execution.Slice, actor, now);
            transaction.Commit();
            return Get(execution)!;
        }

        public DurableChunkState? AcquireLease(
            string operationId,
            SliceExecutionUnit execution,
            string leaseOwner,
            TimeSpan leaseDuration,
            DateTimeOffset nowUtc)
        {
            RequireChunked(execution);
            using var connection = connectionFactory.OpenConnection();
            using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
            var current = GetRequired(connection, transaction, execution);
            if (current.Status is DurableSliceStatus.Completed or DurableSliceStatus.DeadLettered
                || current.LeaseExpiresAtUtc > nowUtc.ToUniversalTime())
            {
                transaction.Commit();
                return null;
            }

            var attempt = current.Attempt + 1;
            var expires = nowUtc.ToUniversalTime() + leaseDuration;
            InsertEvent(connection, transaction, operationId, execution, DurableSliceStatus.Running, null, attempt, "{}", leaseOwner, nowUtc);
            UpdateCurrent(connection, transaction, execution, DurableSliceStatus.Running, attempt, leaseOwner, expires, operationId, null, null, nowUtc);
            RefreshParent(connection, transaction, execution.Slice, leaseOwner, nowUtc);
            transaction.Commit();
            return Get(execution);
        }

        public bool CompleteLease(
            string operationId,
            SliceExecutionUnit execution,
            string leaseOwner,
            string leaseToken,
            DateTimeOffset nowUtc,
            string payloadJson = "{}") =>
            FinishLease(operationId, execution, leaseOwner, leaseToken, nowUtc, DurableSliceStatus.Completed, null, null, payloadJson);

        public bool FailLease(
            string operationId,
            SliceExecutionUnit execution,
            string leaseOwner,
            string leaseToken,
            DateTimeOffset nowUtc,
            string reason,
            string? errorCode,
            string payloadJson = "{}") =>
            FinishLease(operationId, execution, leaseOwner, leaseToken, nowUtc, DurableSliceStatus.Failed, reason, errorCode, payloadJson);

        public bool DeadLetterLease(
            string operationId,
            SliceExecutionUnit execution,
            string leaseOwner,
            string leaseToken,
            DateTimeOffset nowUtc,
            string reason,
            string? errorCode,
            string payloadJson = "{}") =>
            FinishLease(operationId, execution, leaseOwner, leaseToken, nowUtc, DurableSliceStatus.DeadLettered, reason, errorCode, payloadJson);

        public bool RequeueFailed(
            string operationId,
            SliceExecutionUnit execution,
            string actor,
            string reason,
            DateTimeOffset nowUtc)
        {
            RequireChunked(execution);
            using var connection = connectionFactory.OpenConnection();
            using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
            var current = GetRequired(connection, transaction, execution);
            if (current.Status is not (DurableSliceStatus.Failed or DurableSliceStatus.DeadLettered))
            {
                transaction.Commit();
                return false;
            }

            var payload = JsonSerializer.Serialize(new { workKind = "Repair", reason });
            InsertEvent(connection, transaction, operationId, execution, DurableSliceStatus.Queued, reason, current.Attempt, payload, actor, nowUtc);
            UpdateCurrent(connection, transaction, execution, DurableSliceStatus.Queued, current.Attempt, null, null, operationId, null, null, nowUtc);
            RefreshParent(connection, transaction, execution.Slice, actor, nowUtc);
            transaction.Commit();
            return true;
        }

        public string? RequeueFailedAndEnqueue(
            string operationId,
            SliceExecutionUnit execution,
            string actor,
            string reason,
            string queueIdempotencyKey,
            DateTimeOffset availableAtUtc,
            int priority,
            string queueName,
            int maxAttempts,
            string payloadJson,
            string repairBatchId,
            string repairChunkExecutionId)
        {
            RequireChunked(execution);
            using var connection = connectionFactory.OpenConnection();
            using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
            using (var enabled = SqliteStorage.Command(connection, transaction, "SELECT is_enabled FROM job_definitions WHERE job_id=$job;"))
            {
                enabled.Add("$job", execution.Slice.JobId);
                if (Convert.ToInt32(enabled.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                {
                    throw new InvalidOperationException($"Job '{execution.Slice.JobId}' is paused, soft-deleted, or missing. Resume it before repairing failed chunks.");
                }
            }

            var current = GetRequired(connection, transaction, execution);
            if (current.Status is not (DurableSliceStatus.Failed or DurableSliceStatus.DeadLettered))
            {
                transaction.Commit();
                return null;
            }

            using (var active = SqliteStorage.Command(connection, transaction, """
                SELECT COUNT(*)
                FROM work_queue
                WHERE job_id=$job
                  AND slice_start_utc=$start
                  AND slice_end_utc=$end
                  AND (chunk_id=$chunk OR chunk_id IS NULL)
                  AND state IN ('Queued','Leased');
                """))
            {
                BindExecution(active, execution);
                if (Convert.ToInt32(active.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                {
                    transaction.Commit();
                    return null;
                }
            }

            var now = DateTimeOffset.UtcNow;
            var queueItemId = Guid.NewGuid().ToString("N");
            using (var enqueue = SqliteStorage.Command(connection, transaction, """
                INSERT INTO work_queue (
                    queue_item_id,job_id,slice_start_utc,slice_end_utc,queue_name,priority,state,
                    available_at_utc,attempts,max_attempts,idempotency_key,payload_json,chunk_id,total_chunks,
                    created_at_utc,updated_at_utc)
                VALUES (
                    $queueItem,$job,$start,$end,$queue,$priority,'Queued',
                    $available,0,$maxAttempts,$idempotency,$payload,$chunk,$chunks,$now,$now);
                """))
            {
                BindExecution(enqueue, execution);
                enqueue.Add("$queueItem", queueItemId);
                enqueue.Add("$queue", queueName);
                enqueue.Add("$priority", priority);
                enqueue.Add("$available", SqliteStorage.Utc(availableAtUtc));
                enqueue.Add("$maxAttempts", maxAttempts);
                enqueue.Add("$idempotency", queueIdempotencyKey);
                enqueue.Add("$payload", payloadJson);
                enqueue.Add("$now", SqliteStorage.Utc(now));
                enqueue.ExecuteNonQuery();
            }

            using (var history = SqliteStorage.Command(connection, transaction, """
                INSERT INTO repair_chunk_executions (
                    repair_chunk_execution_id,repair_batch_id,job_id,slice_start_utc,slice_end_utc,
                    chunk_id,total_chunks,previous_state,previous_attempt,status,enqueued_queue_item_id,
                    created_at_utc,updated_at_utc)
                VALUES (
                    $repairExecution,$repairBatch,$job,$start,$end,$chunk,$chunks,$previousState,
                    $previousAttempt,'Queued',$queueItem,$now,$now)
                ON CONFLICT(repair_chunk_execution_id) DO UPDATE SET
                    status=excluded.status,
                    enqueued_queue_item_id=excluded.enqueued_queue_item_id,
                    updated_at_utc=excluded.updated_at_utc;
                """))
            {
                BindExecution(history, execution);
                history.Add("$repairExecution", repairChunkExecutionId);
                history.Add("$repairBatch", repairBatchId);
                history.Add("$previousState", current.Status.ToString());
                history.Add("$previousAttempt", current.Attempt);
                history.Add("$queueItem", queueItemId);
                history.Add("$now", SqliteStorage.Utc(now));
                history.ExecuteNonQuery();
            }

            InsertEvent(connection, transaction, operationId, execution, DurableSliceStatus.Queued, reason, current.Attempt, payloadJson, actor, now);
            UpdateCurrent(connection, transaction, execution, DurableSliceStatus.Queued, current.Attempt, null, null, operationId, null, null, now);
            RefreshParent(connection, transaction, execution.Slice, actor, now);
            transaction.Commit();
            return queueItemId;
        }

        public bool RequeueExpiredLease(
            string operationId,
            SliceExecutionUnit execution,
            string actor,
            string reason,
            DateTimeOffset nowUtc)
        {
            RequireChunked(execution);
            using var connection = connectionFactory.OpenConnection();
            using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
            var current = GetRequired(connection, transaction, execution);
            if (current.Status != DurableSliceStatus.Running
                || current.LeaseExpiresAtUtc is { } expiry && expiry > nowUtc.ToUniversalTime())
            {
                transaction.Commit();
                return false;
            }

            InsertEvent(connection, transaction, operationId, execution, DurableSliceStatus.Queued, reason, current.Attempt, "{}", actor, nowUtc);
            UpdateCurrent(connection, transaction, execution, DurableSliceStatus.Queued, current.Attempt, null, null, operationId, null, null, nowUtc);
            RefreshParent(connection, transaction, execution.Slice, actor, nowUtc);
            transaction.Commit();
            return true;
        }

        private bool FinishLease(
            string operationId,
            SliceExecutionUnit execution,
            string leaseOwner,
            string leaseToken,
            DateTimeOffset nowUtc,
            DurableSliceStatus status,
            string? reason,
            string? errorCode,
            string payloadJson)
        {
            if (string.IsNullOrWhiteSpace(leaseToken))
            {
                throw new ArgumentException("A lease token is required for terminal chunk transitions.", nameof(leaseToken));
            }

            RequireChunked(execution);
            using var connection = connectionFactory.OpenConnection();
            using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
            var current = Get(connection, transaction, execution);
            if (current is null
                || current.Status != DurableSliceStatus.Running
                || current.LeaseOwner != leaseOwner
                || current.LastEventId != leaseToken
                || current.LeaseExpiresAtUtc <= nowUtc.ToUniversalTime())
            {
                transaction.Commit();
                return false;
            }

            InsertEvent(connection, transaction, operationId, execution, status, reason, current.Attempt, payloadJson, leaseOwner, nowUtc);
            UpdateCurrent(connection, transaction, execution, status, current.Attempt, null, null, operationId, errorCode, reason, nowUtc);
            RefreshParent(connection, transaction, execution.Slice, leaseOwner, nowUtc);
            transaction.Commit();
            return true;
        }

        private static void EnsureParent(
            SqliteConnection connection,
            SqliteTransaction transaction,
            SliceRange slice,
            string actor,
            DateTimeOffset now)
        {
            using var exists = SqliteStorage.Command(connection, transaction, """
                SELECT COUNT(*)
                FROM current_slice_state
                WHERE job_id=$job AND slice_start_utc=$start AND slice_end_utc=$end;
                """);
            BindSlice(exists, slice);
            if (Convert.ToInt32(exists.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            {
                return;
            }

            var eventId = $"chunk-parent-created|{Guid.NewGuid():N}";
            using (var insertEvent = SqliteStorage.Command(connection, transaction, """
                INSERT INTO slice_state_events (
                    event_id,job_id,slice_start_utc,slice_end_utc,event_type,state,reason,attempt,payload_json,actor,recorded_at_utc)
                VALUES ($event,$job,$start,$end,'Queued','Queued','Chunk window materialized.',0,'{}',$actor,$now);
                """))
            {
                BindSlice(insertEvent, slice);
                insertEvent.Add("$event", eventId);
                insertEvent.Add("$actor", actor);
                insertEvent.Add("$now", SqliteStorage.Utc(now));
                insertEvent.ExecuteNonQuery();
            }

            using var insertParent = SqliteStorage.Command(connection, transaction, """
                INSERT INTO current_slice_state (
                    job_id,slice_start_utc,slice_end_utc,state,attempt,last_event_id,updated_at_utc)
                VALUES ($job,$start,$end,'Queued',0,$event,$now);
                """);
            BindSlice(insertParent, slice);
            insertParent.Add("$event", eventId);
            insertParent.Add("$now", SqliteStorage.Utc(now));
            insertParent.ExecuteNonQuery();
        }

        private static void RefreshParent(
            SqliteConnection connection,
            SqliteTransaction transaction,
            SliceRange slice,
            string? actor,
            DateTimeOffset now)
        {
            var children = ReadAll(connection, transaction, slice);
            if (children.Count == 0)
            {
                return;
            }

            var status = Aggregate(children);
            var attempt = children.Max(child => child.Attempt);
            var latestError = children
                .Where(child => child.LastErrorMessage is not null)
                .OrderByDescending(child => child.UpdatedAtUtc)
                .FirstOrDefault();

            string? currentState;
            int currentAttempt;
            string? currentErrorCode;
            string? currentErrorMessage;
            using (var current = SqliteStorage.Command(connection, transaction, """
                SELECT state,attempt,last_error_code,last_error_message
                FROM current_slice_state
                WHERE job_id=$job AND slice_start_utc=$start AND slice_end_utc=$end;
                """))
            {
                BindSlice(current, slice);
                using var reader = current.ExecuteReader();
                if (!reader.Read())
                {
                    throw new InvalidOperationException($"Parent slice '{slice.ToKey().Value}' is missing.");
                }

                currentState = reader.GetString(0);
                currentAttempt = reader.GetInt32(1);
                currentErrorCode = reader.IsDBNull(2) ? null : reader.GetString(2);
                currentErrorMessage = reader.IsDBNull(3) ? null : reader.GetString(3);
            }

            if (StringComparer.Ordinal.Equals(currentState, status.ToString())
                && currentAttempt == attempt
                && StringComparer.Ordinal.Equals(currentErrorCode, latestError?.LastErrorCode)
                && StringComparer.Ordinal.Equals(currentErrorMessage, latestError?.LastErrorMessage))
            {
                return;
            }

            var counts = children
                .GroupBy(child => child.Status)
                .ToDictionary(group => group.Key.ToString(), group => group.Count(), StringComparer.Ordinal);
            var reason = $"Chunk aggregate: {children.Count(child => child.Status == DurableSliceStatus.Completed).ToString(CultureInfo.InvariantCulture)}/{children.Count.ToString(CultureInfo.InvariantCulture)} completed.";
            var eventId = $"chunk-aggregate|{Guid.NewGuid():N}";
            using (var insertEvent = SqliteStorage.Command(connection, transaction, """
                INSERT INTO slice_state_events (
                    event_id,job_id,slice_start_utc,slice_end_utc,event_type,state,reason,attempt,payload_json,actor,recorded_at_utc)
                VALUES ($event,$job,$start,$end,$state,$state,$reason,$attempt,$payload,$actor,$now);
                """))
            {
                BindSlice(insertEvent, slice);
                insertEvent.Add("$event", eventId);
                insertEvent.Add("$state", status.ToString());
                insertEvent.Add("$reason", reason);
                insertEvent.Add("$attempt", attempt);
                insertEvent.Add("$payload", JsonSerializer.Serialize(new { chunks = children.Count, states = counts }));
                insertEvent.Add("$actor", actor);
                insertEvent.Add("$now", SqliteStorage.Utc(now));
                insertEvent.ExecuteNonQuery();
            }

            using var update = SqliteStorage.Command(connection, transaction, """
                UPDATE current_slice_state
                SET state=$state,
                    attempt=$attempt,
                    lease_owner=NULL,
                    lease_expires_at_utc=NULL,
                    last_event_id=$event,
                    last_error_code=$errorCode,
                    last_error_message=$errorMessage,
                    updated_at_utc=$now
                WHERE job_id=$job AND slice_start_utc=$start AND slice_end_utc=$end;
                """);
            BindSlice(update, slice);
            update.Add("$state", status.ToString());
            update.Add("$attempt", attempt);
            update.Add("$event", eventId);
            update.Add("$errorCode", latestError?.LastErrorCode);
            update.Add("$errorMessage", latestError?.LastErrorMessage);
            update.Add("$now", SqliteStorage.Utc(now));
            update.ExecuteNonQuery();
        }

        private static DurableSliceStatus Aggregate(IReadOnlyList<DurableChunkState> children)
        {
            if (children.All(child => child.Status == DurableSliceStatus.Completed))
            {
                return DurableSliceStatus.Completed;
            }

            if (children.Any(child => child.Status == DurableSliceStatus.Running))
            {
                return DurableSliceStatus.Running;
            }

            if (children.Any(child => child.Status is DurableSliceStatus.Missing or DurableSliceStatus.Queued))
            {
                return DurableSliceStatus.Queued;
            }

            if (children.Any(child => child.Status == DurableSliceStatus.Failed))
            {
                return DurableSliceStatus.Failed;
            }

            return children.Any(child => child.Status == DurableSliceStatus.DeadLettered)
                ? DurableSliceStatus.DeadLettered
                : DurableSliceStatus.Queued;
        }

        private static void InsertEvent(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string operationId,
            SliceExecutionUnit execution,
            DurableSliceStatus status,
            string? reason,
            int attempt,
            string payloadJson,
            string? actor,
            DateTimeOffset now)
        {
            using var command = SqliteStorage.Command(connection, transaction, """
                INSERT INTO slice_chunk_state_events (
                    event_id,job_id,slice_start_utc,slice_end_utc,chunk_id,total_chunks,state,reason,attempt,payload_json,actor,recorded_at_utc)
                VALUES ($event,$job,$start,$end,$chunk,$chunks,$state,$reason,$attempt,$payload,$actor,$now);
                """);
            BindExecution(command, execution);
            command.Add("$event", operationId);
            command.Add("$state", status.ToString());
            command.Add("$reason", reason);
            command.Add("$attempt", attempt);
            command.Add("$payload", payloadJson);
            command.Add("$actor", actor);
            command.Add("$now", SqliteStorage.Utc(now));
            command.ExecuteNonQuery();
        }

        private static void UpdateCurrent(
            SqliteConnection connection,
            SqliteTransaction transaction,
            SliceExecutionUnit execution,
            DurableSliceStatus status,
            int attempt,
            string? owner,
            DateTimeOffset? expires,
            string eventId,
            string? errorCode,
            string? errorMessage,
            DateTimeOffset now)
        {
            using var command = SqliteStorage.Command(connection, transaction, """
                UPDATE current_slice_chunk_state
                SET state=$state,
                    attempt=$attempt,
                    lease_owner=$owner,
                    lease_expires_at_utc=$expires,
                    last_event_id=$event,
                    last_error_code=$errorCode,
                    last_error_message=$errorMessage,
                    updated_at_utc=$now
                WHERE job_id=$job AND slice_start_utc=$start AND slice_end_utc=$end AND chunk_id=$chunk AND total_chunks=$chunks;
                """);
            BindExecution(command, execution);
            command.Add("$state", status.ToString());
            command.Add("$attempt", attempt);
            command.Add("$owner", owner);
            command.Add("$expires", expires is null ? null : SqliteStorage.Utc(expires.Value));
            command.Add("$event", eventId);
            command.Add("$errorCode", errorCode);
            command.Add("$errorMessage", errorMessage);
            command.Add("$now", SqliteStorage.Utc(now));
            if (command.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException($"Chunk state '{execution.ExecutionKey}' changed concurrently.");
            }
        }

        private static IReadOnlyList<DurableChunkState> ReadAll(
            SqliteConnection connection,
            SqliteTransaction transaction,
            SliceRange slice)
        {
            using var command = SqliteStorage.Command(connection, transaction, """
                SELECT *
                FROM current_slice_chunk_state
                WHERE job_id=$job AND slice_start_utc=$start AND slice_end_utc=$end
                ORDER BY chunk_id;
                """);
            BindSlice(command, slice);
            using var reader = command.ExecuteReader();
            var results = new List<DurableChunkState>();
            while (reader.Read())
            {
                results.Add(Read(reader));
            }

            return results;
        }

        private static DurableChunkState GetRequired(
            SqliteConnection connection,
            SqliteTransaction transaction,
            SliceExecutionUnit execution) =>
            Get(connection, transaction, execution)
            ?? throw new InvalidOperationException($"Chunk state '{execution.ExecutionKey}' does not exist.");

        private static DurableChunkState? Get(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            SliceExecutionUnit execution)
        {
            using var command = SqliteStorage.Command(connection, transaction, """
                SELECT *
                FROM current_slice_chunk_state
                WHERE job_id=$job AND slice_start_utc=$start AND slice_end_utc=$end AND chunk_id=$chunk AND total_chunks=$chunks;
                """);
            BindExecution(command, execution);
            using var reader = command.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        }

        private static DurableChunkState Read(SqliteDataReader reader) => new(
            reader.GetString(reader.GetOrdinal("job_id")),
            SqliteStorage.ReadUtc(reader, "slice_start_utc"),
            SqliteStorage.ReadUtc(reader, "slice_end_utc"),
            reader.GetInt32(reader.GetOrdinal("chunk_id")),
            reader.GetInt32(reader.GetOrdinal("total_chunks")),
            Enum.Parse<DurableSliceStatus>(reader.GetString(reader.GetOrdinal("state"))),
            reader.GetInt32(reader.GetOrdinal("attempt")),
            reader.IsDBNull(reader.GetOrdinal("lease_owner")) ? null : reader.GetString(reader.GetOrdinal("lease_owner")),
            SqliteStorage.ReadNullableUtc(reader, "lease_expires_at_utc"),
            reader.IsDBNull(reader.GetOrdinal("last_event_id")) ? null : reader.GetString(reader.GetOrdinal("last_event_id")),
            reader.IsDBNull(reader.GetOrdinal("last_error_code")) ? null : reader.GetString(reader.GetOrdinal("last_error_code")),
            reader.IsDBNull(reader.GetOrdinal("last_error_message")) ? null : reader.GetString(reader.GetOrdinal("last_error_message")),
            SqliteStorage.ReadUtc(reader, "updated_at_utc"));

        private static void RequireChunked(SliceExecutionUnit execution)
        {
            if (!execution.IsChunked)
            {
                throw new ArgumentException("Chunk execution metadata is required.", nameof(execution));
            }
        }

        private static void BindExecution(SqliteCommand command, SliceExecutionUnit execution)
        {
            RequireChunked(execution);
            BindSlice(command, execution.Slice);
            command.Add("$chunk", execution.ChunkId);
            command.Add("$chunks", execution.TotalChunks);
        }

        private static void BindSlice(SqliteCommand command, SliceRange slice)
        {
            command.Add("$job", slice.JobId);
            command.Add("$start", SqliteStorage.Utc(slice.StartUtc));
            command.Add("$end", SqliteStorage.Utc(slice.EndUtc));
        }
    }
}
