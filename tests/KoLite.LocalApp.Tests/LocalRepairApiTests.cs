using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.Schema;
using KoLite.Local.Sqlite.State;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KoLite.LocalApp.Tests
{
    public sealed class LocalRepairApiTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "repair-api-tests", Guid.NewGuid().ToString("N"));
        private readonly string databasePath;
        private readonly KoLiteSqliteConnectionFactory sqlite;
        private readonly WebApplicationFactory<Program> factory;

        public LocalRepairApiTests()
        {
            Directory.CreateDirectory(testDirectory);
            databasePath = Path.Combine(testDirectory, "repair-api.db");
            sqlite = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(databasePath));
            new KoLiteSqliteSchema(sqlite).EnsureSchema();
            factory = CreateFactory();
        }

        [Fact]
        public async Task Preview_lists_only_failed_slices_and_writes_nothing()
        {
            CreateJob("preview.job");
            var state = new SqliteSliceStateRepository(sqlite);
            state.Append("failed", JobId("preview.job"), At(0), At(5), DurableSliceStatus.Failed, expectedVersion: 0, reason: "boom");
            state.Append("dead", JobId("preview.job"), At(5), At(10), DurableSliceStatus.DeadLettered, expectedVersion: 0, reason: "gave up");
            state.Append("done", JobId("preview.job"), At(10), At(15), DurableSliceStatus.Completed, expectedVersion: 0);

            using var client = factory.CreateClient();
            using var response = await client.PostAsJsonAsync(
                $"/api/jobs/{JobId("preview.job")}/repair/preview",
                new { from = At(0), to = At(20) });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = body.RootElement;

            Assert.Equal(2, root.GetProperty("repairableSliceCount").GetInt32());
            Assert.Equal(0, root.GetProperty("blockedSliceCount").GetInt32());
            // The Completed slice and the Missing At(15)-At(20) window are both skipped, not repaired.
            Assert.Equal(2, root.GetProperty("skippedSliceCount").GetInt32());

            var states = root.GetProperty("slices").EnumerateArray()
                .Select(slice => slice.GetProperty("currentState").GetString() ?? string.Empty)
                .ToArray();
            Assert.Equal(["Failed", "DeadLettered"], states);

            // Preview is strictly read-only.
            Assert.Empty(new SqliteWorkQueueRepository(sqlite).List(JobId("preview.job")));
            Assert.Equal(DurableSliceStatus.Failed, state.Get(JobId("preview.job"), At(0), At(5)).Status);
        }

        [Fact]
        public async Task Repair_enqueues_the_previewed_failed_slices()
        {
            CreateJob("repair.job");
            var state = new SqliteSliceStateRepository(sqlite);
            state.Append("failed", JobId("repair.job"), At(0), At(5), DurableSliceStatus.Failed, expectedVersion: 0, reason: "boom");
            state.Append("done", JobId("repair.job"), At(5), At(10), DurableSliceStatus.Completed, expectedVersion: 0);

            using var client = factory.CreateClient();
            using var response = await client.PostAsJsonAsync(
                $"/api/jobs/{JobId("repair.job")}/repair",
                new { from = At(0), to = At(10), reason = "closing a gap", expectedSliceCount = 1 });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = body.RootElement;

            Assert.Equal(1, root.GetProperty("queued").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("repairBatchId").GetString()));
            Assert.Equal("repair.job", root.GetProperty("job").GetProperty("activityId").GetString());

            var work = Assert.Single(new SqliteWorkQueueRepository(sqlite).List(JobId("repair.job")));
            Assert.Equal(At(0), work.SliceStartUtc);
            Assert.Equal(DurableSliceStatus.Queued, state.Get(JobId("repair.job"), At(0), At(5)).Status);
            // The already-completed slice is untouched.
            Assert.Equal(DurableSliceStatus.Completed, state.Get(JobId("repair.job"), At(5), At(10)).Status);
        }

        [Fact]
        public async Task Chunked_repair_requires_exact_preview_token_and_requeues_only_failed_child()
        {
            CreateJob("repair.chunks", chunks: 2);
            var slice = new KoLite.Local.Core.Scheduling.SliceRange(JobId("repair.chunks"), At(0), At(5));
            var chunks = new SqliteChunkStateRepository(sqlite);
            foreach (var child in chunks.EnsureWindow(slice, 2, "test"))
            {
                chunks.MarkQueued($"queued-{child.ChunkId}", child.Execution, actor: "test");
                var lease = chunks.AcquireLease($"lease-{child.ChunkId}", child.Execution, "worker", TimeSpan.FromMinutes(5), At(10))!;
                if (child.ChunkId == 0)
                {
                    chunks.CompleteLease("complete-0", child.Execution, "worker", lease.LeaseToken!, At(11));
                }
                else
                {
                    chunks.DeadLetterLease("dead-1", child.Execution, "worker", lease.LeaseToken!, At(11), "boom", "Permanent");
                }
            }

            using var client = factory.CreateClient();
            using var previewResponse = await client.PostAsJsonAsync(
                $"/api/jobs/{JobId("repair.chunks")}/repair/preview",
                new { from = At(0), to = At(5) });
            using var previewBody = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
            var preview = previewBody.RootElement;

            Assert.Equal(1, preview.GetProperty("repairableSliceCount").GetInt32());
            Assert.Equal(1, preview.GetProperty("repairableExecutionCount").GetInt32());
            Assert.Equal([1], preview.GetProperty("slices").EnumerateArray().Single().GetProperty("chunkIds").EnumerateArray().Select(value => value.GetInt32()).ToArray());

            using var missingToken = await client.PostAsJsonAsync(
                $"/api/jobs/{JobId("repair.chunks")}/repair",
                new { from = At(0), to = At(5), reason = "repair one chunk", expectedSliceCount = 1 });
            Assert.Equal(HttpStatusCode.Conflict, missingToken.StatusCode);

            using var repaired = await client.PostAsJsonAsync(
                $"/api/jobs/{JobId("repair.chunks")}/repair",
                new
                {
                    from = At(0),
                    to = At(5),
                    reason = "repair one chunk",
                    expectedSliceCount = 1,
                    expectedExecutionCount = preview.GetProperty("repairableExecutionCount").GetInt32(),
                    previewToken = preview.GetProperty("previewToken").GetString(),
                });

            Assert.Equal(HttpStatusCode.OK, repaired.StatusCode);
            using var repairedBody = JsonDocument.Parse(await repaired.Content.ReadAsStringAsync());
            var repairedRoot = repairedBody.RootElement;
            var repairedChunk = repairedRoot.GetProperty("chunks").EnumerateArray().Single();
            Assert.Equal(1, repairedChunk.GetProperty("chunkId").GetInt32());
            Assert.Equal(2, repairedChunk.GetProperty("totalChunks").GetInt32());
            Assert.Equal("DeadLettered", repairedChunk.GetProperty("previousState").GetString());
            Assert.False(string.IsNullOrWhiteSpace(repairedChunk.GetProperty("queueItemId").GetString()));
            var work = Assert.Single(new SqliteWorkQueueRepository(sqlite).List(JobId("repair.chunks")));
            Assert.Equal(1, work.ChunkId);
            Assert.Equal(DurableSliceStatus.Completed, chunks.Get(KoLite.Local.Core.Scheduling.SliceExecutionUnit.Chunk(slice, 0, 2))!.Status);
            Assert.Equal(DurableSliceStatus.Queued, chunks.Get(KoLite.Local.Core.Scheduling.SliceExecutionUnit.Chunk(slice, 1, 2))!.Status);

            var batchId = repairedRoot.GetProperty("repairBatchId").GetString();
            using var history = JsonDocument.Parse(await client.GetStringAsync($"/api/diagnostics/repairs?batchId={batchId}"));
            var historyChunk = history.RootElement.GetProperty("repairChunks").EnumerateArray().Single();
            Assert.Equal(1, historyChunk.GetProperty("chunkId").GetInt32());
            Assert.Equal(repairedChunk.GetProperty("queueItemId").GetString(), historyChunk.GetProperty("workItemId").GetString());
        }

        [Fact]
        public async Task Repair_accepts_the_activity_id_as_well_as_the_guid()
        {
            CreateJob("alias.job");
            new SqliteSliceStateRepository(sqlite)
                .Append("failed", JobId("alias.job"), At(0), At(5), DurableSliceStatus.Failed, expectedVersion: 0, reason: "boom");

            using var client = factory.CreateClient();
            using var response = await client.PostAsJsonAsync(
                "/api/jobs/alias.job/repair",
                new { from = At(0), to = At(5), reason = "by activity id", expectedSliceCount = 1 });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Single(new SqliteWorkQueueRepository(sqlite).List(JobId("alias.job")));
        }

        [Fact]
        public async Task Repair_rejects_a_stale_expected_slice_count()
        {
            CreateJob("count.job");
            new SqliteSliceStateRepository(sqlite)
                .Append("failed", JobId("count.job"), At(0), At(5), DurableSliceStatus.Failed, expectedVersion: 0, reason: "boom");

            using var client = factory.CreateClient();
            using var response = await client.PostAsJsonAsync(
                $"/api/jobs/{JobId("count.job")}/repair",
                new { from = At(0), to = At(5), reason = "stale approval", expectedSliceCount = 4 });

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(4, body.RootElement.GetProperty("expectedSliceCount").GetInt32());
            Assert.Equal(1, body.RootElement.GetProperty("actualSliceCount").GetInt32());
            Assert.Empty(new SqliteWorkQueueRepository(sqlite).List(JobId("count.job")));
        }

        [Fact]
        public async Task Chunked_repair_rejects_preview_when_automatic_retry_appears()
        {
            CreateJob("repair.retry-race", chunks: 1);
            var slice = new KoLite.Local.Core.Scheduling.SliceRange(JobId("repair.retry-race"), At(0), At(5));
            var chunks = new SqliteChunkStateRepository(sqlite);
            var child = Assert.Single(chunks.EnsureWindow(slice, 1, "test"));
            chunks.MarkQueued("queued", child.Execution, actor: "test");
            var lease = chunks.AcquireLease("lease", child.Execution, "worker", TimeSpan.FromMinutes(5), At(10))!;
            chunks.DeadLetterLease("dead", child.Execution, "worker", lease.LeaseToken!, At(11), "permanent", "Permanent");

            using var client = factory.CreateClient();
            using var previewResponse = await client.PostAsJsonAsync(
                $"/api/jobs/{slice.JobId}/repair/preview",
                new { from = slice.StartUtc, to = slice.EndUtc });
            using var previewBody = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
            var preview = previewBody.RootElement;
            var existingRetry = new SqliteWorkQueueRepository(sqlite).Enqueue(
                slice.JobId,
                slice.StartUtc,
                slice.EndUtc,
                $"normal|{child.Execution.ExecutionKey}",
                At(12),
                chunkId: 0,
                totalChunks: 1);

            using var repair = await client.PostAsJsonAsync(
                $"/api/jobs/{slice.JobId}/repair",
                new
                {
                    from = slice.StartUtc,
                    to = slice.EndUtc,
                    reason = "stale after retry appeared",
                    expectedSliceCount = preview.GetProperty("repairableSliceCount").GetInt32(),
                    expectedExecutionCount = preview.GetProperty("repairableExecutionCount").GetInt32(),
                    previewToken = preview.GetProperty("previewToken").GetString(),
                });

            Assert.Equal(HttpStatusCode.Conflict, repair.StatusCode);
            var remaining = Assert.Single(new SqliteWorkQueueRepository(sqlite).List(slice.JobId));
            Assert.Equal(existingRetry.QueueItemId, remaining.QueueItemId);
        }

        [Fact]
        public async Task Repair_refuses_a_paused_job_whose_work_would_never_be_claimed()
        {
            CreateJob("paused.job", isPaused: true);
            new SqliteSliceStateRepository(sqlite)
                .Append("failed", JobId("paused.job"), At(0), At(5), DurableSliceStatus.Failed, expectedVersion: 0, reason: "boom");

            using var client = factory.CreateClient();
            using var response = await client.PostAsJsonAsync(
                $"/api/jobs/{JobId("paused.job")}/repair",
                new { from = At(0), to = At(5), reason = "should not run", expectedSliceCount = 1 });

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains("Resume the job first", body.RootElement.GetProperty("error").GetString());
            Assert.Empty(new SqliteWorkQueueRepository(sqlite).List(JobId("paused.job")));
        }

        [Fact]
        public async Task Repair_rejects_a_range_that_is_off_the_slice_grid()
        {
            CreateJob("aligned.job");

            using var client = factory.CreateClient();
            using var response = await client.PostAsJsonAsync(
                $"/api/jobs/{JobId("aligned.job")}/repair/preview",
                new { from = At(2), to = At(7) });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains("align", body.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Repair_requires_a_reason_and_an_expected_slice_count()
        {
            CreateJob("guards.job");

            using var client = factory.CreateClient();

            using var noReason = await client.PostAsJsonAsync(
                $"/api/jobs/{JobId("guards.job")}/repair",
                new { from = At(0), to = At(5), reason = "   ", expectedSliceCount = 0 });
            Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
            using var noReasonBody = JsonDocument.Parse(await noReason.Content.ReadAsStringAsync());
            Assert.Contains("reason", noReasonBody.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);

            using var noCount = await client.PostAsJsonAsync(
                $"/api/jobs/{JobId("guards.job")}/repair",
                new { from = At(0), to = At(5), reason = "no count supplied" });
            Assert.Equal(HttpStatusCode.BadRequest, noCount.StatusCode);
            using var noCountBody = JsonDocument.Parse(await noCount.Content.ReadAsStringAsync());
            Assert.Contains("expectedSliceCount", noCountBody.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Repair_requires_slice_bounds()
        {
            CreateJob("bounds.job");

            using var client = factory.CreateClient();
            using var response = await client.PostAsJsonAsync($"/api/jobs/{JobId("bounds.job")}/repair/preview", new { });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains("'from' and 'to'", body.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Repair_treats_an_offsetless_timestamp_as_utc_rather_than_server_local_time()
        {
            CreateJob("tz.job");
            new SqliteSliceStateRepository(sqlite)
                .Append("failed", JobId("tz.job"), At(0), At(5), DurableSliceStatus.Failed, expectedVersion: 0, reason: "boom");

            using var client = factory.CreateClient();

            // No trailing 'Z'. Bound as a DateTimeOffset this would be read as server-local time and
            // shifted by the machine's UTC offset, silently targeting a different range on any non-UTC
            // host. It must resolve to the same slices as the explicit UTC form.
            using var bare = await client.PostAsJsonAsync(
                $"/api/jobs/{JobId("tz.job")}/repair/preview",
                new { from = "2026-01-01T00:00:00", to = "2026-01-01T00:05:00" });
            Assert.Equal(HttpStatusCode.OK, bare.StatusCode);
            using var bareBody = JsonDocument.Parse(await bare.Content.ReadAsStringAsync());

            Assert.Equal(1, bareBody.RootElement.GetProperty("repairableSliceCount").GetInt32());
            Assert.Equal(At(0), bareBody.RootElement.GetProperty("fromUtc").GetDateTimeOffset());
        }

        [Fact]
        public async Task Repair_rejects_an_unparseable_timestamp()
        {
            CreateJob("badtime.job");

            using var client = factory.CreateClient();
            using var response = await client.PostAsJsonAsync(
                $"/api/jobs/{JobId("badtime.job")}/repair/preview",
                new { from = "not-a-date", to = "2026-01-01T00:05:00Z" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains("ISO-8601", body.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Repair_rejects_a_range_covering_too_many_slices()
        {
            CreateJob("huge.job");

            using var client = factory.CreateClient();
            using var response = await client.PostAsJsonAsync(
                $"/api/jobs/{JobId("huge.job")}/repair/preview",
                new { from = At(0), to = At(5 * 20_000) });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains("narrower range", body.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Repair_returns_404_for_an_unknown_job()
        {
            using var client = factory.CreateClient();
            using var response = await client.PostAsJsonAsync(
                "/api/jobs/does-not-exist/repair/preview",
                new { from = At(0), to = At(5) });

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains("does not exist", body.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Repeated_identical_repair_reuses_the_batch_without_double_queuing()
        {
            CreateJob("idempotent.job");
            new SqliteSliceStateRepository(sqlite)
                .Append("failed", JobId("idempotent.job"), At(0), At(5), DurableSliceStatus.Failed, expectedVersion: 0, reason: "boom");

            using var client = factory.CreateClient();
            var request = new { from = At(0), to = At(5), reason = "retry the same call", expectedSliceCount = 1 };

            using var first = await client.PostAsJsonAsync($"/api/jobs/{JobId("idempotent.job")}/repair", request);
            using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

            // The slice is Queued now, so a naive retry no longer matches - the caller must re-preview.
            using var second = await client.PostAsJsonAsync($"/api/jobs/{JobId("idempotent.job")}/repair", request);
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

            Assert.Single(new SqliteWorkQueueRepository(sqlite).List(JobId("idempotent.job")));
            Assert.False(string.IsNullOrWhiteSpace(firstBody.RootElement.GetProperty("repairBatchId").GetString()));
        }

        [Fact]
        public async Task Repaired_batch_and_audit_row_are_visible_through_diagnostics()
        {
            CreateJob("audit.job");
            new SqliteSliceStateRepository(sqlite)
                .Append("failed", JobId("audit.job"), At(0), At(5), DurableSliceStatus.Failed, expectedVersion: 0, reason: "boom");

            using var client = factory.CreateClient();
            using var repaired = await client.PostAsJsonAsync(
                $"/api/jobs/{JobId("audit.job")}/repair",
                new { from = At(0), to = At(5), reason = "visible in audit", expectedSliceCount = 1 });
            using var repairedBody = JsonDocument.Parse(await repaired.Content.ReadAsStringAsync());
            var batchId = repairedBody.RootElement.GetProperty("repairBatchId").GetString();

            using var repairs = JsonDocument.Parse(await client.GetStringAsync($"/api/diagnostics/repairs?batchId={batchId}"));
            Assert.Equal(batchId, repairs.RootElement.GetProperty("repairBatchId").GetString());
            Assert.Single(repairs.RootElement.GetProperty("repairSlices").EnumerateArray());

            using var audit = JsonDocument.Parse(await client.GetStringAsync("/api/diagnostics/audit?action=RepairEnqueued"));
            var entry = audit.RootElement.GetProperty("audit").EnumerateArray().Single();
            Assert.Equal(batchId, entry.GetProperty("subjectId").GetString());
            Assert.Equal("local-api", entry.GetProperty("actor").GetString());
        }

        private void CreateJob(string activityId, bool isPaused = false, int? chunks = null) =>
            new SqliteJobCatalogRepository(sqlite).Create(Schedule(activityId, isPaused, chunks));

        private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

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

        private static string Schedule(string activityId, bool isPaused, int? chunks) =>
            "{\n" +
            $"  \"id\": \"{JobId(activityId)}\",\n" +
            $"  \"activityId\": \"{activityId}\",\n" +
            "  \"functionName\": \"RepairFunction\",\n" +
            "  \"outputTable\": \"Output\",\n" +
            "  \"queryWindowSize\": \"00:05:00\",\n" +
            "  \"delayFromUtcNow\": \"00:00:00\",\n" +
            "  \"maxParallelism\": 4,\n" +
            "  \"queryTimeout\": \"00:01:00\",\n" +
            (chunks is null ? string.Empty : $"  \"chunks\": {chunks.Value.ToString(CultureInfo.InvariantCulture)},\n") +
            $"  \"isPaused\": {(isPaused ? "true" : "false")},\n" +
            "  \"startFrom\": \"2026-01-01T00:00:00Z\",\n" +
            "  \"target\": { \"clusterUri\": \"https://kolite-example.invalid\", \"database\": \"DemoDb\" }\n" +
            "}";

        public void Dispose()
        {
            factory.Dispose();
            TestCleanup.DeleteDirectoryBestEffort(testDirectory);
        }
    }
}
