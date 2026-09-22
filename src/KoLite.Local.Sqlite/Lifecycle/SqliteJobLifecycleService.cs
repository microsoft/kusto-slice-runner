// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Infrastructure;
using KoLite.Local.Sqlite.Observability;

namespace KoLite.Local.Sqlite.Lifecycle
{
    public sealed record HardDeleteResult(string PurgeRunId, int DeletedJobs, int DeletedQueueRows, int DeletedStateRows, int DeletedRepairBatches);

    public sealed record HardDeleteBatchItem(string JobId, long ExpectedVersion);

    public sealed record HardDeleteBatchIssue(string JobId, string DisplayName, string Message);

    public sealed record HardDeleteBatchResult(string PurgeRunId, int DeletedJobs, int DeletedQueueRows, int DeletedStateRows, int DeletedRepairBatches);

    public sealed class HardDeleteBatchValidationException : InvalidOperationException
    {
        public HardDeleteBatchValidationException(IReadOnlyList<HardDeleteBatchIssue> issues)
            : base("Bulk hard delete could not continue because one or more selected jobs are no longer eligible.")
        {
            Issues = issues;
        }

        public IReadOnlyList<HardDeleteBatchIssue> Issues { get; }
    }

    // Thrown by SoftDelete when a job still has active downstream dependents and force was not
    // requested. Carries the blocking dependents so callers (the web confirm page, the bulk summary)
    // can name them. Derives from InvalidOperationException so existing catch sites keep behaving
    // sensibly, while new call sites can catch this specific type to branch into the confirm flow.
    public sealed class DownstreamDependentsException : InvalidOperationException
    {
        public DownstreamDependentsException(string jobId, IReadOnlyList<(string JobId, string ActivityId)> dependents)
            : base($"Job '{jobId}' cannot be soft-deleted because {dependents.Count} active job(s) depend on it: {string.Join(", ", dependents.Select(dependent => dependent.ActivityId))}.")
        {
            JobId = jobId;
            Dependents = dependents;
        }

        public string JobId { get; }

        public IReadOnlyList<(string JobId, string ActivityId)> Dependents { get; }
    }

    public sealed class SqliteJobLifecycleService
    {
        private readonly IKoLiteSqliteConnectionFactory connectionFactory;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteLifecycleReadModelRepository lifecycleReadModel;

        // The two-argument constructor is preserved for direct (non-DI) callers; it builds its own
        // read-model repository from the same connection factory. DI selects the greediest resolvable
        // constructor, so the container injects the registered SqliteLifecycleReadModelRepository.
        public SqliteJobLifecycleService(IKoLiteSqliteConnectionFactory connectionFactory, SqliteJobCatalogRepository catalog)
            : this(connectionFactory, catalog, new SqliteLifecycleReadModelRepository(connectionFactory))
        {
        }

        public SqliteJobLifecycleService(IKoLiteSqliteConnectionFactory connectionFactory, SqliteJobCatalogRepository catalog, SqliteLifecycleReadModelRepository lifecycleReadModel)
        {
            this.connectionFactory = connectionFactory;
            this.catalog = catalog;
            this.lifecycleReadModel = lifecycleReadModel;
        }

        // Active (non-soft-deleted) jobs that list jobId as an upstream dependency. A dependent that is
        // itself soft-deleted is excluded because it is already hidden from the active catalog and
        // cannot break.
        public IReadOnlyList<(string JobId, string ActivityId)> GetActiveDependents(string jobId)
        {
            var dependents = catalog.FindDependents(jobId);
            if (dependents.Count == 0)
            {
                return dependents;
            }

            var softDeleted = SoftDeletedJobIds();
            return dependents.Where(dependent => !softDeleted.Contains(dependent.JobId)).ToList();
        }

