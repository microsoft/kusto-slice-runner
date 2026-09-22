// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Core.Schedules;
using Ksr.Local.Core.Scheduling;
using Ksr.Local.Sqlite.Connections;
using Ksr.Local.Sqlite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Ksr.Local.Sqlite.State
{
    public enum DurableSliceStatus { Missing, Queued, Running, Completed, Failed, DeadLettered, DependencyBlocked }

    public sealed record DurableSliceState(string JobId, DateTimeOffset SliceStartUtc, DateTimeOffset SliceEndUtc, string? GenerationId, DurableSliceStatus Status, int Attempt, string? LeaseOwner, DateTimeOffset? LeaseExpiresAtUtc, string? LastEventId, string? LastErrorCode, string? LastErrorMessage, long Version, DateTimeOffset UpdatedAtUtc)
    {
        public SliceRange Slice => new(JobId, SliceStartUtc, SliceEndUtc);
        public string? LeaseToken => Status == DurableSliceStatus.Running ? LastEventId : null;
    }

    public sealed record SliceStateAppendResult(DurableSliceState State, bool WasDuplicate);

    public readonly record struct SliceSchedulingState(DurableSliceStatus Status, long Version);

    public sealed class SqliteSliceStateRepository
    {
        private readonly IKsrSqliteConnectionFactory connectionFactory;
        public SqliteSliceStateRepository(IKsrSqliteConnectionFactory connectionFactory) => this.connectionFactory = connectionFactory;

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
            var now = DateTimeOffset.UtcNow;
            InsertEvent(c, tx, operationId, jobId, sliceStartUtc, sliceEndUtc, generationId ?? current?.GenerationId, status, reason, attempt, payloadJson, actor, now);
            Upsert(c, tx, jobId, sliceStartUtc, sliceEndUtc, generationId ?? current?.GenerationId, status, attempt, null, null, operationId, reason, now);
            tx.Commit(); return new SliceStateAppendResult(Get(jobId, sliceStartUtc, sliceEndUtc), false);
        }

        public DurableSliceState? AcquireLease(string operationId, string jobId, DateTimeOffset sliceStartUtc, DateTimeOffset sliceEndUtc, string leaseOwner, TimeSpan leaseDuration, DateTimeOffset nowUtc)
        {
            using var c = connectionFactory.OpenConnection(); using var tx = c.BeginTransaction(System.Data.IsolationLevel.Serializable);
            var current = Get(c, tx, jobId, sliceStartUtc, sliceEndUtc) ?? Missing(jobId, sliceStartUtc, sliceEndUtc);
            if (current.Status is DurableSliceStatus.Completed or DurableSliceStatus.DeadLettered || current.LeaseExpiresAtUtc > nowUtc.ToUniversalTime()) { tx.Commit(); return null; }
            var attempt = current.Attempt + 1; var expires = nowUtc.ToUniversalTime() + leaseDuration;
            InsertEvent(c, tx, operationId, jobId, sliceStartUtc, sliceEndUtc, current.GenerationId, DurableSliceStatus.Running, null, attempt, "{}", leaseOwner, nowUtc);
            Upsert(c, tx, jobId, sliceStartUtc, sliceEndUtc, current.GenerationId, DurableSliceStatus.Running, attempt, leaseOwner, expires, operationId, null, nowUtc);
            tx.Commit(); return Get(jobId, sliceStartUtc, sliceEndUtc);
        }

        public bool CompleteLease(string operationId, string jobId, DateTimeOffset sliceStartUtc, DateTimeOffset sliceEndUtc, string leaseOwner, string leaseToken, DateTimeOffset nowUtc, string payloadJson = "{}") => FinishLease(operationId, jobId, sliceStartUtc, sliceEndUtc, leaseOwner, leaseToken, nowUtc, DurableSliceStatus.Completed, null, payloadJson);
        public bool FailLease(string operationId, string jobId, DateTimeOffset sliceStartUtc, DateTimeOffset sliceEndUtc, string leaseOwner, string leaseToken, DateTimeOffset nowUtc, string reason, string payloadJson = "{}") => FinishLease(operationId, jobId, sliceStartUtc, sliceEndUtc, leaseOwner, leaseToken, nowUtc, DurableSliceStatus.Failed, reason, payloadJson);
        public bool DeadLetterLease(string operationId, string jobId, DateTimeOffset sliceStartUtc, DateTimeOffset sliceEndUtc, string leaseOwner, string leaseToken, DateTimeOffset nowUtc, string reason, string payloadJson = "{}") => FinishLease(operationId, jobId, sliceStartUtc, sliceEndUtc, leaseOwner, leaseToken, nowUtc, DurableSliceStatus.DeadLettered, reason, payloadJson);

        public DependencyReadiness EvaluateDependencyReadiness(JobDefinition downstream, SliceRange downstreamSlice, IReadOnlyDictionary<string, JobDefinition> jobsById)
            => DependencyReadinessEvaluator.Evaluate(downstream, downstreamSlice, jobsById, ListCompletedSliceKeys());

        // Loads every Completed slice key across all jobs in a single query. The scheduler loads this
        // once per pass and reuses it for all dependency-readiness checks instead of reloading the full
        // set per schedulable slice.
        public IReadOnlySet<SliceKey> ListCompletedSliceKeys() => SqliteTransientRetry.Execute(() =>
        {
            var completed = new HashSet<SliceKey>(); using var c = connectionFactory.OpenConnection();
            using (var cmd = SqliteStorage.Command(c, null, "SELECT job_id, slice_start_utc, slice_end_utc FROM current_slice_state WHERE state = 'Completed';"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) completed.Add(SliceKey.Create(r.GetString(0), DateTimeOffset.Parse(r.GetString(1)), DateTimeOffset.Parse(r.GetString(2))));
            return (IReadOnlySet<SliceKey>)completed;
        });

        // Loads the current state and version of every materialized slice of a job in a single query,
        // keyed by UTC slice start. Keys absent from the map are Missing. The scheduler loads this once
        // per job per pass so per-slice status lookups are in-memory instead of one connection per slice.
        public IReadOnlyDictionary<DateTimeOffset, SliceSchedulingState> ListSliceStates(string jobId) => SqliteTransientRetry.Execute(() =>
        {
            var map = new Dictionary<DateTimeOffset, SliceSchedulingState>();
            using var c = connectionFactory.OpenConnection();
            // version = number of slice_state_events rows for the slice. Computed with a single
            // grouped join (restricted to this job so it uses the (job_id, slice_start_utc,
            // slice_end_utc, ...) index) rather than a per-row correlated subquery: fewer/shorter
            // steps means a smaller window for the read to collide with concurrent slice-state
            // writers, which is what surfaced as a transient SQLITE_ABORT under load.
            using var cmd = SqliteStorage.Command(c, null, "SELECT css.slice_start_utc AS slice_start_utc, css.state AS state, COALESCE(ev.version, 0) AS version FROM current_slice_state css LEFT JOIN (SELECT job_id, slice_start_utc, slice_end_utc, COUNT(*) AS version FROM slice_state_events WHERE job_id=$j GROUP BY job_id, slice_start_utc, slice_end_utc) ev ON ev.job_id=css.job_id AND ev.slice_start_utc=css.slice_start_utc AND ev.slice_end_utc=css.slice_end_utc WHERE css.job_id=$j;");
            cmd.Add("$j", jobId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var start = SqliteStorage.ReadUtc(r, "slice_start_utc");
                var status = Enum.Parse<DurableSliceStatus>(r.GetString(r.GetOrdinal("state")));
                var version = r.GetInt64(r.GetOrdinal("version"));
                map[start.ToUniversalTime()] = new SliceSchedulingState(status, version);
            }
            return (IReadOnlyDictionary<DateTimeOffset, SliceSchedulingState>)map;
        });

        private bool FinishLease(string op, string jobId, DateTimeOffset start, DateTimeOffset end, string owner, string leaseToken, DateTimeOffset now, DurableSliceStatus status, string? reason, string payload)
        {
            if (string.IsNullOrWhiteSpace(leaseToken)) throw new ArgumentException("A lease token is required for terminal slice transitions.", nameof(leaseToken));
            using var c = connectionFactory.OpenConnection(); using var tx = c.BeginTransaction(System.Data.IsolationLevel.Serializable); var cur = Get(c, tx, jobId, start, end);
            if (cur is null || cur.Status != DurableSliceStatus.Running || cur.LeaseOwner != owner || cur.LastEventId != leaseToken || cur.LeaseExpiresAtUtc <= now.ToUniversalTime()) { tx.Commit(); return false; }
            InsertEvent(c, tx, op, jobId, start, end, cur.GenerationId, status, reason, cur.Attempt, payload, owner, now); Upsert(c, tx, jobId, start, end, cur.GenerationId, status, cur.Attempt, null, null, op, reason, now);
            tx.Commit(); return true;
        }

        private static DurableSliceState Missing(string jobId, DateTimeOffset start, DateTimeOffset end) => new(jobId, start.ToUniversalTime(), end.ToUniversalTime(), null, DurableSliceStatus.Missing, 0, null, null, null, null, null, 0, DateTimeOffset.MinValue);
        private static DurableSliceState? Get(SqliteConnection c, SqliteTransaction? tx, string jobId, DateTimeOffset start, DateTimeOffset end)
        {
            using var cmd = SqliteStorage.Command(c, tx, "SELECT css.*, (SELECT COUNT(*) FROM slice_state_events e WHERE e.job_id=css.job_id AND e.slice_start_utc=css.slice_start_utc AND e.slice_end_utc=css.slice_end_utc) version FROM current_slice_state css WHERE job_id=$j AND slice_start_utc=$s AND slice_end_utc=$e;");
            cmd.Add("$j", jobId); cmd.Add("$s", SqliteStorage.Utc(start)); cmd.Add("$e", SqliteStorage.Utc(end)); using var r = cmd.ExecuteReader(); if (!r.Read()) return null;
            return new DurableSliceState(r.GetString(r.GetOrdinal("job_id")), SqliteStorage.ReadUtc(r, "slice_start_utc"), SqliteStorage.ReadUtc(r, "slice_end_utc"), r.IsDBNull(r.GetOrdinal("generation_id")) ? null : r.GetString(r.GetOrdinal("generation_id")), Enum.Parse<DurableSliceStatus>(r.GetString(r.GetOrdinal("state"))), r.GetInt32(r.GetOrdinal("attempt")), r.IsDBNull(r.GetOrdinal("lease_owner")) ? null : r.GetString(r.GetOrdinal("lease_owner")), SqliteStorage.ReadNullableUtc(r, "lease_expires_at_utc"), r.IsDBNull(r.GetOrdinal("last_event_id")) ? null : r.GetString(r.GetOrdinal("last_event_id")), r.IsDBNull(r.GetOrdinal("last_error_code")) ? null : r.GetString(r.GetOrdinal("last_error_code")), r.IsDBNull(r.GetOrdinal("last_error_message")) ? null : r.GetString(r.GetOrdinal("last_error_message")), r.GetInt64(r.GetOrdinal("version")), SqliteStorage.ReadUtc(r, "updated_at_utc"));
        }
        private static void InsertEvent(SqliteConnection c, SqliteTransaction tx, string id, string jobId, DateTimeOffset start, DateTimeOffset end, string? gen, DurableSliceStatus state, string? reason, int attempt, string payload, string? actor, DateTimeOffset recordedAtUtc)
        {
            using var cmd = SqliteStorage.Command(c, tx, "INSERT INTO slice_state_events (event_id, job_id, slice_start_utc, slice_end_utc, generation_id, event_type, state, reason, attempt, payload_json, actor, recorded_at_utc) VALUES ($id,$j,$s,$e,$g,$t,$st,$r,$a,$p,$actor,$recorded);");
            cmd.Add("$id", id); cmd.Add("$j", jobId); cmd.Add("$s", SqliteStorage.Utc(start)); cmd.Add("$e", SqliteStorage.Utc(end)); cmd.Add("$g", gen); cmd.Add("$t", state.ToString()); cmd.Add("$st", state.ToString()); cmd.Add("$r", reason); cmd.Add("$a", attempt); cmd.Add("$p", payload); cmd.Add("$actor", actor); cmd.Add("$recorded", SqliteStorage.Utc(recordedAtUtc)); cmd.ExecuteNonQuery();
        }
        private static void Upsert(SqliteConnection c, SqliteTransaction tx, string jobId, DateTimeOffset start, DateTimeOffset end, string? gen, DurableSliceStatus state, int attempt, string? owner, DateTimeOffset? expires, string eventId, string? error, DateTimeOffset now)
        {
            using var cmd = SqliteStorage.Command(c, tx, "INSERT INTO current_slice_state (job_id,slice_start_utc,slice_end_utc,generation_id,state,attempt,lease_owner,lease_expires_at_utc,last_event_id,last_error_code,last_error_message,updated_at_utc) VALUES ($j,$s,$e,$g,$st,$a,$o,$x,$ev,$err,$err,$u) ON CONFLICT(job_id,slice_start_utc,slice_end_utc) DO UPDATE SET generation_id=excluded.generation_id,state=excluded.state,attempt=excluded.attempt,lease_owner=excluded.lease_owner,lease_expires_at_utc=excluded.lease_expires_at_utc,last_event_id=excluded.last_event_id,last_error_code=excluded.last_error_code,last_error_message=excluded.last_error_message,updated_at_utc=excluded.updated_at_utc;");
            cmd.Add("$j", jobId); cmd.Add("$s", SqliteStorage.Utc(start)); cmd.Add("$e", SqliteStorage.Utc(end)); cmd.Add("$g", gen); cmd.Add("$st", state.ToString()); cmd.Add("$a", attempt); cmd.Add("$o", owner); cmd.Add("$x", expires is null ? null : SqliteStorage.Utc(expires.Value)); cmd.Add("$ev", eventId); cmd.Add("$err", error); cmd.Add("$u", SqliteStorage.Utc(now)); cmd.ExecuteNonQuery();
        }
    }
}
