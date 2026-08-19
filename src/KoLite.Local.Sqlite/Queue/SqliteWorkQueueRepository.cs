using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Infrastructure;
using KoLite.Local.Core.Scheduling;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Queue
{
    public enum DurableWorkQueueState { Queued, Leased, Completed, DeadLettered }
    public sealed record DurableWorkItem(string QueueItemId, string JobId, DateTimeOffset SliceStartUtc, DateTimeOffset SliceEndUtc, string QueueName, int Priority, DurableWorkQueueState State, DateTimeOffset AvailableAtUtc, string? LockedBy, DateTimeOffset? LockedUntilUtc, int Attempts, int MaxAttempts, string IdempotencyKey, string PayloadJson, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, int? ChunkId, int? TotalChunks)
    {
        public SliceExecutionUnit Execution => new(new SliceRange(JobId, SliceStartUtc, SliceEndUtc), ChunkId, TotalChunks);
    }

    public sealed class SqliteWorkQueueRepository
    {
        private readonly IKoLiteSqliteConnectionFactory connectionFactory;
        public SqliteWorkQueueRepository(IKoLiteSqliteConnectionFactory connectionFactory) => this.connectionFactory = connectionFactory;
        public DurableWorkItem Enqueue(string jobId, DateTimeOffset sliceStartUtc, DateTimeOffset sliceEndUtc, string idempotencyKey, DateTimeOffset availableAtUtc, int priority = 0, string queueName = "default", int maxAttempts = 3, string payloadJson = "{}", int? chunkId = null, int? totalChunks = null)
        {
            _ = new SliceExecutionUnit(new SliceRange(jobId, sliceStartUtc, sliceEndUtc), chunkId, totalChunks);
            using var c = connectionFactory.OpenConnection(); using var tx = c.BeginTransaction(System.Data.IsolationLevel.Serializable); var existing = ByKey(c, tx, idempotencyKey); if (existing is not null) { tx.Commit(); return existing; }
            var id = Guid.NewGuid().ToString("N"); using var cmd = SqliteStorage.Command(c, tx, "INSERT INTO work_queue (queue_item_id,job_id,slice_start_utc,slice_end_utc,queue_name,priority,state,available_at_utc,attempts,max_attempts,idempotency_key,payload_json,chunk_id,total_chunks,created_at_utc,updated_at_utc) VALUES ($id,$j,$s,$e,$q,$p,'Queued',$a,0,$m,$k,$payload,$chunk,$chunks,$n,$n);");
            cmd.Add("$id", id); cmd.Add("$j", jobId); cmd.Add("$s", SqliteStorage.Utc(sliceStartUtc)); cmd.Add("$e", SqliteStorage.Utc(sliceEndUtc)); cmd.Add("$q", queueName); cmd.Add("$p", priority); cmd.Add("$a", SqliteStorage.Utc(availableAtUtc)); cmd.Add("$m", maxAttempts); cmd.Add("$k", idempotencyKey); cmd.Add("$payload", payloadJson); cmd.Add("$chunk", chunkId); cmd.Add("$chunks", totalChunks); cmd.Add("$n", SqliteStorage.Utc(DateTimeOffset.UtcNow)); cmd.ExecuteNonQuery(); tx.Commit(); return Get(id)!;
        }
        public DurableWorkItem? Claim(string queueName, string workerId, TimeSpan visibilityTimeout, DateTimeOffset nowUtc, bool enforceJobParallelism = false, TimeSpan expiredLeaseGrace = default)
        {
            return ClaimCore(queueName, workerId, visibilityTimeout, nowUtc, includeExpiredLeases: true, enforceJobParallelism, expiredLeaseGrace);
        }
        public DurableWorkItem? ClaimQueued(string queueName, string workerId, TimeSpan visibilityTimeout, DateTimeOffset nowUtc, bool enforceJobParallelism = false)
        {
            return ClaimCore(queueName, workerId, visibilityTimeout, nowUtc, includeExpiredLeases: false, enforceJobParallelism, TimeSpan.Zero);
        }
        // When enforceJobParallelism is set, a claim is only granted while the job has fewer non-expired
        // leases than its MaxParallelism (read from the schedule JSON). This makes MaxParallelism a hard
        // per-job execution bound for every enqueue source (scheduler and repair/rerun) so the worker pool
        // can run with unbounded global concurrency. The guard is evaluated inside the claim's serializable
        // transaction, and the existing single-item UPDATE guard keeps two claims from taking the same row.
        private DurableWorkItem? ClaimCore(string queueName, string workerId, TimeSpan visibilityTimeout, DateTimeOffset nowUtc, bool includeExpiredLeases, bool enforceJobParallelism, TimeSpan expiredLeaseGrace)
        {
            var expiredLeaseCutoff = ExpiredLeaseCutoff(nowUtc, expiredLeaseGrace);
            using var c = connectionFactory.OpenConnection(); using var tx = c.BeginTransaction(System.Data.IsolationLevel.Serializable); using var select = SqliteStorage.Command(c, tx, """
                SELECT w.*
                FROM work_queue w
                JOIN job_definitions jd ON jd.job_id = w.job_id AND jd.is_enabled = 1
                WHERE w.queue_name=$q
                  AND (
                      (w.state='Queued')
                      OR ($includeExpiredLeases=1 AND w.state='Leased' AND w.locked_until_utc <= $gc)
                  )
                  AND w.available_at_utc <= $n
                  AND (
                      $enforceParallelism=0
                      OR (
                          (SELECT COUNT(*) FROM work_queue inflight
                           WHERE inflight.job_id = w.job_id AND inflight.queue_name = w.queue_name
                             AND inflight.state = 'Leased' AND inflight.locked_until_utc > $n)
                          < MAX(1, COALESCE(json_extract(jd.schedule_json, '$.maxParallelism'), 1))
                      )
                  )
                ORDER BY w.available_at_utc ASC, w.priority DESC, w.created_at_utc ASC
                LIMIT 1;
                """);
            select.Add("$q", queueName); select.Add("$includeExpiredLeases", includeExpiredLeases ? 1 : 0); select.Add("$enforceParallelism", enforceJobParallelism ? 1 : 0); select.Add("$n", SqliteStorage.Utc(nowUtc)); select.Add("$gc", SqliteStorage.Utc(expiredLeaseCutoff)); DurableWorkItem? item; using (var r = select.ExecuteReader()) item = r.Read() ? Read(r) : null; if (item is null) { tx.Commit(); return null; }
            using var update = SqliteStorage.Command(c, tx, """
                UPDATE work_queue
                SET state='Leased', locked_by=$w, locked_until_utc=$u, attempts=attempts+1, updated_at_utc=$n
                WHERE queue_item_id=$id
                  AND (
                      (state='Queued')
                      OR ($includeExpiredLeases=1 AND state='Leased' AND locked_until_utc <= $gc)
                  )
                  AND EXISTS (
                      SELECT 1
                      FROM job_definitions jd
                      WHERE jd.job_id = work_queue.job_id
                        AND jd.is_enabled = 1
                  );
                """);
            update.Add("$w", workerId); update.Add("$includeExpiredLeases", includeExpiredLeases ? 1 : 0); update.Add("$u", SqliteStorage.Utc(nowUtc + visibilityTimeout)); update.Add("$n", SqliteStorage.Utc(nowUtc)); update.Add("$gc", SqliteStorage.Utc(expiredLeaseCutoff)); update.Add("$id", item.QueueItemId); if (update.ExecuteNonQuery() != 1) { tx.Commit(); return null; }
            tx.Commit(); return Get(item.QueueItemId);
        }
        public bool Complete(string queueItemId, string workerId) => Terminal(queueItemId, workerId, DurableWorkQueueState.Completed);
        public bool DeadLetter(string queueItemId, string workerId) => Terminal(queueItemId, workerId, DurableWorkQueueState.DeadLettered);
        public int CountActive(string jobId, string queueName)
        {
            using var c = connectionFactory.OpenConnection(); using var cmd = SqliteStorage.Command(c, null, "SELECT COUNT(*) FROM work_queue WHERE job_id=$j AND queue_name=$q AND state IN ('Queued','Leased');");
            cmd.Add("$j", jobId); cmd.Add("$q", queueName); return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        public int CountClaimable(string queueName, DateTimeOffset nowUtc, bool enforceJobParallelism = false, TimeSpan expiredLeaseGrace = default)
        {
            return CountClaimableCore(queueName, nowUtc, includeExpiredLeases: true, enforceJobParallelism, expiredLeaseGrace);
        }
        public int CountQueuedClaimable(string queueName, DateTimeOffset nowUtc, bool enforceJobParallelism = false)
        {
            return CountClaimableCore(queueName, nowUtc, includeExpiredLeases: false, enforceJobParallelism, TimeSpan.Zero);
        }
        private int CountClaimableCore(string queueName, DateTimeOffset nowUtc, bool includeExpiredLeases, bool enforceJobParallelism, TimeSpan expiredLeaseGrace)
        {
            // When enforcing parallelism, the backlog is the number of items a worker could actually claim
            // right now: per job, min(claimable items, remaining MaxParallelism capacity). This keeps the
            // dispatcher from spin-starting workers for jobs already at their MaxParallelism.
            var sql = enforceJobParallelism
                ? """
                    SELECT COALESCE(SUM(MIN(per_job.claimable, MAX(0, per_job.cap - per_job.inflight))), 0)
                    FROM (
                        SELECT COUNT(*) AS claimable,
                               MAX(1, COALESCE(json_extract(jd.schedule_json, '$.maxParallelism'), 1)) AS cap,
                               (SELECT COUNT(*) FROM work_queue inflight
                                WHERE inflight.job_id = w.job_id AND inflight.queue_name = w.queue_name
                                  AND inflight.state = 'Leased' AND inflight.locked_until_utc > $n) AS inflight
                        FROM work_queue w
                        JOIN job_definitions jd ON jd.job_id = w.job_id AND jd.is_enabled = 1
                        WHERE w.queue_name=$q
                          AND (
                              (w.state='Queued')
                              OR ($includeExpiredLeases=1 AND w.state='Leased' AND w.locked_until_utc <= $gc)
                          )
                          AND w.available_at_utc <= $n
                        GROUP BY w.job_id
                    ) per_job;
                    """
                : """
                    SELECT COUNT(*)
                    FROM work_queue w
                    JOIN job_definitions jd ON jd.job_id = w.job_id AND jd.is_enabled = 1
                    WHERE w.queue_name=$q
                      AND (
                          (w.state='Queued')
                          OR ($includeExpiredLeases=1 AND w.state='Leased' AND w.locked_until_utc <= $gc)
                      )
                      AND w.available_at_utc <= $n
                    """;
            using var c = connectionFactory.OpenConnection(); using var cmd = SqliteStorage.Command(c, null, sql);
            cmd.Add("$q", queueName); cmd.Add("$includeExpiredLeases", includeExpiredLeases ? 1 : 0); cmd.Add("$n", SqliteStorage.Utc(nowUtc)); cmd.Add("$gc", SqliteStorage.Utc(ExpiredLeaseCutoff(nowUtc, expiredLeaseGrace))); return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        public IReadOnlyList<DurableWorkItem> List(string? jobId = null, string? queueName = null)
        {
            using var c = connectionFactory.OpenConnection(); using var cmd = SqliteStorage.Command(c, null, "SELECT * FROM work_queue WHERE ($j IS NULL OR job_id=$j) AND ($q IS NULL OR queue_name=$q) ORDER BY created_at_utc, queue_item_id;");
            cmd.Add("$j", jobId); cmd.Add("$q", queueName); using var r = cmd.ExecuteReader(); var items = new List<DurableWorkItem>(); while (r.Read()) items.Add(Read(r)); return items;
        }
        public IReadOnlyList<DurableWorkItem> ListPage(string? jobId, string? queueName, DurableWorkQueueState? state, DateTimeOffset? cursorCreatedAtUtc, string? cursorId, int take)
        {
            if (take < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(take));
            }

            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT *
                FROM work_queue
                WHERE ($j IS NULL OR job_id=$j)
                  AND ($q IS NULL OR queue_name=$q)
                  AND ($state IS NULL OR state=$state)
                  AND (
                      $cursorCreated IS NULL
                      OR created_at_utc < $cursorCreated
                      OR (created_at_utc = $cursorCreated AND queue_item_id < $cursorId)
                  )
                ORDER BY created_at_utc DESC, queue_item_id DESC
                LIMIT $take;
                """);
            cmd.Add("$j", jobId);
            cmd.Add("$q", queueName);
            cmd.Add("$state", state?.ToString());
            cmd.Add("$cursorCreated", cursorCreatedAtUtc is null ? null : SqliteStorage.Utc(cursorCreatedAtUtc.Value));
            cmd.Add("$cursorId", cursorId);
            cmd.Add("$take", take);
            using var r = cmd.ExecuteReader();
            var items = new List<DurableWorkItem>();
            while (r.Read()) items.Add(Read(r));
            return items;
        }
        public bool Abandon(string queueItemId, string workerId, DateTimeOffset availableAtUtc)
        {
            using var c = connectionFactory.OpenConnection(); using var cmd = SqliteStorage.Command(c, null, "UPDATE work_queue SET state='Queued', locked_by=NULL, locked_until_utc=NULL, available_at_utc=$a, updated_at_utc=$u WHERE queue_item_id=$id AND state='Leased' AND locked_by=$w;");
            cmd.Add("$a", SqliteStorage.Utc(availableAtUtc)); cmd.Add("$u", SqliteStorage.Utc(DateTimeOffset.UtcNow)); cmd.Add("$id", queueItemId); cmd.Add("$w", workerId); return cmd.ExecuteNonQuery() == 1;
        }
        public bool ExtendLease(string queueItemId, string workerId, DateTimeOffset lockedUntilUtc, DateTimeOffset nowUtc)
        {
            using var c = connectionFactory.OpenConnection(); using var cmd = SqliteStorage.Command(c, null, "UPDATE work_queue SET locked_until_utc=CASE WHEN locked_until_utc IS NULL OR locked_until_utc < $until THEN $until ELSE locked_until_utc END, updated_at_utc=$now WHERE queue_item_id=$id AND state='Leased' AND locked_by=$w AND locked_until_utc > $now;");
            cmd.Add("$until", SqliteStorage.Utc(lockedUntilUtc)); cmd.Add("$now", SqliteStorage.Utc(nowUtc)); cmd.Add("$id", queueItemId); cmd.Add("$w", workerId); return cmd.ExecuteNonQuery() == 1;
        }
        public bool DeferWithoutAttempt(string queueItemId, string workerId, DateTimeOffset availableAtUtc)
        {
            using var c = connectionFactory.OpenConnection(); using var cmd = SqliteStorage.Command(c, null, "UPDATE work_queue SET state='Queued', locked_by=NULL, locked_until_utc=NULL, available_at_utc=$a, attempts=CASE WHEN attempts > 0 THEN attempts - 1 ELSE 0 END, updated_at_utc=$u WHERE queue_item_id=$id AND state='Leased' AND locked_by=$w;");
            cmd.Add("$a", SqliteStorage.Utc(availableAtUtc)); cmd.Add("$u", SqliteStorage.Utc(DateTimeOffset.UtcNow)); cmd.Add("$id", queueItemId); cmd.Add("$w", workerId); return cmd.ExecuteNonQuery() == 1;
        }
        // Operator-driven recovery: flips an expired-lease (orphaned) queue row for a specific slice back
        // to Queued so a worker re-claims and re-runs it idempotently. Guarded by locked_until_utc <= now
        // so it can never steal a healthy, still-held lease. Returns true when a row was reset.
        public bool RequeueExpiredLease(string jobId, DateTimeOffset sliceStartUtc, DateTimeOffset sliceEndUtc, DateTimeOffset availableAtUtc, DateTimeOffset nowUtc)
        {
            using var c = connectionFactory.OpenConnection(); using var cmd = SqliteStorage.Command(c, null, "UPDATE work_queue SET state='Queued', locked_by=NULL, locked_until_utc=NULL, available_at_utc=$a, updated_at_utc=$u WHERE job_id=$j AND slice_start_utc=$s AND slice_end_utc=$e AND state='Leased' AND locked_until_utc <= $n;");
            cmd.Add("$a", SqliteStorage.Utc(availableAtUtc)); cmd.Add("$u", SqliteStorage.Utc(DateTimeOffset.UtcNow)); cmd.Add("$j", jobId); cmd.Add("$s", SqliteStorage.Utc(sliceStartUtc)); cmd.Add("$e", SqliteStorage.Utc(sliceEndUtc)); cmd.Add("$n", SqliteStorage.Utc(nowUtc)); return cmd.ExecuteNonQuery() >= 1;
        }
        public DurableWorkItem? Get(string queueItemId) { using var c = connectionFactory.OpenConnection(); using var cmd = SqliteStorage.Command(c, null, "SELECT * FROM work_queue WHERE queue_item_id=$id;"); cmd.Add("$id", queueItemId); using var r = cmd.ExecuteReader(); return r.Read() ? Read(r) : null; }
        // A lease is only reclaimable once it has been expired by at least the grace margin, tolerating
        // clock skew and a single missed dispatch cycle. A zero/negative grace falls back to "now".
        private static DateTimeOffset ExpiredLeaseCutoff(DateTimeOffset nowUtc, TimeSpan grace)
        {
            if (grace <= TimeSpan.Zero) return nowUtc;
            var utc = nowUtc.ToUniversalTime();
            return utc - DateTimeOffset.MinValue < grace ? DateTimeOffset.MinValue : utc - grace;
        }
        private bool Terminal(string id, string worker, DurableWorkQueueState state)
        {
            using var c = connectionFactory.OpenConnection();
            using var tx = c.BeginTransaction(System.Data.IsolationLevel.Serializable);
            var now = SqliteStorage.Utc(DateTimeOffset.UtcNow);
            using var cmd = SqliteStorage.Command(c, tx, "UPDATE work_queue SET state=$s, locked_by=NULL, locked_until_utc=NULL, updated_at_utc=$u WHERE queue_item_id=$id AND state='Leased' AND locked_by=$w;");
            cmd.Add("$s", state.ToString());
            cmd.Add("$u", now);
            cmd.Add("$id", id);
            cmd.Add("$w", worker);
            var changed = cmd.ExecuteNonQuery() == 1;
            if (changed)
            {
                using var repair = SqliteStorage.Command(c, tx, """
                    UPDATE repair_chunk_executions
                    SET status=$status, updated_at_utc=$now
                    WHERE enqueued_queue_item_id=$id;
                    """);
                repair.Add("$status", state == DurableWorkQueueState.Completed
                    ? KoLite.Local.Core.Repair.RepairSliceStatus.Completed.ToString()
                    : KoLite.Local.Core.Repair.RepairSliceStatus.Failed.ToString());
                repair.Add("$now", now);
                repair.Add("$id", id);
                repair.ExecuteNonQuery();

                using var repairSlices = SqliteStorage.Command(c, tx, """
                    UPDATE repair_slices
                    SET status=CASE
                            WHEN EXISTS (
                                SELECT 1 FROM repair_chunk_executions child
                                WHERE child.repair_batch_id=repair_slices.repair_batch_id
                                  AND child.job_id=repair_slices.job_id
                                  AND child.slice_start_utc=repair_slices.slice_start_utc
                                  AND child.slice_end_utc=repair_slices.slice_end_utc
                                  AND child.status='Failed'
                            ) THEN 'Failed'
                            WHEN NOT EXISTS (
                                SELECT 1 FROM repair_chunk_executions child
                                WHERE child.repair_batch_id=repair_slices.repair_batch_id
                                  AND child.job_id=repair_slices.job_id
                                  AND child.slice_start_utc=repair_slices.slice_start_utc
                                  AND child.slice_end_utc=repair_slices.slice_end_utc
                                  AND child.status NOT IN ('Completed','Failed')
                            ) THEN 'Completed'
                            ELSE 'Queued'
                        END,
                        updated_at_utc=$now
                    WHERE EXISTS (
                        SELECT 1 FROM repair_chunk_executions trigger
                        WHERE trigger.enqueued_queue_item_id=$id
                          AND trigger.repair_batch_id=repair_slices.repair_batch_id
                          AND trigger.job_id=repair_slices.job_id
                          AND trigger.slice_start_utc=repair_slices.slice_start_utc
                          AND trigger.slice_end_utc=repair_slices.slice_end_utc
                    );
                    """);
                repairSlices.Add("$now", now);
                repairSlices.Add("$id", id);
                repairSlices.ExecuteNonQuery();

                using var repairBatches = SqliteStorage.Command(c, tx, """
                    UPDATE repair_batches
                    SET status=CASE
                            WHEN EXISTS (
                                SELECT 1 FROM repair_slices slice
                                WHERE slice.repair_batch_id=repair_batches.repair_batch_id
                                  AND slice.status='Failed'
                            ) THEN 'Failed'
                            WHEN NOT EXISTS (
                                SELECT 1 FROM repair_slices slice
                                WHERE slice.repair_batch_id=repair_batches.repair_batch_id
                                  AND slice.status IN ('Planned','Queued','Running','Blocked')
                            ) THEN 'Completed'
                            ELSE 'Queued'
                        END,
                        completed_at_utc=CASE
                            WHEN NOT EXISTS (
                                SELECT 1 FROM repair_slices slice
                                WHERE slice.repair_batch_id=repair_batches.repair_batch_id
                                  AND slice.status IN ('Planned','Queued','Running','Blocked')
                            ) THEN $now
                            ELSE completed_at_utc
                        END
                    WHERE repair_batch_id IN (
                        SELECT repair_batch_id
                        FROM repair_chunk_executions
                        WHERE enqueued_queue_item_id=$id
                    );
                    """);
                repairBatches.Add("$now", now);
                repairBatches.Add("$id", id);
                repairBatches.ExecuteNonQuery();
            }

            tx.Commit();
            return changed;
        }
        private static DurableWorkItem? ByKey(SqliteConnection c, SqliteTransaction tx, string key) { using var cmd = SqliteStorage.Command(c, tx, "SELECT * FROM work_queue WHERE idempotency_key=$k;"); cmd.Add("$k", key); using var r = cmd.ExecuteReader(); return r.Read() ? Read(r) : null; }
        private static DurableWorkItem Read(SqliteDataReader r) => new(r.GetString(r.GetOrdinal("queue_item_id")), r.GetString(r.GetOrdinal("job_id")), SqliteStorage.ReadUtc(r, "slice_start_utc"), SqliteStorage.ReadUtc(r, "slice_end_utc"), r.GetString(r.GetOrdinal("queue_name")), r.GetInt32(r.GetOrdinal("priority")), Enum.Parse<DurableWorkQueueState>(r.GetString(r.GetOrdinal("state"))), SqliteStorage.ReadUtc(r, "available_at_utc"), r.IsDBNull(r.GetOrdinal("locked_by")) ? null : r.GetString(r.GetOrdinal("locked_by")), SqliteStorage.ReadNullableUtc(r, "locked_until_utc"), r.GetInt32(r.GetOrdinal("attempts")), r.GetInt32(r.GetOrdinal("max_attempts")), r.GetString(r.GetOrdinal("idempotency_key")), r.GetString(r.GetOrdinal("payload_json")), SqliteStorage.ReadUtc(r, "created_at_utc"), SqliteStorage.ReadUtc(r, "updated_at_utc"), r.IsDBNull(r.GetOrdinal("chunk_id")) ? null : r.GetInt32(r.GetOrdinal("chunk_id")), r.IsDBNull(r.GetOrdinal("total_chunks")) ? null : r.GetInt32(r.GetOrdinal("total_chunks")));
    }
}