        public JobCatalogRecord SoftDelete(string jobId, long expectedVersion, string actor, string reason, bool force = false)
        {
            // Block by default when active downstream dependents would silently break; an explicit
            // force override (the confirm page's "Soft delete anyway") proceeds past the check.
            if (!force)
            {
                var dependents = GetActiveDependents(jobId);
                if (dependents.Count > 0)
                {
                    throw new DownstreamDependentsException(jobId, dependents);
                }
            }

            var updated = catalog.SetEnabled(jobId, enabled: false, expectedVersion, actor, eventId: Guid.NewGuid().ToString("N"));
            RecordLifecycle(jobId, "SoftDeleted", actor, reason, new { expectedVersion });
            return updated;
        }

        public JobCatalogRecord Restore(string jobId, long expectedVersion, string actor, string reason)
        {
            var updated = catalog.SetEnabled(jobId, enabled: true, expectedVersion, actor, eventId: Guid.NewGuid().ToString("N"));
            RecordLifecycle(jobId, "Restored", actor, reason, new { expectedVersion });
            return updated;
        }

        public void RecordTransition(string jobId, string eventType, string actor, string reason, object payload)
        {
            RecordLifecycle(jobId, eventType, actor, reason, payload);
        }

        public HardDeleteResult HardDelete(string jobId, string confirmation, string actor, string reason)
        {
            // The confirmation phrase is the human-readable display name (the activity id shown in
            // the UI), not the opaque GUID job id, so an operator confirms against the name they see.
            // The purge below still targets the GUID jobId; only the safety phrase is name-based.
            var record = catalog.Get(jobId) ?? throw new InvalidOperationException($"Job '{jobId}' does not exist.");
            var expected = $"DELETE {record.DisplayName}";
            if (!StringComparer.Ordinal.Equals(confirmation, expected))
            {
                throw new InvalidOperationException($"Hard-delete confirmation must exactly match '{expected}'.");
            }

            var purgeRunId = Guid.NewGuid().ToString("N");
            using var c = connectionFactory.OpenConnection();

            // BEGIN IMMEDIATE: the purge is a write-heavy operation, so acquire the single WAL writer
            // up front rather than starting deferred (a read that later upgrades to a write). The
            // deferred read-then-upgrade pattern can lose the upgrade race to a concurrent worker write
            // and raise SQLITE_BUSY mid-purge; taking the writer immediately makes the purge atomic
            // against other writers from the first statement.
            using var tx = c.BeginTransaction(deferred: false);
            EnsureHardDeletePreconditions(c, tx, jobId);
            InsertPurgeRun(c, tx, purgeRunId, jobId, actor, reason, "Running");
            var counts = PurgeJob(c, tx, jobId);

            InsertAudit(c, tx, actor, "HardDeleted", "Job", jobId, new
            {
                purgeRunId,
                reason,
                counts.QueueRows,
                counts.AttemptRows,
                counts.PerformanceAttemptRows,
                counts.ScheduledRows,
                counts.LogRows,
                counts.StateRows,
                counts.StateEventRows,
                counts.RepairSliceRows,
                counts.RepairBatchRows,
                counts.SummaryRows,
                counts.LifecycleRows,
                counts.EventRows,
                counts.JobRows
            });
            CompletePurgeRun(c, tx, purgeRunId, new
            {
                jobId,
                counts.JobRows,
                counts.QueueRows,
                counts.StateRows,
                counts.RepairBatchRows,
                counts.PerformanceAttemptRows
            });
            tx.Commit();

            Checkpoint(c);
            return new HardDeleteResult(purgeRunId, counts.JobRows, counts.QueueRows, counts.StateRows, counts.RepairBatchRows);
        }

