// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using KoLite.Local.Core.Performance;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Infrastructure;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Performance;
using KoLite.Local.Sqlite.Schema;
using KoLite.Local.Sqlite.State;

namespace KoLite.Local.Sqlite.Tests
{
    internal sealed class PerformanceTestStore : IDisposable
    {
        private readonly string directory = Path.Combine(AppContext.BaseDirectory, "performance-file-tests", Guid.NewGuid().ToString("N"));
        internal const string Cluster = "https://performance-example.invalid";
        internal const string Database = "MetricsDb";

        public PerformanceTestStore()
        {
            Factory = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(Path.Combine(directory, "performance.db")));
            new KoLiteSqliteSchema(Factory).EnsureSchema();
            Catalog = new SqliteJobCatalogRepository(Factory);
            State = new SqliteSliceStateRepository(Factory);
            Observability = new SqliteOperationalReadModelRepository(Factory);
            Repository = new SqlitePerformanceRepository(Factory);
        }

        internal KoLiteSqliteConnectionFactory Factory { get; }
        internal SqliteJobCatalogRepository Catalog { get; }
        internal SqliteSliceStateRepository State { get; }
        internal SqliteOperationalReadModelRepository Observability { get; }
        internal SqlitePerformanceRepository Repository { get; }

        internal static DateTimeOffset At(double minutes) => new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        internal JobCatalogRecord CreateJob(string name = "performance", int? chunks = null, string cluster = Cluster, string database = Database)
        {
            var job = Catalog.Create(Schedule(name, chunks, cluster, database));
            Execute("UPDATE job_definition_events SET recorded_at_utc=$time WHERE job_id=$job;",
                ("$time", SqliteStorage.Utc(At(-60))), ("$job", job.JobId));
            EnsureSlice(job.JobId);
            return job;
        }

        internal void EnsureSlice(string jobId, DateTimeOffset? start = null, DateTimeOffset? end = null)
        {
            var sliceStart = start ?? At(0);
            var sliceEnd = end ?? At(5);
            if (State.Get(jobId, sliceStart, sliceEnd).Status == DurableSliceStatus.Missing)
            {
                State.Append(Guid.NewGuid().ToString("N"), jobId, sliceStart, sliceEnd, DurableSliceStatus.Queued, expectedVersion: 0);
            }
        }

        internal void Capture(
            string id, JobCatalogRecord job, DateTimeOffset? started, DateTimeOffset? completed,
            string status = "Succeeded", int? chunk = null, bool? suppressed = null, string? clientRequestId = null)
        {
            Observability.RecordAttempt(
                id, job.JobId, At(0), At(5), 1, status, "test-worker", started, completed,
                chunkId: chunk, totalChunks: chunk.HasValue ? job.Definition.Chunks : null,
                performanceCapture: new PerformanceAttemptCapture(
                    job.Definition.Target.ClusterUri, job.Definition.Target.Database, job.CatalogVersion,
                    clientRequestId ?? $"KoLite.Local.Output;attempt|{id}", suppressed));
        }

        internal void Legacy(
            string id, JobCatalogRecord job, DateTimeOffset? started, DateTimeOffset? completed,
            string status = "Succeeded", int? chunk = null, int? chunks = null)
        {
            Execute("""
                INSERT INTO slice_attempts
                    (attempt_id,job_id,slice_start_utc,slice_end_utc,attempt,status,started_at_utc,completed_at_utc,chunk_id,total_chunks)
                VALUES ($id,$job,$start,$end,1,$status,$started,$completed,$chunk,$chunks);
                """,
                ("$id", id), ("$job", job.JobId), ("$start", SqliteStorage.Utc(At(0))), ("$end", SqliteStorage.Utc(At(5))),
                ("$status", status), ("$started", started.HasValue ? SqliteStorage.Utc(started.Value) : null),
                ("$completed", completed.HasValue ? SqliteStorage.Utc(completed.Value) : null), ("$chunk", chunk), ("$chunks", chunks));
        }

