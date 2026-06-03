using KoLite.Local.Core.Schedules;
using KoLite.Local.Core.Scheduling;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.State
{
    public enum DurableSliceStatus { Missing, Queued, Running, Completed, Failed, DeadLettered, DependencyBlocked }

    public sealed record DurableSliceState(string JobId, DateTimeOffset SliceStartUtc, DateTimeOffset SliceEndUtc, string? GenerationId, DurableSliceStatus Status, int Attempt, string? LeaseOwner, DateTimeOffset? LeaseExpiresAtUtc, string? LastEventId, string? LastErrorCode, string? LastErrorMessage, long Version, DateTimeOffset UpdatedAtUtc)
    {
        public SliceRange Slice => new(JobId, SliceStartUtc, SliceEndUtc);
        public string? LeaseToken => Status == DurableSliceStatus.Running ? LastEventId : null;
    }

    public sealed record SliceStateAppendResult(DurableSliceState State, bool WasDuplicate);

    public sealed class SqliteSliceStateRepository
    {
        private readonly IKoLiteSqliteConnectionFactory connectionFactory;
        public SqliteSliceStateRepository(IKoLiteSqliteConnectionFactory connectionFactory) => this.connectionFactory = connectionFactory;

        public DurableSliceState Get(string jobId, DateTimeOffset sliceStartUtc, DateTimeOffset sliceEndUtc)
        {
            using var c = connectionFactory.OpenConnection();
            return Get(c, null, jobId, sliceStartUtc, sliceEndUtc) ?? Missing(jobId, sliceStartUtc, sliceEndUtc);
        }

        public SliceStateAppendResult Append(string operationId, string jobId, DateTimeOffset sliceStartUtc, DateTimeOffset sliceEndUtc, DurableSliceStatus status, long expectedVersion, string? reason = null, string? actor = null, string? generationId = null, string payloadJson = "{}")
        {
            using var c = connectionFactory.OpenConnection(); using var tx = c.BeginTransaction(System.Data.IsolationLevel.Serializable);
            var current = Get(c, tx, jobId, sliceStartUtc, sliceEndUtc); var version = current?.Version ?? 0;
            if (version != expectedVersion) throw new InvalidOperationException($"Slice state version conflict for '{jobId}'. Expected {expectedVersion}, found {version}.");
            var attempt = status == DurableSliceStatus.Running ? (current?.Attempt ?? 0) + 1 : current?.Attempt ?? 0;
            InsertEvent(c, tx, operationId, jobId, sliceStartUtc, sliceEndUtc, generationId ?? current?.GenerationId, status, reason, attempt, payloadJson, actor);
            Upsert(c, tx, jobId, sliceStartUtc, sliceEndUtc, generationId ?? current?.GenerationId, status, attempt, null, null, operationId, reason, DateTimeOffset.UtcNow);
            tx.Commit(); return new SliceStateAppendResult(Get(jobId, sliceStartUtc, sliceEndUtc), false);
        }

        public DurableSliceState? AcquireLease(string operationId, string jobId, DateTimeOffset sliceStartUtc, DateTimeOffset sliceEndUtc, string leaseOwner, TimeSpan leaseDuration, DateTimeOffset nowUtc)
        {
            using var c = connectionFactory.OpenConnection(); using var tx = c.BeginTransaction(System.Data.IsolationLevel.Serializable);
            var current = Get(c, tx, jobId, sliceStartUtc, sliceEndUtc) ?? Missing(jobId, sliceStartUtc, sliceEndUtc);
            if (current.Status is DurableSliceStatus.Completed or DurableSliceStatus.DeadLettered || current.LeaseExpiresAtUtc > nowUtc.ToUniversalTime()) { tx.Commit(); return null; }
            var attempt = current.Attempt + 1; var expires = nowUtc.ToUniversalTime() + leaseDuration;
            InsertEvent(c, tx, operationId, jobId, sliceStartUtc, sliceEndUtc, current.GenerationId, DurableSliceStatus.Running, null, attempt, "{}", leaseOwner);
            Upsert(c, tx, jobId, sliceStartUtc, sliceEndUtc, current.GenerationId, DurableSliceStatus.Running, attempt, leaseOwner, expires, operationId, null, nowUtc);
            tx.Commit(); return Get(jobId, sliceStartUtc, sliceEndUtc);
        }

        public bool CompleteLease(string operationId, string jobId, DateTimeOffset sliceStartUtc, DateTimeOffset sliceEndUtc, string leaseOwner, string leaseToken, DateTimeOffset nowUtc, string payloadJson = "{}") => FinishLease(operationId, jobId, sliceStartUtc, sliceEndUtc, leaseOwner, leaseToken, nowUtc, DurableSliceStatus.Completed, null, payloadJson);
        public bool FailLease(string operationId, string jobId, DateTimeOffset sliceStartUtc, DateTimeOffset sliceEndUtc, string leaseOwner, string leaseToken, DateTimeOffset nowUtc, string reason, string payloadJson = "{}") => FinishLease(operationId, jobId, sliceStartUtc, sliceEndUtc, leaseOwner, leaseToken, nowUtc, DurableSliceStatus.Failed, reason, payloadJson);
        public bool DeadLetterLease(string operationId, string jobId, DateTimeOffset sliceStartUtc, DateTimeOffset sliceEndUtc, string leaseOwner, string leaseToken, DateTimeOffset nowUtc, string reason, string payloadJson = "{}") => FinishLease(operationId, jobId, sliceStartUtc, sliceEndUtc, leaseOwner, leaseToken, nowUtc, DurableSliceStatus.DeadLettered, reason, payloadJson);

        public DependencyReadiness EvaluateDependencyReadiness(JobDefinition downstream, SliceRange downstreamSlice, IReadOnlyDictionary<string, JobDefinition> jobsByActivityId)
        {
            var completed = new HashSet<SliceKey>(); using var c = connectionFactory.OpenConnection();
            using (var cmd = SqliteStorage.Command(c, null, "SELECT job_id, slice_start_utc, slice_end_utc FROM current_slice_state WHERE state = 'Completed';"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) completed.Add(SliceKey.Create(r.GetString(0), DateTimeOffset.Parse(r.GetString(1)), DateTimeOffset.Parse(r.GetString(2))));
            return DependencyReadinessEvaluator.Evaluate(downstream, downstreamSlice, jobsByActivityId, completed);
        }

        private bool FinishLease(string op, string jobId, DateTimeOffset start, DateTimeOffset end, string owner, string leaseToken, DateTimeOffset now, DurableSliceStatus status, string? reason, string payload)
        {
            if (string.IsNullOrWhiteSpace(leaseToken)) throw new ArgumentException("A lease token is required for terminal slice transitions.", nameof(leaseToken));
            using var c = connectionFactory.OpenConnection(); using var tx = c.BeginTransaction(System.Data.IsolationLevel.Serializable); var cur = Get(c, tx, jobId, start, end);
            if (cur is null || cur.Status != DurableSliceStatus.Running || cur.LeaseOwner != owner || cur.LastEventId != leaseToken || cur.LeaseExpiresAtUtc <= now.ToUniversalTime()) { tx.Commit(); return false; }
            InsertEvent(c, tx, op, jobId, start, end, cur.GenerationId, status, reason, cur.Attempt, payload, owner); Upsert(c, tx, jobId, start, end, cur.GenerationId, status, cur.Attempt, null, null, op, reason, now);
            tx.Commit(); return true;
        }

        private static DurableSliceState Missing(string jobId, DateTimeOffset start, DateTimeOffset end) => new(jobId, start.ToUniversalTime(), end.ToUniversalTime(), null, DurableSliceStatus.Missing, 0, null, null, null, null, null, 0, DateTimeOffset.MinValue);
        private static DurableSliceState? Get(SqliteConnection c, SqliteTransaction? tx, string jobId, DateTimeOffset start, DateTimeOffset end)
        {
            using var cmd = SqliteStorage.Command(c, tx, "SELECT css.*, (SELECT COUNT(*) FROM slice_state_events e WHERE e.job_id=css.job_id AND e.slice_start_utc=css.slice_start_utc AND e.slice_end_utc=css.slice_end_utc) version FROM current_slice_state css WHERE job_id=$j AND slice_start_utc=$s AND slice_end_utc=$e;");
            cmd.Add("$j", jobId); cmd.Add("$s", SqliteStorage.Utc(start)); cmd.Add("$e", SqliteStorage.Utc(end)); using var r = cmd.ExecuteReader(); if (!r.Read()) return null;
            return new DurableSliceState(r.GetString(r.GetOrdinal("job_id")), SqliteStorage.ReadUtc(r, "slice_start_utc"), SqliteStorage.ReadUtc(r, "slice_end_utc"), r.IsDBNull(r.GetOrdinal("generation_id")) ? null : r.GetString(r.GetOrdinal("generation_id")), Enum.Parse<DurableSliceStatus>(r.GetString(r.GetOrdinal("state"))), r.GetInt32(r.GetOrdinal("attempt")), r.IsDBNull(r.GetOrdinal("lease_owner")) ? null : r.GetString(r.GetOrdinal("lease_owner")), SqliteStorage.ReadNullableUtc(r, "lease_expires_at_utc"), r.IsDBNull(r.GetOrdinal("last_event_id")) ? null : r.GetString(r.GetOrdinal("last_event_id")), r.IsDBNull(r.GetOrdinal("last_error_code")) ? null : r.GetString(r.GetOrdinal("last_error_code")), r.IsDBNull(r.GetOrdinal("last_error_message")) ? null : r.GetString(r.GetOrdinal("last_error_message")), r.GetInt64(r.GetOrdinal("version")), SqliteStorage.ReadUtc(r, "updated_at_utc"));
        }
        private static void InsertEvent(SqliteConnection c, SqliteTransaction tx, string id, string jobId, DateTimeOffset start, DateTimeOffset end, string? gen, DurableSliceStatus state, string? reason, int attempt, string payload, string? actor)
        {
            using var cmd = SqliteStorage.Command(c, tx, "INSERT INTO slice_state_events (event_id, job_id, slice_start_utc, slice_end_utc, generation_id, event_type, state, reason, attempt, payload_json, actor) VALUES ($id,$j,$s,$e,$g,$t,$st,$r,$a,$p,$actor);");
            cmd.Add("$id", id); cmd.Add("$j", jobId); cmd.Add("$s", SqliteStorage.Utc(start)); cmd.Add("$e", SqliteStorage.Utc(end)); cmd.Add("$g", gen); cmd.Add("$t", state.ToString()); cmd.Add("$st", state.ToString()); cmd.Add("$r", reason); cmd.Add("$a", attempt); cmd.Add("$p", payload); cmd.Add("$actor", actor); cmd.ExecuteNonQuery();
        }
        private static void Upsert(SqliteConnection c, SqliteTransaction tx, string jobId, DateTimeOffset start, DateTimeOffset end, string? gen, DurableSliceStatus state, int attempt, string? owner, DateTimeOffset? expires, string eventId, string? error, DateTimeOffset now)
        {
            using var cmd = SqliteStorage.Command(c, tx, "INSERT INTO current_slice_state (job_id,slice_start_utc,slice_end_utc,generation_id,state,attempt,lease_owner,lease_expires_at_utc,last_event_id,last_error_code,last_error_message,updated_at_utc) VALUES ($j,$s,$e,$g,$st,$a,$o,$x,$ev,$err,$err,$u) ON CONFLICT(job_id,slice_start_utc,slice_end_utc) DO UPDATE SET generation_id=excluded.generation_id,state=excluded.state,attempt=excluded.attempt,lease_owner=excluded.lease_owner,lease_expires_at_utc=excluded.lease_expires_at_utc,last_event_id=excluded.last_event_id,last_error_code=excluded.last_error_code,last_error_message=excluded.last_error_message,updated_at_utc=excluded.updated_at_utc;");
            cmd.Add("$j", jobId); cmd.Add("$s", SqliteStorage.Utc(start)); cmd.Add("$e", SqliteStorage.Utc(end)); cmd.Add("$g", gen); cmd.Add("$st", state.ToString()); cmd.Add("$a", attempt); cmd.Add("$o", owner); cmd.Add("$x", expires is null ? null : SqliteStorage.Utc(expires.Value)); cmd.Add("$ev", eventId); cmd.Add("$err", error); cmd.Add("$u", SqliteStorage.Utc(now)); cmd.ExecuteNonQuery();
        }
    }
}