        public HardDeleteBatchResult HardDeleteBatch(
            IReadOnlyList<HardDeleteBatchItem> requestedItems,
            string confirmation,
            string actor,
            string reason)
        {
            var items = requestedItems
                .Where(item => !string.IsNullOrWhiteSpace(item.JobId))
                .DistinctBy(item => item.JobId, StringComparer.Ordinal)
                .ToArray();
            if (items.Length == 0)
            {
                throw new HardDeleteBatchValidationException(
                [
                    new HardDeleteBatchIssue(string.Empty, "Selection", "Select at least one soft-deleted job.")
                ]);
            }

            var expectedConfirmation = BatchConfirmation(items.Length);
            if (!StringComparer.Ordinal.Equals(confirmation, expectedConfirmation))
            {
                throw new InvalidOperationException($"Bulk hard-delete confirmation must exactly match '{expectedConfirmation}'.");
            }

            var purgeRunId = Guid.NewGuid().ToString("N");
            using var c = connectionFactory.OpenConnection();
            using var tx = c.BeginTransaction(deferred: false);
            var validated = ValidateBatchItems(c, tx, items);
            var issues = validated.SelectMany(item => item.Issues).ToArray();
            if (issues.Length > 0)
            {
                throw new HardDeleteBatchValidationException(issues);
            }

            InsertPurgeRun(c, tx, purgeRunId, jobId: null, actor, reason, "Running");
            var totals = HardDeleteCounts.Empty;
            foreach (var item in validated)
            {
                var counts = PurgeJob(c, tx, item.Item.JobId);
                totals += counts;
                InsertAudit(c, tx, actor, "HardDeleted", "Job", item.Item.JobId, new
                {
                    purgeRunId,
                    reason,
                    bulk = true,
                    counts.QueueRows,
                    counts.AttemptRows,
                    counts.PerformanceAttemptRows,
                    counts.ScheduledRows,
                    counts.LogRows,
                    counts.StateRows,
                    counts.StateEventRows,
                    counts.RepairSliceRows,
                    counts.RepairBatchRows,
                    counts.SummaryRows,
                    counts.LifecycleRows,
                    counts.EventRows,
                    counts.JobRows
                });
            }

            CompletePurgeRun(c, tx, purgeRunId, new
            {
                bulk = true,
                jobIds = validated.Select(item => item.Item.JobId).ToArray(),
                totals.JobRows,
                totals.QueueRows,
                totals.StateRows,
                totals.RepairBatchRows,
                totals.PerformanceAttemptRows
            });
            tx.Commit();

            Checkpoint(c);
            return new HardDeleteBatchResult(purgeRunId, totals.JobRows, totals.QueueRows, totals.StateRows, totals.RepairBatchRows);
        }

        public static string BatchConfirmation(int count) => count == 1 ? "DELETE 1 JOB" : $"DELETE {count} JOBS";

        private static void EnsureHardDeletePreconditions(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string jobId)
        {
            using (var job = SqliteStorage.Command(c, tx, "SELECT is_enabled FROM job_definitions WHERE job_id=$job;"))
            {
                job.Add("$job", jobId);
                var enabled = job.ExecuteScalar();
                if (enabled is null)
                {
                    throw new InvalidOperationException($"Job '{jobId}' does not exist.");
                }

                if (Convert.ToInt32(enabled, System.Globalization.CultureInfo.InvariantCulture) != 0)
                {
                    throw new InvalidOperationException($"Hard-delete requires job '{jobId}' to be disabled first.");
                }
            }

            // The job is confirmed disabled above, so neither the scheduler nor a worker will claim any
            // NEW work for it. Only block on work a worker is *actively executing right now* -- a lease
            // that has not yet expired. Inert Queued retries and expired/abandoned leases will never be
            // processed for a disabled job and are removed by this purge, so they must not block it
            // forever (which previously left a disabled job with a single leftover Queued retry, or a
            // crashed worker's expired lease, permanently un-deletable). The purge runs in one BEGIN
            // IMMEDIATE transaction and a worker's lease completion is guarded -- it no-ops if the slice
            // is already gone -- so purging is safe even if a lease finishes around it.
            var nowUtc = SqliteStorage.Utc(DateTimeOffset.UtcNow);

            var liveLeased = CountActive(c, tx, "work_queue", "job_id=$job AND state='Leased' AND locked_until_utc IS NOT NULL AND locked_until_utc > $now", jobId, nowUtc);
            if (liveLeased > 0)
            {
                throw new InvalidOperationException($"Hard-delete cannot purge job '{jobId}' while {liveLeased} work item(s) are leased by an active worker. Wait for the lease to finish or expire, then retry.");
            }

            var runningSlices = CountActive(c, tx, "current_slice_state", "job_id=$job AND state='Running' AND lease_expires_at_utc IS NOT NULL AND lease_expires_at_utc > $now", jobId, nowUtc);
            if (runningSlices > 0)
            {
                throw new InvalidOperationException($"Hard-delete cannot purge job '{jobId}' while {runningSlices} slice(s) are running on an active worker. Wait for the lease to finish or expire, then retry.");
            }
        }

