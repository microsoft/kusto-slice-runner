using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KoLite.Local.Core.Rerun;
using KoLite.Local.Core.Schedules;
using KoLite.Local.Core.Scheduling;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Rerun
{
    public sealed class SqliteRerunService(
        IKoLiteSqliteConnectionFactory connectionFactory,
        SqliteJobCatalogRepository catalog,
        IClock clock)
    {
        public RerunPlanResult Plan(RerunPlanRequest request) => BuildPlan(request, "preview");

        public RerunPlanResult CreatePlan(RerunPlanRequest request)
        {
            var batchId = Guid.NewGuid().ToString("N");
            var plan = BuildPlan(request, batchId);
            using var connection = connectionFactory.OpenConnection();
            using var transaction = connection.BeginTransaction();
            InsertPlan(connection, transaction, request, plan);
            transaction.Commit();
            return plan;
        }

        public RerunBatchReadout? GetBatch(string rerunBatchId)
        {
            using var connection = connectionFactory.OpenConnection();
            using var batch = SqliteStorage.Command(connection, null, "SELECT * FROM rerun_batches WHERE rerun_batch_id=$id;");
            batch.Add("$id", rerunBatchId);
            BatchReadoutRow row;
            using (var reader = batch.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return null;
                }

                row = new BatchReadoutRow(
                    reader.GetString(reader.GetOrdinal("rerun_batch_id")),
                    reader.GetString(reader.GetOrdinal("root_job_id")),
                    SqliteStorage.ReadUtc(reader, "root_start_utc"),
                    SqliteStorage.ReadUtc(reader, "root_end_utc"),
                    reader.IsDBNull(reader.GetOrdinal("requested_by")) ? null : reader.GetString(reader.GetOrdinal("requested_by")),
                    reader.GetString(reader.GetOrdinal("reason")),
                    Enum.Parse<RerunBatchStatus>(reader.GetString(reader.GetOrdinal("status"))),
                    reader.GetInt32(reader.GetOrdinal("kusto_cleanup_acknowledged")) == 1,
                    reader.GetString(reader.GetOrdinal("kusto_cleanup_commands")),
                    SqliteStorage.ReadUtc(reader, "requested_at_utc"),
                    SqliteStorage.ReadNullableUtc(reader, "completed_at_utc"));
            }

            var jobs = catalog.List().ToDictionary(j => j.JobId, StringComparer.Ordinal);
            var slices = ReadPersistedSlices(connection, rerunBatchId, jobs);
            return new RerunBatchReadout(
                row.RerunBatchId,
                row.RootJobId,
                row.RootStartUtc,
                row.RootEndUtc,
                row.RequestedBy,
                row.Reason,
                row.Status,
                row.KustoCleanupAcknowledged,
                row.KustoCleanupCommands,
                row.RequestedAtUtc,
                row.CompletedAtUtc,
                slices);
        }

        public RerunExecuteResult Execute(RerunExecuteRequest request)
        {
            if (!request.KustoCleanupAcknowledged)
            {
                throw new InvalidOperationException("Rerun requires acknowledgement that Kusto cleanup has been handled.");
            }

            using var connection = connectionFactory.OpenConnection();
            using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
            var batch = ReadBatchForUpdate(connection, transaction, request.RerunBatchId);
            if (batch is null)
            {
                throw new InvalidOperationException($"Rerun batch '{request.RerunBatchId}' does not exist.");
            }

            if (batch.Value.Status == RerunBatchStatus.Completed)
            {
                transaction.Commit();
                return new RerunExecuteResult(request.RerunBatchId, RerunBatchStatus.Completed, 0);
            }

            var slices = ReadPersistedSlices(connection, transaction, request.RerunBatchId);
            if (slices.Count == 0)
            {
                throw new InvalidOperationException($"Rerun batch '{request.RerunBatchId}' has no slices.");
            }

            var blockers = slices
                .Select(slice => ActiveBlocker(connection, transaction, slice.JobId, slice.Slice.StartUtc, slice.Slice.EndUtc))
                .Where(static blocker => blocker is not null)
                .ToArray();
            if (blockers.Length > 0)
            {
                throw new InvalidOperationException("Rerun cannot execute while affected slices have active work: " + string.Join("; ", blockers));
            }

            foreach (var slice in slices)
            {
                var snapshot = SnapshotSlice(connection, transaction, slice.JobId, slice.Slice.StartUtc, slice.Slice.EndUtc);
                DeleteSliceRows(connection, transaction, slice.JobId, slice.Slice.StartUtc, slice.Slice.EndUtc);
                UpdateRerunSliceReset(connection, transaction, request.RerunBatchId, slice, snapshot);
            }

            using (var update = SqliteStorage.Command(connection, transaction, """
                UPDATE rerun_batches
                SET status=$status,
                    kusto_cleanup_acknowledged=1,
                    completed_at_utc=$now,
                    summary_json=$summary
                WHERE rerun_batch_id=$id;
                """))
            {
                update.Add("$status", RerunBatchStatus.Completed.ToString());
                update.Add("$now", SqliteStorage.Utc(clock.UtcNow));
                update.Add("$summary", JsonSerializer.Serialize(new { resetSlices = slices.Count, actor = request.RequestedBy }, SqliteStorage.JsonOptions));
                update.Add("$id", request.RerunBatchId);
                update.ExecuteNonQuery();
            }

            InsertAudit(connection, transaction, request.RequestedBy, "RerunExecuted", "RerunBatch", request.RerunBatchId, new { resetSlices = slices.Count });
            transaction.Commit();
            return new RerunExecuteResult(request.RerunBatchId, RerunBatchStatus.Completed, slices.Count);
        }

        private RerunPlanResult BuildPlan(RerunPlanRequest request, string batchId)
        {
            if (string.IsNullOrWhiteSpace(request.JobId))
            {
                throw new ArgumentException("Root job id is required.", nameof(request));
            }

            var jobs = catalog.List().ToDictionary(j => j.JobId, StringComparer.Ordinal);
            if (!jobs.TryGetValue(request.JobId, out var root))
            {
                throw new InvalidOperationException($"Job '{request.JobId}' does not exist.");
            }

            var start = request.StartUtc.ToUniversalTime();
            var end = request.EndUtc.ToUniversalTime();
            ValidateAlignedRange(root.Definition, start, end);

            var affected = ExpandAffectedSlices(root, jobs, start, end);
            if (affected.Count == 0)
            {
                throw new InvalidOperationException("The requested range does not include any root slices.");
            }

            using var connection = connectionFactory.OpenConnection();
            var slices = affected
                .OrderBy(s => s.Slice.StartUtc)
                .ThenBy(s => s.JobId, StringComparer.Ordinal)
                .Select(s => BuildAffectedReadout(connection, null, s, jobs[s.JobId]))
                .ToArray();

            var status = slices.Any(s => s.BlockerReason is not null) ? RerunBatchStatus.Blocked : RerunBatchStatus.Planned;
            return new RerunPlanResult(batchId, status, slices, GenerateKustoCleanupCommands(slices), status == RerunBatchStatus.Planned);
        }

        private static IReadOnlyList<RerunSliceCandidate> ExpandAffectedSlices(
            JobCatalogRecord root,
            IReadOnlyDictionary<string, JobCatalogRecord> jobs,
            DateTimeOffset start,
            DateTimeOffset end)
        {
            var affected = new Dictionary<string, Dictionary<string, RerunSliceCandidate>>(StringComparer.Ordinal);
            foreach (var slice in SliceEnumerator.Enumerate(root.JobId, start, end, root.Definition.QueryWindowSize))
            {
                AddAffected(affected, new RerunSliceCandidate(root.JobId, slice, RerunSliceRole.Root));
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var downstream in jobs.Values)
                {
                    foreach (var dependency in downstream.Definition.DependsOn)
                    {
                        if (!affected.TryGetValue(dependency.ActivityId, out var upstreamSlices) ||
                            !jobs.TryGetValue(dependency.ActivityId, out var upstream))
                        {
                            continue;
                        }

                        foreach (var upstreamSlice in upstreamSlices.Values.ToArray())
                        {
                            foreach (var candidate in CandidateDownstreamSlices(downstream.Definition, upstreamSlice.Slice))
                            {
                                var required = DependencyReadinessEvaluator.RequiredUpstreamSlices(upstream.Definition, candidate)
                                    .Select(s => s.ToKey())
                                    .ToHashSet();
                                if (!required.Contains(upstreamSlice.Slice.ToKey()))
                                {
                                    continue;
                                }

                                changed |= AddAffected(affected, new RerunSliceCandidate(downstream.JobId, candidate, RerunSliceRole.Downstream));
                            }
                        }
                    }
                }
            }

            return affected.Values.SelectMany(s => s.Values).ToArray();
        }

        private static IEnumerable<SliceRange> CandidateDownstreamSlices(JobDefinition downstream, SliceRange upstreamSlice)
        {
            var start = AlignFloor(upstreamSlice.StartUtc, downstream.StartFrom, downstream.QueryWindowSize);
            if (start < downstream.StartFrom.ToUniversalTime())
            {
                start = downstream.StartFrom.ToUniversalTime();
            }

            for (var cursor = start; cursor < upstreamSlice.EndUtc; cursor = cursor.Add(downstream.QueryWindowSize))
            {
                var candidate = new SliceRange(downstream.ActivityId, cursor, cursor.Add(downstream.QueryWindowSize));
                if (candidate.EndUtc <= upstreamSlice.StartUtc || candidate.StartUtc >= upstreamSlice.EndUtc)
                {
                    continue;
                }

                if (downstream.EndOn is { } endOn && candidate.EndUtc > endOn.ToUniversalTime())
                {
                    continue;
                }

                yield return candidate;
            }
        }

        private static bool AddAffected(Dictionary<string, Dictionary<string, RerunSliceCandidate>> affected, RerunSliceCandidate candidate)
        {
            if (!affected.TryGetValue(candidate.JobId, out var slices))
            {
                slices = new Dictionary<string, RerunSliceCandidate>(StringComparer.Ordinal);
                affected[candidate.JobId] = slices;
            }

            var key = candidate.Slice.ToKey().Value;
            if (slices.ContainsKey(key))
            {
                return false;
            }

            slices[key] = candidate;
            return true;
        }

        private static void ValidateAlignedRange(JobDefinition job, DateTimeOffset start, DateTimeOffset end)
        {
            if (end <= start)
            {
                throw new InvalidOperationException("Rerun end must be after start.");
            }

            if (start < job.StartFrom.ToUniversalTime())
            {
                throw new InvalidOperationException($"Rerun start must not be before job start {SqliteStorage.Utc(job.StartFrom)}.");
            }

            if (job.EndOn is { } endOn && end > endOn.ToUniversalTime())
            {
                throw new InvalidOperationException($"Rerun end must not be after job end {SqliteStorage.Utc(endOn)}.");
            }

            if (!IsAligned(start, job.StartFrom, job.QueryWindowSize) || !IsAligned(end, job.StartFrom, job.QueryWindowSize))
            {
                throw new InvalidOperationException("Rerun start and end must align to this job's slice boundaries.");
            }
        }

        private static bool IsAligned(DateTimeOffset value, DateTimeOffset anchor, TimeSpan queryWindow) =>
            (value.ToUniversalTime().Ticks - anchor.ToUniversalTime().Ticks) % queryWindow.Ticks == 0;

        private static DateTimeOffset AlignFloor(DateTimeOffset value, DateTimeOffset anchor, TimeSpan queryWindow)
        {
            value = value.ToUniversalTime();
            anchor = anchor.ToUniversalTime();
            var quotient = Math.DivRem(value.Ticks - anchor.Ticks, queryWindow.Ticks, out var remainder);
            if (value.Ticks < anchor.Ticks && remainder != 0)
            {
                quotient--;
            }

            return anchor.AddTicks(quotient * queryWindow.Ticks);
        }

        private RerunAffectedSlice BuildAffectedReadout(SqliteConnection connection, SqliteTransaction? transaction, RerunSliceCandidate candidate, JobCatalogRecord job)
        {
            var previous = ReadCurrentState(connection, transaction, candidate.JobId, candidate.Slice.StartUtc, candidate.Slice.EndUtc);
            var queueRows = Count(connection, transaction, "work_queue", candidate.JobId, candidate.Slice.StartUtc, candidate.Slice.EndUtc);
            var activeQueueRows = Count(connection, transaction, "work_queue", candidate.JobId, candidate.Slice.StartUtc, candidate.Slice.EndUtc, "state IN ('Queued','Leased')");
            var attemptRows = Count(connection, transaction, "slice_attempts", candidate.JobId, candidate.Slice.StartUtc, candidate.Slice.EndUtc);
            var eventRows = Count(connection, transaction, "slice_state_events", candidate.JobId, candidate.Slice.StartUtc, candidate.Slice.EndUtc);
            var logRows = Count(connection, transaction, "operational_logs", candidate.JobId, candidate.Slice.StartUtc, candidate.Slice.EndUtc);
            var scheduledRows = Count(connection, transaction, "scheduled_slices", candidate.JobId, candidate.Slice.StartUtc, candidate.Slice.EndUtc);
            var blocker = ActiveBlocker(previous?.State, activeQueueRows);

            return new RerunAffectedSlice(
                candidate.JobId,
                candidate.Slice,
                candidate.Role,
                job.Definition.OutputTable,
                job.Definition.Target.ClusterUri,
                job.Definition.Target.Database,
                previous?.State,
                previous?.Attempt,
                queueRows,
                attemptRows,
                eventRows,
                logRows,
                scheduledRows,
                blocker,
                blocker is null ? RerunSliceStatus.Planned : RerunSliceStatus.Blocked);
        }

        private static string? ActiveBlocker(string? state, int activeQueueRows)
        {
            if (state is "Queued" or "Running")
            {
                return $"Current state is {state}.";
            }

            return activeQueueRows > 0 ? $"{activeQueueRows} queued or leased work item(s) still exist." : null;
        }

        private static string? ActiveBlocker(SqliteConnection connection, SqliteTransaction transaction, string jobId, DateTimeOffset start, DateTimeOffset end)
        {
            var previous = ReadCurrentState(connection, transaction, jobId, start, end);
            var queueRows = Count(connection, transaction, "work_queue", jobId, start, end, "state IN ('Queued','Leased')");
            return ActiveBlocker(previous?.State, queueRows) is { } blocker
                ? $"{jobId} {SqliteStorage.Utc(start)}-{SqliteStorage.Utc(end)}: {blocker}"
                : null;
        }

        private static string GenerateKustoCleanupCommands(IReadOnlyList<RerunAffectedSlice> slices)
        {
            var builder = new StringBuilder();
            foreach (var group in slices.GroupBy(s => new { s.ClusterUri, s.Database, s.OutputTable }).OrderBy(g => g.Key.ClusterUri).ThenBy(g => g.Key.Database).ThenBy(g => g.Key.OutputTable))
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                    builder.AppendLine();
                }

                builder.AppendLine($"// Cluster: {group.Key.ClusterUri}");
                builder.AppendLine($"// Database: {group.Key.Database}");
                builder.AppendLine($".delete table {group.Key.OutputTable} records <|");
                builder.AppendLine($"    {group.Key.OutputTable}");
                var ranges = MergeRanges(group.Select(s => (s.Slice.StartUtc, s.Slice.EndUtc)));
                for (var i = 0; i < ranges.Count; i++)
                {
                    var prefix = i == 0 ? "    | where " : "        or ";
                    builder.Append(prefix);
                    builder.Append("(StartTime < datetime(");
                    builder.Append(SqliteStorage.Utc(ranges[i].EndUtc));
                    builder.Append(") and EndTime > datetime(");
                    builder.Append(SqliteStorage.Utc(ranges[i].StartUtc));
                    builder.AppendLine("))");
                }
            }

            return builder.ToString().TrimEnd();
        }

        private static IReadOnlyList<(DateTimeOffset StartUtc, DateTimeOffset EndUtc)> MergeRanges(IEnumerable<(DateTimeOffset StartUtc, DateTimeOffset EndUtc)> source)
        {
            var ranges = source.OrderBy(r => r.StartUtc).ToArray();
            if (ranges.Length == 0)
            {
                return [];
            }

            var merged = new List<(DateTimeOffset StartUtc, DateTimeOffset EndUtc)>();
            var current = ranges[0];
            foreach (var next in ranges.Skip(1))
            {
                if (next.StartUtc <= current.EndUtc)
                {
                    current = (current.StartUtc, next.EndUtc > current.EndUtc ? next.EndUtc : current.EndUtc);
                    continue;
                }

                merged.Add(current);
                current = next;
            }

            merged.Add(current);
            return merged;
        }

        private void InsertPlan(SqliteConnection connection, SqliteTransaction transaction, RerunPlanRequest request, RerunPlanResult plan)
        {
            using (var insert = SqliteStorage.Command(connection, transaction, """
                INSERT INTO rerun_batches (rerun_batch_id, root_job_id, root_start_utc, root_end_utc, requested_by, reason, status, kusto_cleanup_commands, summary_json, requested_at_utc)
                VALUES ($id,$job,$start,$end,$by,$reason,$status,$commands,$summary,$now);
                """))
            {
                insert.Add("$id", plan.RerunBatchId);
                insert.Add("$job", request.JobId);
                insert.Add("$start", SqliteStorage.Utc(request.StartUtc));
                insert.Add("$end", SqliteStorage.Utc(request.EndUtc));
                insert.Add("$by", string.IsNullOrWhiteSpace(request.RequestedBy) ? null : request.RequestedBy);
                insert.Add("$reason", string.IsNullOrWhiteSpace(request.Reason) ? "Manual rerun" : request.Reason);
                insert.Add("$status", plan.Status.ToString());
                insert.Add("$commands", plan.KustoCleanupCommands);
                insert.Add("$summary", JsonSerializer.Serialize(new { sliceCount = plan.Slices.Count, blocked = plan.BlockedSlices.Count }, SqliteStorage.JsonOptions));
                insert.Add("$now", SqliteStorage.Utc(clock.UtcNow));
                insert.ExecuteNonQuery();
            }

            foreach (var slice in plan.Slices)
            {
                using var insertSlice = SqliteStorage.Command(connection, transaction, """
                    INSERT INTO rerun_slices (rerun_slice_id, rerun_batch_id, job_id, slice_start_utc, slice_end_utc, role, previous_state, previous_attempt, status, blocker_reason, snapshot_json, created_at_utc, updated_at_utc)
                    VALUES ($id,$batch,$job,$start,$end,$role,$state,$attempt,$status,$blocker,$snapshot,$now,$now);
                    """);
                insertSlice.Add("$id", StableId("rerun-slice", plan.RerunBatchId, slice.JobId, SqliteStorage.Utc(slice.Slice.StartUtc), SqliteStorage.Utc(slice.Slice.EndUtc)));
                insertSlice.Add("$batch", plan.RerunBatchId);
                insertSlice.Add("$job", slice.JobId);
                insertSlice.Add("$start", SqliteStorage.Utc(slice.Slice.StartUtc));
                insertSlice.Add("$end", SqliteStorage.Utc(slice.Slice.EndUtc));
                insertSlice.Add("$role", slice.Role.ToString());
                insertSlice.Add("$state", slice.PreviousState);
                insertSlice.Add("$attempt", slice.PreviousAttempt);
                insertSlice.Add("$status", slice.BlockerReason is null ? RerunSliceStatus.Planned.ToString() : RerunSliceStatus.Blocked.ToString());
                insertSlice.Add("$blocker", slice.BlockerReason);
                insertSlice.Add("$snapshot", JsonSerializer.Serialize(new
                {
                    counts = new
                    {
                        slice.QueueRows,
                        slice.AttemptRows,
                        slice.EventRows,
                        slice.LogRows,
                        slice.ScheduledRows
                    }
                }, SqliteStorage.JsonOptions));
                insertSlice.Add("$now", SqliteStorage.Utc(clock.UtcNow));
                insertSlice.ExecuteNonQuery();
            }

            InsertAudit(connection, transaction, request.RequestedBy, "RerunPlanned", "RerunBatch", plan.RerunBatchId, new { request.JobId, request.StartUtc, request.EndUtc, plan.Status });
        }

        private IReadOnlyList<RerunAffectedSlice> ReadPersistedSlices(SqliteConnection connection, string rerunBatchId, IReadOnlyDictionary<string, JobCatalogRecord> jobs)
        {
            using var transaction = connection.BeginTransaction();
            var rows = ReadPersistedSlices(connection, transaction, rerunBatchId, jobs);
            transaction.Commit();
            return rows;
        }

        private IReadOnlyList<RerunAffectedSlice> ReadPersistedSlices(SqliteConnection connection, SqliteTransaction transaction, string rerunBatchId)
        {
            var jobs = catalog.List().ToDictionary(j => j.JobId, StringComparer.Ordinal);
            return ReadPersistedSlices(connection, transaction, rerunBatchId, jobs);
        }

        private static IReadOnlyList<RerunAffectedSlice> ReadPersistedSlices(SqliteConnection connection, SqliteTransaction transaction, string rerunBatchId, IReadOnlyDictionary<string, JobCatalogRecord> jobs)
        {
            using var command = SqliteStorage.Command(connection, transaction, "SELECT * FROM rerun_slices WHERE rerun_batch_id=$id ORDER BY slice_start_utc, job_id;");
            command.Add("$id", rerunBatchId);
            using var reader = command.ExecuteReader();
            var rows = new List<RerunAffectedSlice>();
            while (reader.Read())
            {
                var jobId = reader.GetString(reader.GetOrdinal("job_id"));
                var start = SqliteStorage.ReadUtc(reader, "slice_start_utc");
                var end = SqliteStorage.ReadUtc(reader, "slice_end_utc");
                var counts = ReadCounts(reader.GetString(reader.GetOrdinal("snapshot_json")));
                var job = jobs[jobId];
                rows.Add(new RerunAffectedSlice(
                    jobId,
                    new SliceRange(jobId, start, end),
                    Enum.Parse<RerunSliceRole>(reader.GetString(reader.GetOrdinal("role"))),
                    job.Definition.OutputTable,
                    job.Definition.Target.ClusterUri,
                    job.Definition.Target.Database,
                    reader.IsDBNull(reader.GetOrdinal("previous_state")) ? null : reader.GetString(reader.GetOrdinal("previous_state")),
                    reader.IsDBNull(reader.GetOrdinal("previous_attempt")) ? null : reader.GetInt32(reader.GetOrdinal("previous_attempt")),
                    counts.QueueRows,
                    counts.AttemptRows,
                    counts.EventRows,
                    counts.LogRows,
                    counts.ScheduledRows,
                    reader.IsDBNull(reader.GetOrdinal("blocker_reason")) ? null : reader.GetString(reader.GetOrdinal("blocker_reason")),
                    Enum.Parse<RerunSliceStatus>(reader.GetString(reader.GetOrdinal("status"))),
                    reader.GetString(reader.GetOrdinal("snapshot_json"))));
            }

            return rows;
        }

        private static (int QueueRows, int AttemptRows, int EventRows, int LogRows, int ScheduledRows) ReadCounts(string snapshotJson)
        {
            using var document = JsonDocument.Parse(snapshotJson);
            if (!document.RootElement.TryGetProperty("counts", out var counts))
            {
                return (0, 0, 0, 0, 0);
            }

            return (
                ReadInt(counts, "queueRows"),
                ReadInt(counts, "attemptRows"),
                ReadInt(counts, "eventRows"),
                ReadInt(counts, "logRows"),
                ReadInt(counts, "scheduledRows"));
        }

        private static int ReadInt(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

        private static BatchRow? ReadBatchForUpdate(SqliteConnection connection, SqliteTransaction transaction, string batchId)
        {
            using var command = SqliteStorage.Command(connection, transaction, "SELECT rerun_batch_id,status FROM rerun_batches WHERE rerun_batch_id=$id;");
            command.Add("$id", batchId);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new BatchRow(reader.GetString(0), Enum.Parse<RerunBatchStatus>(reader.GetString(1)))
                : null;
        }

        private static CurrentStateRow? ReadCurrentState(SqliteConnection connection, SqliteTransaction? transaction, string jobId, DateTimeOffset start, DateTimeOffset end)
        {
            using var command = SqliteStorage.Command(connection, transaction, "SELECT state, attempt FROM current_slice_state WHERE job_id=$job AND slice_start_utc=$start AND slice_end_utc=$end;");
            command.Add("$job", jobId);
            command.Add("$start", SqliteStorage.Utc(start));
            command.Add("$end", SqliteStorage.Utc(end));
            using var reader = command.ExecuteReader();
            return reader.Read() ? new CurrentStateRow(reader.GetString(0), reader.GetInt32(1)) : null;
        }

        private static int Count(SqliteConnection connection, SqliteTransaction? transaction, string table, string jobId, DateTimeOffset start, DateTimeOffset end, string? extraPredicate = null)
        {
            var sql = $"SELECT COUNT(*) FROM {table} WHERE job_id=$job AND slice_start_utc=$start AND slice_end_utc=$end";
            if (!string.IsNullOrWhiteSpace(extraPredicate))
            {
                sql += $" AND {extraPredicate}";
            }

            using var command = SqliteStorage.Command(connection, transaction, sql + ";");
            command.Add("$job", jobId);
            command.Add("$start", SqliteStorage.Utc(start));
            command.Add("$end", SqliteStorage.Utc(end));
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string SnapshotSlice(SqliteConnection connection, SqliteTransaction transaction, string jobId, DateTimeOffset start, DateTimeOffset end)
        {
            var snapshot = new
            {
                currentState = QueryRows(connection, transaction, "SELECT * FROM current_slice_state WHERE job_id=$job AND slice_start_utc=$start AND slice_end_utc=$end;", jobId, start, end),
                stateEvents = QueryRows(connection, transaction, "SELECT * FROM slice_state_events WHERE job_id=$job AND slice_start_utc=$start AND slice_end_utc=$end ORDER BY recorded_at_utc, event_id;", jobId, start, end),
                attempts = QueryRows(connection, transaction, "SELECT * FROM slice_attempts WHERE job_id=$job AND slice_start_utc=$start AND slice_end_utc=$end ORDER BY attempt, attempt_id;", jobId, start, end),
                logs = QueryRows(connection, transaction, "SELECT * FROM operational_logs WHERE job_id=$job AND slice_start_utc=$start AND slice_end_utc=$end ORDER BY recorded_at_utc, log_id;", jobId, start, end),
                scheduledSlices = QueryRows(connection, transaction, "SELECT * FROM scheduled_slices WHERE job_id=$job AND slice_start_utc=$start AND slice_end_utc=$end;", jobId, start, end),
                queueRows = QueryRows(connection, transaction, "SELECT * FROM work_queue WHERE job_id=$job AND slice_start_utc=$start AND slice_end_utc=$end ORDER BY created_at_utc, queue_item_id;", jobId, start, end),
                counts = new
                {
                    queueRows = Count(connection, transaction, "work_queue", jobId, start, end),
                    attemptRows = Count(connection, transaction, "slice_attempts", jobId, start, end),
                    eventRows = Count(connection, transaction, "slice_state_events", jobId, start, end),
                    logRows = Count(connection, transaction, "operational_logs", jobId, start, end),
                    scheduledRows = Count(connection, transaction, "scheduled_slices", jobId, start, end)
                }
            };
            return JsonSerializer.Serialize(snapshot, SqliteStorage.JsonOptions);
        }

        private static IReadOnlyList<Dictionary<string, object?>> QueryRows(SqliteConnection connection, SqliteTransaction transaction, string sql, string jobId, DateTimeOffset start, DateTimeOffset end)
        {
            using var command = SqliteStorage.Command(connection, transaction, sql);
            command.Add("$job", jobId);
            command.Add("$start", SqliteStorage.Utc(start));
            command.Add("$end", SqliteStorage.Utc(end));
            using var reader = command.ExecuteReader();
            var rows = new List<Dictionary<string, object?>>();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                rows.Add(row);
            }

            return rows;
        }

        private static void DeleteSliceRows(SqliteConnection connection, SqliteTransaction transaction, string jobId, DateTimeOffset start, DateTimeOffset end)
        {
            Delete(connection, transaction, "work_queue", jobId, start, end);
            Delete(connection, transaction, "slice_attempts", jobId, start, end);
            Delete(connection, transaction, "scheduled_slices", jobId, start, end);
            Delete(connection, transaction, "operational_logs", jobId, start, end);
            Delete(connection, transaction, "current_slice_state", jobId, start, end);
            Delete(connection, transaction, "slice_state_events", jobId, start, end);
        }

        private static void Delete(SqliteConnection connection, SqliteTransaction transaction, string table, string jobId, DateTimeOffset start, DateTimeOffset end)
        {
            using var command = SqliteStorage.Command(connection, transaction, $"DELETE FROM {table} WHERE job_id=$job AND slice_start_utc=$start AND slice_end_utc=$end;");
            command.Add("$job", jobId);
            command.Add("$start", SqliteStorage.Utc(start));
            command.Add("$end", SqliteStorage.Utc(end));
            command.ExecuteNonQuery();
        }

        private void UpdateRerunSliceReset(SqliteConnection connection, SqliteTransaction transaction, string batchId, RerunAffectedSlice slice, string snapshotJson)
        {
            using var command = SqliteStorage.Command(connection, transaction, """
                UPDATE rerun_slices
                SET status=$status,
                    snapshot_json=$snapshot,
                    reset_at_utc=$now,
                    updated_at_utc=$now
                WHERE rerun_batch_id=$batch AND job_id=$job AND slice_start_utc=$start AND slice_end_utc=$end;
                """);
            command.Add("$status", RerunSliceStatus.Reset.ToString());
            command.Add("$snapshot", snapshotJson);
            command.Add("$now", SqliteStorage.Utc(clock.UtcNow));
            command.Add("$batch", batchId);
            command.Add("$job", slice.JobId);
            command.Add("$start", SqliteStorage.Utc(slice.Slice.StartUtc));
            command.Add("$end", SqliteStorage.Utc(slice.Slice.EndUtc));
            command.ExecuteNonQuery();
        }

        private static void InsertAudit(SqliteConnection connection, SqliteTransaction transaction, string? actor, string action, string subjectType, string subjectId, object payload)
        {
            using var command = SqliteStorage.Command(connection, transaction, "INSERT INTO system_audit (audit_id,actor,action,subject_type,subject_id,payload_json) VALUES ($id,$actor,$action,$type,$subject,$payload);");
            command.Add("$id", Guid.NewGuid().ToString("N"));
            command.Add("$actor", actor);
            command.Add("$action", action);
            command.Add("$type", subjectType);
            command.Add("$subject", subjectId);
            command.Add("$payload", JsonSerializer.Serialize(payload, SqliteStorage.JsonOptions));
            command.ExecuteNonQuery();
        }

        private static string StableId(params string[] parts)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts)));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private sealed record RerunSliceCandidate(string JobId, SliceRange Slice, RerunSliceRole Role);
        private readonly record struct CurrentStateRow(string State, int Attempt);
        private readonly record struct BatchRow(string BatchId, RerunBatchStatus Status);
        private sealed record BatchReadoutRow(
            string RerunBatchId,
            string RootJobId,
            DateTimeOffset RootStartUtc,
            DateTimeOffset RootEndUtc,
            string? RequestedBy,
            string Reason,
            RerunBatchStatus Status,
            bool KustoCleanupAcknowledged,
            string KustoCleanupCommands,
            DateTimeOffset RequestedAtUtc,
            DateTimeOffset? CompletedAtUtc);
    }
}
