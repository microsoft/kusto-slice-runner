using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Queue
{
    public enum DurableWorkQueueState { Queued, Leased, Completed, DeadLettered }
    public sealed record DurableWorkItem(string QueueItemId, string JobId, DateTimeOffset SliceStartUtc, DateTimeOffset SliceEndUtc, string QueueName, int Priority, DurableWorkQueueState State, DateTimeOffset AvailableAtUtc, string? LockedBy, DateTimeOffset? LockedUntilUtc, int Attempts, int MaxAttempts, string IdempotencyKey, string PayloadJson, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

    public sealed class SqliteWorkQueueRepository
    {
        private readonly IKoLiteSqliteConnectionFactory connectionFactory;
        public SqliteWorkQueueRepository(IKoLiteSqliteConnectionFactory connectionFactory) => this.connectionFactory = connectionFactory;
        public DurableWorkItem Enqueue(string jobId, DateTimeOffset sliceStartUtc, DateTimeOffset sliceEndUtc, string idempotencyKey, DateTimeOffset availableAtUtc, int priority = 0, string queueName = "default", int maxAttempts = 3, string payloadJson = "{}")
        {
            using var c = connectionFactory.OpenConnection(); using var tx = c.BeginTransaction(System.Data.IsolationLevel.Serializable); var existing = ByKey(c, tx, idempotencyKey); if (existing is not null) { tx.Commit(); return existing; }
            var id = Guid.NewGuid().ToString("N"); using var cmd = SqliteStorage.Command(c, tx, "INSERT INTO work_queue (queue_item_id,job_id,slice_start_utc,slice_end_utc,queue_name,priority,state,available_at_utc,attempts,max_attempts,idempotency_key,payload_json,created_at_utc,updated_at_utc) VALUES ($id,$j,$s,$e,$q,$p,'Queued',$a,0,$m,$k,$payload,$n,$n);");
            cmd.Add("$id", id); cmd.Add("$j", jobId); cmd.Add("$s", SqliteStorage.Utc(sliceStartUtc)); cmd.Add("$e", SqliteStorage.Utc(sliceEndUtc)); cmd.Add("$q", queueName); cmd.Add("$p", priority); cmd.Add("$a", SqliteStorage.Utc(availableAtUtc)); cmd.Add("$m", maxAttempts); cmd.Add("$k", idempotencyKey); cmd.Add("$payload", payloadJson); cmd.Add("$n", SqliteStorage.Utc(DateTimeOffset.UtcNow)); cmd.ExecuteNonQuery(); tx.Commit(); return Get(id)!;
        }
        public DurableWorkItem? Claim(string queueName, string workerId, TimeSpan visibilityTimeout, DateTimeOffset nowUtc)
        {
            return ClaimCore(queueName, workerId, visibilityTimeout, nowUtc, includeExpiredLeases: true);
        }
        public DurableWorkItem? ClaimQueued(string queueName, string workerId, TimeSpan visibilityTimeout, DateTimeOffset nowUtc)
        {
            return ClaimCore(queueName, workerId, visibilityTimeout, nowUtc, includeExpiredLeases: false);
        }
        private DurableWorkItem? ClaimCore(string queueName, string workerId, TimeSpan visibilityTimeout, DateTimeOffset nowUtc, bool includeExpiredLeases)
        {
            using var c = connectionFactory.OpenConnection(); using var tx = c.BeginTransaction(System.Data.IsolationLevel.Serializable); using var select = SqliteStorage.Command(c, tx, """
                SELECT w.*
                FROM work_queue w
                JOIN job_definitions jd ON jd.job_id = w.job_id AND jd.is_enabled = 1
                WHERE w.queue_name=$q
                  AND (
                      (w.state='Queued')
                      OR ($includeExpiredLeases=1 AND w.state='Leased' AND w.locked_until_utc <= $n)
                  )
                  AND w.available_at_utc <= $n
                ORDER BY w.available_at_utc ASC, w.priority DESC, w.created_at_utc ASC
                LIMIT 1;
                """);
            select.Add("$q", queueName); select.Add("$includeExpiredLeases", includeExpiredLeases ? 1 : 0); select.Add("$n", SqliteStorage.Utc(nowUtc)); DurableWorkItem? item; using (var r = select.ExecuteReader()) item = r.Read() ? Read(r) : null; if (item is null) { tx.Commit(); return null; }
            using var update = SqliteStorage.Command(c, tx, """
                UPDATE work_queue
                SET state='Leased', locked_by=$w, locked_until_utc=$u, attempts=attempts+1, updated_at_utc=$n
                WHERE queue_item_id=$id
                  AND (
                      (state='Queued')
                      OR ($includeExpiredLeases=1 AND state='Leased' AND locked_until_utc <= $n)
                  )
                  AND EXISTS (
                      SELECT 1
                      FROM job_definitions jd
                      WHERE jd.job_id = work_queue.job_id
                        AND jd.is_enabled = 1
                  );
                """);
            update.Add("$w", workerId); update.Add("$includeExpiredLeases", includeExpiredLeases ? 1 : 0); update.Add("$u", SqliteStorage.Utc(nowUtc + visibilityTimeout)); update.Add("$n", SqliteStorage.Utc(nowUtc)); update.Add("$id", item.QueueItemId); if (update.ExecuteNonQuery() != 1) { tx.Commit(); return null; }
            tx.Commit(); return Get(item.QueueItemId);
        }
        public bool Complete(string queueItemId, string workerId) => Terminal(queueItemId, workerId, DurableWorkQueueState.Completed);
        public bool DeadLetter(string queueItemId, string workerId) => Terminal(queueItemId, workerId, DurableWorkQueueState.DeadLettered);
        public int CountActive(string jobId, string queueName)
        {
            using var c = connectionFactory.OpenConnection(); using var cmd = SqliteStorage.Command(c, null, "SELECT COUNT(*) FROM work_queue WHERE job_id=$j AND queue_name=$q AND state IN ('Queued','Leased');");
            cmd.Add("$j", jobId); cmd.Add("$q", queueName); return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        public int CountClaimable(string queueName, DateTimeOffset nowUtc)
        {
            return CountClaimableCore(queueName, nowUtc, includeExpiredLeases: true);
        }
        public int CountQueuedClaimable(string queueName, DateTimeOffset nowUtc)
        {
            return CountClaimableCore(queueName, nowUtc, includeExpiredLeases: false);
        }
        private int CountClaimableCore(string queueName, DateTimeOffset nowUtc, bool includeExpiredLeases)
        {
            using var c = connectionFactory.OpenConnection(); using var cmd = SqliteStorage.Command(c, null, """
                SELECT COUNT(*)
                FROM work_queue w
                JOIN job_definitions jd ON jd.job_id = w.job_id AND jd.is_enabled = 1
                WHERE w.queue_name=$q
                  AND (
                      (w.state='Queued')
                      OR ($includeExpiredLeases=1 AND w.state='Leased' AND w.locked_until_utc <= $n)
                  )
                  AND w.available_at_utc <= $n
                """);
            cmd.Add("$q", queueName); cmd.Add("$includeExpiredLeases", includeExpiredLeases ? 1 : 0); cmd.Add("$n", SqliteStorage.Utc(nowUtc)); return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        public IReadOnlyList<DurableWorkItem> List(string? jobId = null, string? queueName = null)
        {
            using var c = connectionFactory.OpenConnection(); using var cmd = SqliteStorage.Command(c, null, "SELECT * FROM work_queue WHERE ($j IS NULL OR job_id=$j) AND ($q IS NULL OR queue_name=$q) ORDER BY created_at_utc, queue_item_id;");
            cmd.Add("$j", jobId); cmd.Add("$q", queueName); using var r = cmd.ExecuteReader(); var items = new List<DurableWorkItem>(); while (r.Read()) items.Add(Read(r)); return items;
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
        public DurableWorkItem? Get(string queueItemId) { using var c = connectionFactory.OpenConnection(); using var cmd = SqliteStorage.Command(c, null, "SELECT * FROM work_queue WHERE queue_item_id=$id;"); cmd.Add("$id", queueItemId); using var r = cmd.ExecuteReader(); return r.Read() ? Read(r) : null; }
        private bool Terminal(string id, string worker, DurableWorkQueueState state) { using var c = connectionFactory.OpenConnection(); using var cmd = SqliteStorage.Command(c, null, "UPDATE work_queue SET state=$s, locked_by=NULL, locked_until_utc=NULL, updated_at_utc=$u WHERE queue_item_id=$id AND state='Leased' AND locked_by=$w;"); cmd.Add("$s", state.ToString()); cmd.Add("$u", SqliteStorage.Utc(DateTimeOffset.UtcNow)); cmd.Add("$id", id); cmd.Add("$w", worker); return cmd.ExecuteNonQuery() == 1; }
        private static DurableWorkItem? ByKey(SqliteConnection c, SqliteTransaction tx, string key) { using var cmd = SqliteStorage.Command(c, tx, "SELECT * FROM work_queue WHERE idempotency_key=$k;"); cmd.Add("$k", key); using var r = cmd.ExecuteReader(); return r.Read() ? Read(r) : null; }
        private static DurableWorkItem Read(SqliteDataReader r) => new(r.GetString(r.GetOrdinal("queue_item_id")), r.GetString(r.GetOrdinal("job_id")), SqliteStorage.ReadUtc(r, "slice_start_utc"), SqliteStorage.ReadUtc(r, "slice_end_utc"), r.GetString(r.GetOrdinal("queue_name")), r.GetInt32(r.GetOrdinal("priority")), Enum.Parse<DurableWorkQueueState>(r.GetString(r.GetOrdinal("state"))), SqliteStorage.ReadUtc(r, "available_at_utc"), r.IsDBNull(r.GetOrdinal("locked_by")) ? null : r.GetString(r.GetOrdinal("locked_by")), SqliteStorage.ReadNullableUtc(r, "locked_until_utc"), r.GetInt32(r.GetOrdinal("attempts")), r.GetInt32(r.GetOrdinal("max_attempts")), r.GetString(r.GetOrdinal("idempotency_key")), r.GetString(r.GetOrdinal("payload_json")), SqliteStorage.ReadUtc(r, "created_at_utc"), SqliteStorage.ReadUtc(r, "updated_at_utc"));
    }
}