        private static IReadOnlyList<ValidatedHardDeleteBatchItem> ValidateBatchItems(
            Microsoft.Data.Sqlite.SqliteConnection c,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            IReadOnlyList<HardDeleteBatchItem> items)
        {
            var results = new List<ValidatedHardDeleteBatchItem>(items.Count);
            foreach (var item in items)
            {
                string displayName;
                long actualVersion;
                bool enabled;
                using (var job = SqliteStorage.Command(c, tx, "SELECT display_name,catalog_version,is_enabled FROM job_definitions WHERE job_id=$job;"))
                {
                    job.Add("$job", item.JobId);
                    using var reader = job.ExecuteReader();
                    if (!reader.Read())
                    {
                        results.Add(new ValidatedHardDeleteBatchItem(
                            item,
                            item.JobId,
                            [new HardDeleteBatchIssue(item.JobId, item.JobId, "The job no longer exists.")]));
                        continue;
                    }

                    displayName = reader.GetString(0);
                    actualVersion = reader.GetInt64(1);
                    enabled = reader.GetInt32(2) != 0;
                }

                var issues = new List<HardDeleteBatchIssue>();
                if (actualVersion != item.ExpectedVersion)
                {
                    issues.Add(new HardDeleteBatchIssue(
                        item.JobId,
                        displayName,
                        $"The job changed since it was selected (expected catalog version {item.ExpectedVersion}, found {actualVersion})."));
                }

                if (enabled)
                {
                    issues.Add(new HardDeleteBatchIssue(item.JobId, displayName, "The job is enabled. Soft-delete it again before retrying."));
                }

                using (var lifecycle = SqliteStorage.Command(c, tx, """
                    SELECT event_type
                    FROM job_lifecycle_events
                    WHERE job_id=$job
                    ORDER BY recorded_at_utc DESC, rowid DESC
                    LIMIT 1;
                    """))
                {
                    lifecycle.Add("$job", item.JobId);
                    var latestEvent = lifecycle.ExecuteScalar() as string;
                    if (!StringComparer.Ordinal.Equals(latestEvent, "SoftDeleted"))
                    {
                        issues.Add(new HardDeleteBatchIssue(item.JobId, displayName, "The job is no longer soft-deleted."));
                    }
                }

                var nowUtc = SqliteStorage.Utc(DateTimeOffset.UtcNow);
                var liveLeased = CountActive(c, tx, "work_queue", "job_id=$job AND state='Leased' AND locked_until_utc IS NOT NULL AND locked_until_utc > $now", item.JobId, nowUtc);
                if (liveLeased > 0)
                {
                    issues.Add(new HardDeleteBatchIssue(item.JobId, displayName, $"{liveLeased} work item(s) are leased by an active worker."));
                }

                var runningSlices = CountActive(c, tx, "current_slice_state", "job_id=$job AND state='Running' AND lease_expires_at_utc IS NOT NULL AND lease_expires_at_utc > $now", item.JobId, nowUtc);
                if (runningSlices > 0)
                {
                    issues.Add(new HardDeleteBatchIssue(item.JobId, displayName, $"{runningSlices} slice(s) are running on an active worker."));
                }

                results.Add(new ValidatedHardDeleteBatchItem(item, displayName, issues));
            }

            return results;
        }