        internal static Dictionary<string, object?> ArchivedAttempt(
            string id, JobCatalogRecord job, DateTimeOffset? started, DateTimeOffset? completed,
            string status = "Succeeded", int? chunk = null, int? chunks = null) => new()
            {
                ["attempt_id"] = id,
                ["job_id"] = job.JobId,
                ["slice_start_utc"] = SqliteStorage.Utc(At(0)),
                ["slice_end_utc"] = SqliteStorage.Utc(At(5)),
                ["attempt"] = 1,
                ["status"] = status,
                ["started_at_utc"] = started.HasValue ? SqliteStorage.Utc(started.Value) : null,
                ["completed_at_utc"] = completed.HasValue ? SqliteStorage.Utc(completed.Value) : null,
                ["chunk_id"] = chunk,
                ["total_chunks"] = chunks
            };

        internal void Archive(string id, JobCatalogRecord job, object[] attempts, DateTimeOffset? archivedAt = null)
        {
            var timestamp = SqliteStorage.Utc(archivedAt ?? At(50));
            Execute("""
                INSERT INTO rerun_batches (rerun_batch_id,root_job_id,root_start_utc,root_end_utc,reason,status)
                VALUES ($id,$job,$start,$end,'test','Completed');
                INSERT INTO rerun_slices (
                    rerun_slice_id,rerun_batch_id,job_id,slice_start_utc,slice_end_utc,role,status,snapshot_json,reset_at_utc,updated_at_utc)
                VALUES ($id,$id,$job,$start,$end,'Root','Reset',$snapshot,$now,$now);
                """, ("$id", id), ("$job", job.JobId), ("$start", SqliteStorage.Utc(At(0))), ("$end", SqliteStorage.Utc(At(5))),
                ("$snapshot", JsonSerializer.Serialize(new { attempts, counts = new { attemptRows = attempts.Length } })), ("$now", timestamp));
        }

        internal void Reconcile(DateTimeOffset? now = null, int batchSize = 500, bool begin = true)
        {
            var timestamp = now ?? At(60);
            if (begin) Repository.BeginHistoryReconciliation(timestamp);
            for (var batch = 0; batch < 1000; batch++)
            {
                if (Repository.BackfillBatch(timestamp, batchSize)) return;
            }

            throw new InvalidOperationException("The test reconciliation did not finish within 1000 bounded batches.");
        }

        internal static KustoCommandStatistics Statistics(
            PerformancePendingAttempt attempt, double? cpu = 1, double? duration = 2, long? memory = 1_073_741_824,
            Guid? server = null, string state = "Completed", string? error = null) =>
            new(attempt.ClientRequestId, server ?? Guid.NewGuid(),
                attempt.StartedAtUtc, attempt.CompletedAtUtc, state, cpu, duration, memory, error);

        internal long Count(string sql, params (string Name, object? Value)[] values) =>
            Convert.ToInt64(Scalar(sql, values), CultureInfo.InvariantCulture);

        internal object? Scalar(string sql, params (string Name, object? Value)[] values)
        {
            using var connection = Factory.OpenConnection();
            using var command = SqliteStorage.Command(connection, null, sql);
            foreach (var (name, value) in values) command.Add(name, value);
            var result = command.ExecuteScalar();
            return result is DBNull ? null : result;
        }

        internal void Execute(string sql, params (string Name, object? Value)[] values)
        {
            using var connection = Factory.OpenConnection();
            using var command = SqliteStorage.Command(connection, null, sql);
            foreach (var (name, value) in values) command.Add(name, value);
            command.ExecuteNonQuery();
        }

        internal static string Schedule(string name, int? chunks = null, string cluster = Cluster, string database = Database) => $$"""
            {
              "activityId":"{{name}}", "functionName":"PerformanceFunction", "outputTable":"PerformanceOutput",
              "queryWindowSize":"00:05:00", "delayFromUtcNow":"00:00:00", "maxParallelism":32,
              "queryTimeout":"00:01:00", "startFrom":"2026-09-01T00:00:00Z",
              {{(chunks.HasValue ? $"\"chunks\":{chunks.Value}," : string.Empty)}}
              "target":{"clusterUri":"{{cluster}}","database":"{{database}}"}
            }
            """;

        public void Dispose() => TestCleanup.DeleteDirectoryWithRetry(directory);
    }
}
