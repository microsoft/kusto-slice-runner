using System.Globalization;
using System.Net;
using System.Text.Json;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.Schema;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.State;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KoLite.LocalApp.Tests
{
    public sealed class LocalDiagnosticsApiTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "diagnostics-tests", Guid.NewGuid().ToString("N"));
        private readonly string databasePath;
        private readonly KoLiteSqliteConnectionFactory sqlite;
        private readonly WebApplicationFactory<Program> factory;

        public LocalDiagnosticsApiTests()
        {
            Directory.CreateDirectory(testDirectory);
            databasePath = Path.Combine(testDirectory, "diagnostics.db");
            sqlite = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(databasePath));
            new KoLiteSqliteSchema(sqlite).EnsureSchema();
            factory = CreateFactory();
        }

        [Fact]
        public async Task Status_endpoint_reports_state_counts_and_max_parallelism()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("status.job", "StatusFunction", maxParallelism: 4));
            var state = new SqliteSliceStateRepository(sqlite);
            state.AcquireLease("op-run", JobId("status.job"), At(0), At(5), "worker-1", TimeSpan.FromMinutes(22), DateTimeOffset.UtcNow);
            state.Append("op-block", JobId("status.job"), At(5), At(10), DurableSliceStatus.DependencyBlocked, expectedVersion: 0, reason: "blocked");
            var queue = new SqliteWorkQueueRepository(sqlite);
            queue.Enqueue(JobId("status.job"), At(0), At(5), "status-leased", At(0));
            Assert.NotNull(queue.Claim("default", "queue-worker", TimeSpan.FromMinutes(5), At(10)));
            queue.Enqueue(JobId("status.job"), At(5), At(10), "status-queued", At(20));

            using var client = factory.CreateClient();
            using var document = JsonDocument.Parse(await client.GetStringAsync($"/api/jobs/{JobId("status.job")}/status"));
            var root = document.RootElement;

            Assert.Equal(4, root.GetProperty("maxParallelism").GetInt32());
            Assert.Equal("status.job", root.GetProperty("job").GetProperty("activityId").GetString());
            var counts = root.GetProperty("sliceStates");
            Assert.Equal(1, counts.GetProperty("running").GetInt32());
            Assert.Equal(1, counts.GetProperty("dependencyBlocked").GetInt32());
            var queueCounts = root.GetProperty("queue");
            Assert.Equal(1, queueCounts.GetProperty("queued").GetInt32());
            Assert.Equal(1, queueCounts.GetProperty("leased").GetInt32());
            Assert.Equal(2, queueCounts.GetProperty("total").GetInt32());
        }

        [Fact]
        public async Task Queue_endpoint_honors_take_and_prioritizes_active_work()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("queue.job", "QueueFunction"));
            var state = new SqliteSliceStateRepository(sqlite);
            state.Append("queue-active", JobId("queue.job"), At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
            state.Append("queue-older", JobId("queue.job"), At(5), At(10), DurableSliceStatus.Queued, expectedVersion: 0);
            state.Append("queue-newer", JobId("queue.job"), At(10), At(15), DurableSliceStatus.Queued, expectedVersion: 0);
            var queue = new SqliteWorkQueueRepository(sqlite);
            var active = queue.Enqueue(JobId("queue.job"), At(0), At(5), "active", At(100));
            queue.Enqueue(JobId("queue.job"), At(5), At(10), "older-terminal", At(0));
            queue.Enqueue(JobId("queue.job"), At(10), At(15), "newer-terminal", At(0));

            var olderTerminal = queue.Claim("default", "queue-worker", TimeSpan.FromMinutes(5), At(10))!;
            Assert.True(queue.Complete(olderTerminal.QueueItemId, "queue-worker"));
            var newerTerminal = queue.Claim("default", "queue-worker", TimeSpan.FromMinutes(5), At(10))!;
            Assert.True(queue.Complete(newerTerminal.QueueItemId, "queue-worker"));
            Exec(
                "UPDATE work_queue SET updated_at_utc=$updated WHERE queue_item_id=$id;",
                new() { ["$updated"] = Utc(At(10)), ["$id"] = olderTerminal.QueueItemId });
            Exec(
                "UPDATE work_queue SET updated_at_utc=$updated WHERE queue_item_id=$id;",
                new() { ["$updated"] = Utc(At(20)), ["$id"] = newerTerminal.QueueItemId });

            using var client = factory.CreateClient();
            using var document = JsonDocument.Parse(
                await client.GetStringAsync($"/api/jobs/{JobId("queue.job")}/queue?take=2"));
            var rows = document.RootElement.GetProperty("queue").EnumerateArray().ToList();

            Assert.Equal(2, rows.Count);
            Assert.Equal(active.QueueItemId, rows[0].GetProperty("queueItemId").GetString());
            Assert.Equal(newerTerminal.QueueItemId, rows[1].GetProperty("queueItemId").GetString());
        }

        [Fact]
        public async Task Running_slices_endpoint_surfaces_expired_lease_holders_oldest_first()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("stall.job", "StallFunction", maxParallelism: 2));
            var state = new SqliteSliceStateRepository(sqlite);
            var observability = new SqliteOperationalReadModelRepository(sqlite);

            // Two slices left Running with leases that lapsed hours ago, oldest first. This is the
            // fingerprint of the stall: both maxParallelism slots pinned by hung executions.
            var older = DateTimeOffset.UtcNow.AddHours(-9);
            var newer = DateTimeOffset.UtcNow.AddHours(-7);
            state.AcquireLease("op-a", JobId("stall.job"), At(0), At(5), "worker-8", TimeSpan.FromMinutes(22), older);
            state.AcquireLease("op-b", JobId("stall.job"), At(5), At(10), "worker-1", TimeSpan.FromMinutes(22), newer);
            observability.RecordAttempt("att-a", JobId("stall.job"), At(0), At(5), 1, "Started", "worker-8", older, null);

            using var client = factory.CreateClient();
            using var document = JsonDocument.Parse(await client.GetStringAsync("/api/diagnostics/running-slices"));
            var rows = document.RootElement.GetProperty("runningSlices").EnumerateArray().ToList();

            Assert.Equal(2, rows.Count);
            Assert.Equal("worker-8", rows[0].GetProperty("leaseOwner").GetString());
            Assert.True(rows[0].GetProperty("leaseExpired").GetBoolean());
            Assert.True(rows[1].GetProperty("leaseExpired").GetBoolean());
            // Oldest-updated slice floats to the top.
            Assert.True(
                rows[0].GetProperty("updatedAtUtc").GetDateTimeOffset() <= rows[1].GetProperty("updatedAtUtc").GetDateTimeOffset());
        }

        [Fact]
        public async Task History_endpoint_exposes_max_parallelism_change_diff()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("hist.job", "HistFunction", maxParallelism: 2));
            catalog.Import(Schedule("hist.job", "HistFunction", maxParallelism: 8), actor: "test");

            using var client = factory.CreateClient();
            using var document = JsonDocument.Parse(await client.GetStringAsync($"/api/jobs/{JobId("hist.job")}/history"));
            var history = document.RootElement.GetProperty("history").EnumerateArray().ToList();

            var diffRows = history.SelectMany(row => row.GetProperty("diffRows").EnumerateArray());
            var parallelismChange = diffRows.FirstOrDefault(d => d.GetProperty("path").GetString() == "$.maxParallelism");
            Assert.Equal(JsonValueKind.Object, parallelismChange.ValueKind);
            Assert.Equal("2", parallelismChange.GetProperty("previousValue").GetString());
            Assert.Equal("8", parallelismChange.GetProperty("currentValue").GetString());
        }

        [Fact]
        public async Task Slices_endpoint_returns_lease_fields_and_filters_by_state()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("slices.job", "SlicesFunction"));
            var state = new SqliteSliceStateRepository(sqlite);
            state.AcquireLease("op-run", JobId("slices.job"), At(0), At(5), "worker-3", TimeSpan.FromMinutes(22), DateTimeOffset.UtcNow);
            state.Append("op-done", JobId("slices.job"), At(5), At(10), DurableSliceStatus.Completed, expectedVersion: 0);

            using var client = factory.CreateClient();

            using var all = JsonDocument.Parse(await client.GetStringAsync($"/api/jobs/{JobId("slices.job")}/slices"));
            Assert.Equal(2, all.RootElement.GetProperty("slices").GetArrayLength());

            using var running = JsonDocument.Parse(await client.GetStringAsync($"/api/jobs/{JobId("slices.job")}/slices?state=Running"));
            var runningSlices = running.RootElement.GetProperty("slices").EnumerateArray().ToList();
            Assert.Single(runningSlices);
            Assert.Equal("Running", runningSlices[0].GetProperty("state").GetString());
            Assert.Equal("worker-3", runningSlices[0].GetProperty("leaseOwner").GetString());
        }

        [Fact]
        public async Task Chunks_endpoint_returns_child_state_for_one_logical_slice()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("chunks.job", "ChunksFunction", chunks: 2));
            var slice = new KoLite.Local.Core.Scheduling.SliceRange(JobId("chunks.job"), At(0), At(5));
            var chunks = new SqliteChunkStateRepository(sqlite);
            var children = chunks.EnsureWindow(slice, 2, "test");
            chunks.MarkQueued("chunk-queued", children[0].Execution, actor: "test");

            using var client = factory.CreateClient();
            var start = Uri.EscapeDataString(At(0).ToString("O", CultureInfo.InvariantCulture));
            var end = Uri.EscapeDataString(At(5).ToString("O", CultureInfo.InvariantCulture));
            using var document = JsonDocument.Parse(await client.GetStringAsync($"/api/jobs/{JobId("chunks.job")}/chunks?start={start}&end={end}"));

            var rows = document.RootElement.GetProperty("chunks").EnumerateArray().ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal(0, rows[0].GetProperty("chunkId").GetInt32());
            Assert.Equal("Queued", rows[0].GetProperty("status").GetString());
            Assert.Equal(1, rows[1].GetProperty("chunkId").GetInt32());
            Assert.Equal("Missing", rows[1].GetProperty("status").GetString());
            var events = document.RootElement.GetProperty("events").EnumerateArray().ToList();
            Assert.Contains(events, evt =>
                evt.GetProperty("chunkId").GetInt32() == 0
                && evt.GetProperty("status").GetString() == "Queued");
        }

        [Fact]
        public async Task Throughput_endpoint_buckets_succeeded_completions()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("tp.job", "ThroughputFunction"));
            var state = new SqliteSliceStateRepository(sqlite);
            var observability = new SqliteOperationalReadModelRepository(sqlite);
            var now = DateTimeOffset.UtcNow;
            state.Append("op-done", JobId("tp.job"), At(0), At(5), DurableSliceStatus.Completed, expectedVersion: 0);
            observability.RecordAttempt("att-1", JobId("tp.job"), At(0), At(5), 1, "Succeeded", "worker-1", now.AddMinutes(-10), now.AddMinutes(-9));

            using var client = factory.CreateClient();
            using var document = JsonDocument.Parse(await client.GetStringAsync($"/api/jobs/{JobId("tp.job")}/throughput?bucket=30m"));

            var buckets = document.RootElement.GetProperty("buckets").EnumerateArray().ToList();
            Assert.NotEmpty(buckets);
            Assert.Equal(1, buckets.Sum(b => b.GetProperty("succeededCount").GetInt32()));
            Assert.Equal(1, document.RootElement.GetProperty("sample").GetProperty("succeededCount").GetInt32());
        }

        [Fact]
        public async Task Logs_endpoint_filters_by_level()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("logs.job", "LogsFunction"));
            var observability = new SqliteOperationalReadModelRepository(sqlite);
            observability.RecordLog("Information", "Slice dispatched.", "worker", JobId("logs.job"));
            observability.RecordLog("Warning", "Slice failed and was scheduled for retry.", "worker", JobId("logs.job"));

            using var client = factory.CreateClient();

            using var warnings = JsonDocument.Parse(await client.GetStringAsync($"/api/jobs/{JobId("logs.job")}/logs?level=Warning"));
            var rows = warnings.RootElement.GetProperty("logs").EnumerateArray().ToList();
            Assert.Single(rows);
            Assert.Equal("Warning", rows[0].GetProperty("level").GetString());

            using var global = JsonDocument.Parse(await client.GetStringAsync("/api/diagnostics/logs?level=Information"));
            Assert.Contains(
                global.RootElement.GetProperty("logs").EnumerateArray(),
                row => row.GetProperty("message").GetString() == "Slice dispatched.");
        }

        [Fact]
        public async Task Logs_endpoints_include_optional_chunk_identity()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("logs.chunks", "ChunkLogsFunction", chunks: 3));
            var observability = new SqliteOperationalReadModelRepository(sqlite);
            observability.RecordLog(
                "Error",
                "Slice chunk dead-lettered.",
                "worker",
                JobId("logs.chunks"),
                At(0),
                At(5),
                chunkId: 1,
                totalChunks: 3);

            using var client = factory.CreateClient();
            using var jobLogs = JsonDocument.Parse(await client.GetStringAsync($"/api/jobs/{JobId("logs.chunks")}/logs"));
            var jobRow = jobLogs.RootElement.GetProperty("logs").EnumerateArray().Single();
            Assert.Equal(1, jobRow.GetProperty("chunkId").GetInt32());
            Assert.Equal(3, jobRow.GetProperty("totalChunks").GetInt32());

            using var globalLogs = JsonDocument.Parse(await client.GetStringAsync($"/api/diagnostics/logs?jobId={JobId("logs.chunks")}"));
            var globalRow = globalLogs.RootElement.GetProperty("logs").EnumerateArray().Single();
            Assert.Equal(1, globalRow.GetProperty("chunkId").GetInt32());
            Assert.Equal(3, globalRow.GetProperty("totalChunks").GetInt32());
        }

        [Fact]
        public async Task Failures_endpoint_identifies_terminal_failed_chunks()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("failures.chunks", "ChunkFailuresFunction", chunks: 2));
            var slice = new KoLite.Local.Core.Scheduling.SliceRange(JobId("failures.chunks"), At(0), At(5));
            var chunks = new SqliteChunkStateRepository(sqlite);
            foreach (var child in chunks.EnsureWindow(slice, 2, "test"))
            {
                chunks.MarkQueued($"queued-{child.ChunkId}", child.Execution, actor: "test");
                var lease = chunks.AcquireLease($"lease-{child.ChunkId}", child.Execution, "worker", TimeSpan.FromMinutes(5), At(10))!;
                if (child.ChunkId == 0)
                {
                    chunks.DeadLetterLease("dead-0", child.Execution, "worker", lease.LeaseToken!, At(11), "bad partition", "Permanent");
                }
                else
                {
                    chunks.CompleteLease("complete-1", child.Execution, "worker", lease.LeaseToken!, At(11));
                }
            }

            using var client = factory.CreateClient();
            using var document = JsonDocument.Parse(await client.GetStringAsync($"/api/diagnostics/failures?jobId={JobId("failures.chunks")}"));

            var failure = document.RootElement.GetProperty("recentFailures").EnumerateArray().Single();
            Assert.Equal(1, failure.GetProperty("failedChunkCount").GetInt32());
            Assert.Equal([0], failure.GetProperty("failedChunkIds").EnumerateArray().Select(value => value.GetInt32()).ToArray());
        }

        [Fact]
        public async Task Failures_endpoint_applies_job_filter_before_take_limit()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("failures.target", "TargetFailuresFunction"));
            catalog.Create(Schedule("failures.noise", "NoiseFailuresFunction"));
            var state = new SqliteSliceStateRepository(sqlite);
            state.Append("target-failed", JobId("failures.target"), At(0), At(5), DurableSliceStatus.DeadLettered, expectedVersion: 0, reason: "target");
            await Task.Delay(20);
            state.Append("noise-failed", JobId("failures.noise"), At(0), At(5), DurableSliceStatus.DeadLettered, expectedVersion: 0, reason: "noise");

            using var client = factory.CreateClient();
            using var document = JsonDocument.Parse(await client.GetStringAsync($"/api/diagnostics/failures?jobId={JobId("failures.target")}&take=1"));

            var failure = document.RootElement.GetProperty("recentFailures").EnumerateArray().Single();
            Assert.Equal(JobId("failures.target"), failure.GetProperty("jobId").GetString());
        }

        [Fact]
        public async Task Audit_reruns_and_repairs_endpoints_return_seeded_rows()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("audit.job", "AuditFunction"));
            var nowUtc = Utc(DateTimeOffset.UtcNow);
            Exec(
                "INSERT INTO system_audit (audit_id, actor, action, subject_type, subject_id, payload_json, recorded_at_utc) VALUES ($id,$actor,$action,$type,$subject,'{}',$now);",
                new() { ["$id"] = "audit-1", ["$actor"] = "local-web", ["$action"] = "RerunExecuted", ["$type"] = "RerunBatch", ["$subject"] = "batch-1", ["$now"] = nowUtc });
            Exec(
                "INSERT INTO rerun_batches (rerun_batch_id, root_job_id, root_start_utc, root_end_utc, requested_by, reason, status, kusto_cleanup_acknowledged, kusto_cleanup_commands, summary_json, requested_at_utc) VALUES ($id,$job,$s,$e,$by,$reason,'Completed',0,'','{}',$now);",
                new() { ["$id"] = "batch-1", ["$job"] = JobId("audit.job"), ["$s"] = Utc(At(0)), ["$e"] = Utc(At(5)), ["$by"] = "tester", ["$reason"] = "backfill", ["$now"] = nowUtc });
            Exec(
                "INSERT INTO repair_batches (repair_batch_id, job_id, requested_by, reason, status, criteria_json, requested_at_utc) VALUES ($id,$job,$by,$reason,'Completed','{}',$now);",
                new() { ["$id"] = "repair-1", ["$job"] = JobId("audit.job"), ["$by"] = "tester", ["$reason"] = "repair", ["$now"] = nowUtc });

            using var client = factory.CreateClient();

            using var audit = JsonDocument.Parse(await client.GetStringAsync("/api/diagnostics/audit?action=RerunExecuted"));
            Assert.Single(audit.RootElement.GetProperty("audit").EnumerateArray());

            using var reruns = JsonDocument.Parse(await client.GetStringAsync("/api/diagnostics/reruns"));
            Assert.Equal("batch-1", reruns.RootElement.GetProperty("reruns").EnumerateArray().Single().GetProperty("rerunBatchId").GetString());

            using var repairs = JsonDocument.Parse(await client.GetStringAsync("/api/diagnostics/repairs"));
            Assert.Equal("repair-1", repairs.RootElement.GetProperty("repairs").EnumerateArray().Single().GetProperty("repairBatchId").GetString());
        }

        [Fact]
        public async Task Dependencies_endpoint_resolves_declared_dependencies_and_blocked_slices()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("upstream.dep", "UpstreamFunction"));
            catalog.Create(Schedule("downstream.dep", "DownstreamFunction", dependsOnIds: [JobId("upstream.dep")]));
            new SqliteSliceStateRepository(sqlite)
                .Append("op-block", JobId("downstream.dep"), At(0), At(5), DurableSliceStatus.DependencyBlocked, expectedVersion: 0, reason: "blocked");

            using var client = factory.CreateClient();
            using var document = JsonDocument.Parse(await client.GetStringAsync($"/api/jobs/{JobId("downstream.dep")}/dependencies"));
            var root = document.RootElement;

            var dependency = root.GetProperty("dependencies").EnumerateArray().Single();
            Assert.Equal(JobId("upstream.dep"), dependency.GetProperty("upstreamId").GetString());
            Assert.True(dependency.GetProperty("exists").GetBoolean());
            Assert.Equal("upstream.dep", dependency.GetProperty("activityId").GetString());

            Assert.Equal(1, root.GetProperty("blockedSliceCount").GetInt32());
            var blocked = root.GetProperty("blockedSamples").EnumerateArray().Single();
            Assert.False(blocked.GetProperty("isReady").GetBoolean());
            var missing = blocked.GetProperty("missing").EnumerateArray().Single();
            Assert.Equal("upstream.dep", missing.GetProperty("activityId").GetString());
        }

        [Fact]
        public async Task Unknown_job_returns_404()
        {
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/api/jobs/does-not-exist/status");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains("does not exist", body.RootElement.GetProperty("error").GetString());
        }

        private void Exec(string sql, Dictionary<string, object?> parameters)
        {
            using var connection = sqlite.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters)
            {
                var dbParameter = command.CreateParameter();
                dbParameter.ParameterName = parameter.Key;
                dbParameter.Value = parameter.Value ?? DBNull.Value;
                command.Parameters.Add(dbParameter);
            }

            command.ExecuteNonQuery();
        }

        private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        private static string Utc(DateTimeOffset value) =>
            value.ToUniversalTime().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

        private WebApplicationFactory<Program> CreateFactory() =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:KoLiteSqlite"] = databasePath,
                        ["KoLite:Scheduler:Enabled"] = "false",
                        ["KoLite:UpdateCheck:Enabled"] = "false"
                    });
                });
                builder.ConfigureServices(services => services.AddLogging(logging => logging.ClearProviders()));
            });

        private static string JobId(string activityId)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(activityId));
            return new Guid(bytes).ToString("N");
        }

        private static string Schedule(string activityId, string functionName, bool isPaused = false, int maxParallelism = 1, IReadOnlyList<string>? dependsOnIds = null, int? chunks = null)
        {
            var dependsOnLine = dependsOnIds is { Count: > 0 }
                ? "  \"dependsOn\": [" + string.Join(",", dependsOnIds.Select(id => $"{{ \"id\": \"{id}\" }}")) + "],\n"
                : string.Empty;
            return
                "{\n" +
                $"  \"id\": \"{JobId(activityId)}\",\n" +
                $"  \"activityId\": \"{activityId}\",\n" +
                $"  \"functionName\": \"{functionName}\",\n" +
                "  \"outputTable\": \"Output\",\n" +
                "  \"queryWindowSize\": \"00:05:00\",\n" +
                "  \"delayFromUtcNow\": \"00:00:00\",\n" +
                $"  \"maxParallelism\": {maxParallelism.ToString(CultureInfo.InvariantCulture)},\n" +
                "  \"queryTimeout\": \"00:01:00\",\n" +
                (chunks is null ? string.Empty : $"  \"chunks\": {chunks.Value.ToString(CultureInfo.InvariantCulture)},\n") +
                $"  \"isPaused\": {(isPaused ? "true" : "false")},\n" +
                "  \"startFrom\": \"2026-01-01T00:00:00Z\",\n" +
                dependsOnLine +
                "  \"target\": { \"clusterUri\": \"https://kolite-example.invalid\", \"database\": \"DemoDb\" }\n" +
                "}";
        }

        public void Dispose()
        {
            factory.Dispose();
            TestCleanup.DeleteDirectoryBestEffort(testDirectory);
        }
    }
}