        private static HardDeleteCounts PurgeJob(
            Microsoft.Data.Sqlite.SqliteConnection c,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string jobId)
        {
            var repairBatchIds = ReadRepairBatchIds(c, tx, jobId);
            var queueRows = Delete(c, tx, "work_queue", "job_id=$job", jobId);
            var attemptRows = Delete(c, tx, "slice_attempts", "job_id=$job", jobId);
            var performanceAttemptRows = Delete(c, tx, "performance_attempts", "job_id=$job", jobId);
            var scheduledRows = Delete(c, tx, "scheduled_slices", "job_id=$job", jobId);
            var logRows = Delete(c, tx, "operational_logs", "job_id=$job", jobId);
            var stateEventRows = Delete(c, tx, "slice_state_events", "job_id=$job", jobId);
            var stateRows = Delete(c, tx, "current_slice_state", "job_id=$job", jobId);
            var repairSliceRows = Delete(c, tx, "repair_slices", "job_id=$job", jobId);
            var repairBatchRows = DeleteRepairBatches(c, tx, repairBatchIds);
            var summaryRows = Delete(c, tx, "failure_summary_runs", "job_id=$job", jobId);
            var lifecycleRows = Delete(c, tx, "job_lifecycle_events", "job_id=$job", jobId);
            var eventRows = Delete(c, tx, "job_definition_events", "job_id=$job", jobId);
            var jobRows = Delete(c, tx, "job_definitions", "job_id=$job", jobId);
            return new HardDeleteCounts(
                jobRows,
                queueRows,
                attemptRows,
                scheduledRows,
                logRows,
                stateRows,
                stateEventRows,
                repairSliceRows,
                repairBatchRows,
                summaryRows,
                lifecycleRows,
                eventRows,
                performanceAttemptRows);
        }

        private static void Checkpoint(Microsoft.Data.Sqlite.SqliteConnection c)
        {
            using var checkpoint = SqliteStorage.Command(c, null, "PRAGMA wal_checkpoint(PASSIVE);");
            checkpoint.ExecuteNonQuery();
        }

        private void RecordLifecycle(string jobId, string eventType, string actor, string reason, object payload)
        {
            using var c = connectionFactory.OpenConnection();
            using var tx = c.BeginTransaction();
            using (var cmd = SqliteStorage.Command(c, tx, """
                INSERT INTO job_lifecycle_events (lifecycle_event_id,job_id,event_type,reason,payload_json)
                VALUES ($id,$job,$type,$reason,$payload);
                """))
            {
                cmd.Add("$id", Guid.NewGuid().ToString("N"));
                cmd.Add("$job", jobId);
                cmd.Add("$type", eventType);
                cmd.Add("$reason", reason);
                cmd.Add("$payload", JsonSerializer.Serialize(payload));
                cmd.ExecuteNonQuery();
            }

            InsertAudit(c, tx, actor, eventType, "Job", jobId, new { reason });
            tx.Commit();
        }

        // Latest lifecycle event per job, where a job counts as soft-deleted when its newest event is
        // "SoftDeleted". Mirrors the LocalApp LifecycleReadModel projection without referencing the app
        // layer: GetLatestStateRows() is ordered newest-first per job, so the first row wins.
        private HashSet<string> SoftDeletedJobIds()
        {
            var softDeleted = new HashSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in lifecycleReadModel.GetLatestStateRows())
            {
                if (!seen.Add(row.JobId))
                {
                    continue;
                }

                if (StringComparer.Ordinal.Equals(row.EventType, "SoftDeleted"))
                {
                    softDeleted.Add(row.JobId);
                }
            }

            return softDeleted;
        }

        private static IReadOnlyList<string> ReadRepairBatchIds(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string jobId)
        {
            using var cmd = SqliteStorage.Command(c, tx, """
                SELECT repair_batch_id FROM repair_batches WHERE job_id=$job
                UNION
                SELECT DISTINCT repair_batch_id FROM repair_slices WHERE job_id=$job;
                """);
            cmd.Add("$job", jobId);
            using var r = cmd.ExecuteReader();
            var ids = new List<string>();
            while (r.Read()) ids.Add(r.GetString(0));
            return ids;
        }

        private static int DeleteRepairBatches(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, IReadOnlyList<string> ids)
        {
            var deleted = 0;
            foreach (var id in ids)
            {
                using var cmd = SqliteStorage.Command(c, tx, "DELETE FROM repair_batches WHERE repair_batch_id=$id;");
                cmd.Add("$id", id);
                deleted += cmd.ExecuteNonQuery();
            }
            return deleted;
        }

        private static int Delete(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string table, string predicate, string jobId)
        {
            using var cmd = SqliteStorage.Command(c, tx, $"DELETE FROM {table} WHERE {predicate};");
            cmd.Add("$job", jobId);
            return cmd.ExecuteNonQuery();
        }

        private static int CountActive(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string table, string predicate, string jobId, string nowUtc)
        {
            using var cmd = SqliteStorage.Command(c, tx, $"SELECT COUNT(*) FROM {table} WHERE {predicate};");
            cmd.Add("$job", jobId);
            cmd.Add("$now", nowUtc);
            return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void InsertPurgeRun(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string id, string? jobId, string actor, string reason, string status)
        {
            using var cmd = SqliteStorage.Command(c, tx, "INSERT INTO purge_runs (purge_run_id,job_id,reason,status,requested_by,details_json) VALUES ($id,$job,$reason,$status,$by,'{}');");
            cmd.Add("$id", id); cmd.Add("$job", jobId); cmd.Add("$reason", reason); cmd.Add("$status", status); cmd.Add("$by", actor); cmd.ExecuteNonQuery();
        }

        private static void CompletePurgeRun(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string id, object details)
        {
            using var cmd = SqliteStorage.Command(c, tx, "UPDATE purge_runs SET status='Completed', completed_at_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now'), details_json=$details WHERE purge_run_id=$id;");
            cmd.Add("$id", id); cmd.Add("$details", JsonSerializer.Serialize(details)); cmd.ExecuteNonQuery();
        }

        private static void InsertAudit(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string actor, string action, string subjectType, string subjectId, object payload)
        {
            using var cmd = SqliteStorage.Command(c, tx, "INSERT INTO system_audit (audit_id,actor,action,subject_type,subject_id,payload_json) VALUES ($id,$actor,$action,$type,$subject,$payload);");
            cmd.Add("$id", Guid.NewGuid().ToString("N")); cmd.Add("$actor", actor); cmd.Add("$action", action); cmd.Add("$type", subjectType); cmd.Add("$subject", subjectId); cmd.Add("$payload", JsonSerializer.Serialize(payload)); cmd.ExecuteNonQuery();
        }

        private sealed record ValidatedHardDeleteBatchItem(
            HardDeleteBatchItem Item,
            string DisplayName,
            IReadOnlyList<HardDeleteBatchIssue> Issues);

        private sealed record HardDeleteCounts(
            int JobRows,
            int QueueRows,
            int AttemptRows,
            int ScheduledRows,
            int LogRows,
            int StateRows,
            int StateEventRows,
            int RepairSliceRows,
            int RepairBatchRows,
            int SummaryRows,
            int LifecycleRows,
            int EventRows,
            int PerformanceAttemptRows)
        {
            public static HardDeleteCounts Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

            public static HardDeleteCounts operator +(HardDeleteCounts left, HardDeleteCounts right) => new(
                left.JobRows + right.JobRows,
                left.QueueRows + right.QueueRows,
                left.AttemptRows + right.AttemptRows,
                left.ScheduledRows + right.ScheduledRows,
                left.LogRows + right.LogRows,
                left.StateRows + right.StateRows,
                left.StateEventRows + right.StateEventRows,
                left.RepairSliceRows + right.RepairSliceRows,
                left.RepairBatchRows + right.RepairBatchRows,
                left.SummaryRows + right.SummaryRows,
                left.LifecycleRows + right.LifecycleRows,
                left.EventRows + right.EventRows,
                left.PerformanceAttemptRows + right.PerformanceAttemptRows);
        }
    }
}
