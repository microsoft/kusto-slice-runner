using System.Data;
using System.Text.Json;
using Kusto.Data.Common;
using Kusto.Data.Exceptions;
using KoLite.Local.Core.Orchestration;
using KoLite.Local.Core.Scheduling;
using KoLite.Local.Core.Time;
using KoLite.Local.Kusto.Execution;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Orchestration;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.Schema;
using KoLite.Local.Sqlite.State;

namespace KoLite.LocalApp.Tests
{
    public sealed class KustoRetryIntegrationTests : IDisposable
    {
        private const string JobId = "08e40ad051f04fd5ae8902446a28f53a";
        private const string RemoteSchemaMessage =
            "Semantic error: Errors occurred while resolving remote entities. "
            + "Failed to resolve name or pattern 'MycroftContainerSnapshot' in one or more scopes: "
            + "($Cluster='https://sample-query.westeurope.kusto.windows.net/', Database='AzureCP')";
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "kusto-retry-tests", Guid.NewGuid().ToString("N"));
        private readonly KoLiteSqliteConnectionFactory sqlite;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteSliceStateRepository state;
        private readonly SqliteChunkStateRepository chunks;
        private readonly SqliteWorkQueueRepository queue;
        private readonly SqliteOperationalReadModelRepository readModels;
        private readonly ManualClock clock = new(At(5));

        public KustoRetryIntegrationTests()
        {
            Directory.CreateDirectory(testDirectory);
            sqlite = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(Path.Combine(testDirectory, "retry.db")));
            new KoLiteSqliteSchema(sqlite).EnsureSchema();
            catalog = new SqliteJobCatalogRepository(sqlite);
            state = new SqliteSliceStateRepository(sqlite);
            chunks = new SqliteChunkStateRepository(sqlite);
            queue = new SqliteWorkQueueRepository(sqlite);
            readModels = new SqliteOperationalReadModelRepository(sqlite);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Remote_schema_retry_recovers_with_stable_identity_and_respects_pause(bool chunked)
        {
            var job = Schedule(chunked);
            var client = new TestControlCommandClientFactory(attempt =>
                attempt == 1 ? new SemanticException(RemoteSchemaMessage, null) : null);
            var worker = Worker(client);

            var first = await worker.RunOnceAsync();

            Assert.True(first.Executed);
            Assert.False(first.DeadLettered);
            await CompleteSibling(worker, chunked);
            Assert.Equal(DurableSliceStatus.Failed, state.Get(JobId, At(0), At(5)).Status);
            var queued = Assert.Single(queue.List(JobId), item => item.State == DurableWorkQueueState.Queued);
            Assert.Equal(1, queued.Attempts);
            Assert.Equal(At(6), queued.AvailableAtUtc);
            Assert.False((await worker.RunOnceAsync()).ClaimedWork);
            AssertFailureDiagnostics(isRetryable: true, RemoteSchemaMessage);

            var paused = catalog.SetEnabled(JobId, false, job.CatalogVersion);
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.False((await worker.RunOnceAsync()).ClaimedWork);
            Assert.Equal(1, queue.Get(queued.QueueItemId)!.Attempts);
            catalog.SetEnabled(JobId, true, paused.CatalogVersion);

            var second = await worker.RunOnceAsync();

            Assert.True(second.Succeeded);
            Assert.Equal(DurableSliceStatus.Completed, state.Get(JobId, At(0), At(5)).Status);
            var requests = client.Requests.Where(request => request.ChunkId is null or 0).ToArray();
            Assert.Equal(2, requests.Length);
            AssertStableIngestionAcrossAttempts(requests);
            Assert.Equal(2, queue.Get(queued.QueueItemId)!.Attempts);
            Assert.Equal(DurableWorkQueueState.Completed, queue.Get(queued.QueueItemId)!.State);
            Assert.Equal(
                ["FailedRetryable", "Succeeded"],
                readModels.GetSliceAttempts(JobId)
                    .Where(attempt => attempt.ChunkId is null or 0)
                    .OrderBy(attempt => attempt.Attempt)
                    .Select(attempt => attempt.Status).ToArray());
            AssertSiblingUnchanged(client, chunked);
            Assert.False((await worker.RunOnceAsync()).ClaimedWork);
        }

        [Theory]
        [InlineData(false, 2)]
        [InlineData(false, 3)]
        [InlineData(true, 2)]
        [InlineData(true, 3)]
        public async Task Remote_schema_retry_deadletters_at_exact_worker_budget(bool chunked, int maxAttempts)
        {
            Schedule(chunked);
            var client = new TestControlCommandClientFactory(_ => new SemanticException(RemoteSchemaMessage, null));
            var worker = Worker(client, maxAttempts);

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var result = await worker.RunOnceAsync();

                Assert.True(result.Executed);
                Assert.False(result.Succeeded);
                Assert.Equal(attempt == maxAttempts, result.DeadLettered);
                if (attempt == 1)
                {
                    await CompleteSibling(worker, chunked);
                }

                if (attempt < maxAttempts)
                {
                    var retry = Assert.Single(queue.List(JobId), item => item.State == DurableWorkQueueState.Queued);
                    var delay = TimeSpan.FromMinutes(Math.Pow(2, attempt - 1));
                    Assert.Equal(clock.UtcNow + delay, retry.AvailableAtUtc);
                    clock.Advance(delay - TimeSpan.FromTicks(1));
                    Assert.False((await worker.RunOnceAsync()).ClaimedWork);
                    clock.Advance(TimeSpan.FromTicks(1));
                }
            }

            var deadLetter = Assert.Single(queue.List(JobId), item => item.State == DurableWorkQueueState.DeadLettered);
            Assert.Equal(maxAttempts, deadLetter.Attempts);
            Assert.Equal(DurableSliceStatus.DeadLettered, state.Get(JobId, At(0), At(5)).Status);
            var failedRequests = client.Requests.Where(request => request.ChunkId is null or 0).ToArray();
            Assert.Equal(maxAttempts, failedRequests.Length);
            AssertStableIngestionAcrossAttempts(failedRequests);
            var attempts = readModels.GetSliceAttempts(JobId).Where(attempt => attempt.ChunkId is null or 0).ToArray();
            Assert.Equal(maxAttempts, attempts.Length);
            Assert.Single(attempts, attempt => attempt.Status == "DeadLettered");
            Assert.Equal(maxAttempts - 1, attempts.Count(attempt => attempt.Status == "FailedRetryable"));
            AssertFailureDiagnostics(isRetryable: true, RemoteSchemaMessage);
            Assert.Contains(readModels.GetOperationalLogs(JobId), log =>
                log.Message == $"Slice dead-lettered after {maxAttempts} of {maxAttempts} attempts.");
            AssertSiblingUnchanged(client, chunked);

            clock.Advance(TimeSpan.FromMinutes(10));
            Assert.False((await worker.RunOnceAsync()).ClaimedWork);
            Assert.Equal(maxAttempts + (chunked ? 1 : 0), client.Requests.Count);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Local_semantic_failure_still_deadletters_without_retry(bool chunked)
        {
            const string message = "Semantic error: 'LocalMissingTable' could not be resolved.";
            Schedule(chunked);
            var client = new TestControlCommandClientFactory(_ => new SemanticException(message, null));
            var worker = Worker(client);

            var result = await worker.RunOnceAsync();

            Assert.True(result.Executed);
            Assert.True(result.DeadLettered);
            await CompleteSibling(worker, chunked);
            var deadLetter = Assert.Single(queue.List(JobId), item => item.State == DurableWorkQueueState.DeadLettered);
            Assert.Equal(1, deadLetter.Attempts);
            Assert.Equal(DurableSliceStatus.DeadLettered, state.Get(JobId, At(0), At(5)).Status);
            AssertFailureDiagnostics(isRetryable: false, message);
            Assert.Contains(readModels.GetOperationalLogs(JobId), log =>
                log.Message == "Slice dead-lettered without retry because the failure was classified as permanent.");
            AssertSiblingUnchanged(client, chunked);

            clock.Advance(TimeSpan.FromMinutes(10));
            Assert.False((await worker.RunOnceAsync()).ClaimedWork);
            Assert.Single(client.Requests, request => request.ChunkId is null or 0);
        }

        public void Dispose() => TestCleanup.DeleteDirectoryWithRetry(testDirectory);

        private JobCatalogRecord Schedule(bool chunked)
        {
            var job = catalog.Create($$"""
                {
                  "id": "{{JobId}}",
                  "activityId": "retry.integration",
                  "functionName": "TestFunction",
                  "outputTable": "TestOutput",
                  "queryWindowSize": "00:05:00",
                  "delayFromUtcNow": "00:00:00",
                  "maxParallelism": 2,
                  "queryTimeout": "00:01:00",
                  {{(chunked ? "\"chunks\": 2," : string.Empty)}}
                  "startFrom": "2026-01-01T00:00:00Z",
                  "endOn": "2026-01-01T00:05:00Z",
                  "target": { "clusterUri": "https://kolite-example.invalid", "database": "DemoDb" }
                }
                """);
            var scheduler = new SqliteLocalScheduler(catalog, state, queue, readModels, clock, chunkState: chunks);
            Assert.Equal(chunked ? 2 : 1, scheduler.Tick().Enqueued);
            return job;
        }

        private SqliteLocalWorker Worker(TestControlCommandClientFactory client, int maxAttempts = 3) =>
            new(catalog, state, queue, readModels,
                new KustoLocalSliceOutputExecutor(new KustoRequestBuilder(), new KustoSdkExecutor(client)),
                clock, new LocalWorkerOptions(MaxAttempts: maxAttempts, EnforceJobParallelism: true), chunkState: chunks);

        private async Task CompleteSibling(SqliteLocalWorker worker, bool chunked)
        {
            if (chunked)
            {
                Assert.True((await worker.RunOnceAsync()).Succeeded);
                Assert.Equal(DurableSliceStatus.Completed, chunks.Get(
                    SliceExecutionUnit.Chunk(new SliceRange(JobId, At(0), At(5)), 1, 2))!.Status);
            }
        }

        private static void AssertStableIngestionAcrossAttempts(IReadOnlyList<KustoExecutionRequest> requests)
        {
            Assert.NotEmpty(requests);
            Assert.Equal(requests.Count, requests.Select(request => request.ClientRequestId).Distinct(StringComparer.Ordinal).Count());
            var ingestionRequest = requests[0] with { ClientRequestIdOverride = null };
            Assert.All(requests, request =>
            {
                Assert.False(string.IsNullOrWhiteSpace(request.ClientRequestIdOverride));
                Assert.Equal(ingestionRequest, request with { ClientRequestIdOverride = null });
            });
        }

        private void AssertSiblingUnchanged(TestControlCommandClientFactory client, bool chunked)
        {
            if (chunked)
            {
                Assert.Single(client.Requests, request => request.ChunkId == 1);
                var sibling = chunks.Get(SliceExecutionUnit.Chunk(new SliceRange(JobId, At(0), At(5)), 1, 2))!;
                Assert.Equal(DurableSliceStatus.Completed, sibling.Status);
                Assert.Equal(1, sibling.Attempt);
            }
        }

        private void AssertFailureDiagnostics(bool isRetryable, string message)
        {
            var failures = readModels.GetSliceAttempts(JobId).Where(attempt => attempt.ErrorCode is not null).ToArray();
            Assert.NotEmpty(failures);
            Assert.All(failures, attempt =>
            {
                Assert.Equal(nameof(SemanticException), attempt.ErrorCode);
                Assert.Equal(message, attempt.ErrorMessage);
            });

            using var connection = sqlite.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT metrics_json FROM slice_attempts WHERE job_id=$job AND error_code IS NOT NULL;";
            command.Parameters.AddWithValue("$job", JobId);
            using var reader = command.ExecuteReader();
            var count = 0;
            while (reader.Read())
            {
                using var metrics = JsonDocument.Parse(reader.GetString(0));
                Assert.Equal(isRetryable, metrics.RootElement.GetProperty("isRetryable").GetBoolean());
                Assert.Equal(!isRetryable, metrics.RootElement.GetProperty("isPermanent").GetBoolean());
                Assert.Equal(400, metrics.RootElement.GetProperty("kustoFailureCode").GetInt32());
                Assert.Equal("General_BadRequest", metrics.RootElement.GetProperty("kustoFailureSubCode").GetString());
                count++;
            }

            Assert.Equal(failures.Length, count);
        }

        private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        private sealed class TestControlCommandClientFactory : IKustoControlCommandClientFactory
        {
            private readonly Func<int, Exception?> failureForAttempt;
            public List<KustoExecutionRequest> Requests { get; } = [];

            public TestControlCommandClientFactory(Func<int, Exception?> failureForAttempt)
            {
                this.failureForAttempt = failureForAttempt;
            }

            public IKustoControlCommandClient Create(KustoExecutionRequest request)
            {
                var attempt = Requests.Count(previous => previous.ExecutionKey == request.ExecutionKey) + 1;
                Requests.Add(request);
                return new TestControlCommandClient(request.ChunkId is null or 0 ? failureForAttempt(attempt) : null);
            }

            public IKustoControlCommandClient CreateForDatabase(Uri clusterUri, string database) =>
                throw new NotSupportedException("These tests execute slice requests only.");

            public KoLiteKustoConnectionDescriptor Describe(KustoExecutionRequest request) =>
                new(request.ClusterUri.ToString(), request.Database, KoLiteKustoAuthMode.AzureCli, null);
        }

        private sealed class TestControlCommandClient : IKustoControlCommandClient
        {
            private readonly Exception? exception;

            public TestControlCommandClient(Exception? exception)
            {
                this.exception = exception;
            }

            public Task<IDataReader> ExecuteControlCommandAsync(string database, string commandText, ClientRequestProperties properties, CancellationToken cancellationToken) =>
                exception is null
                    ? Task.FromResult<IDataReader>(new DataTable().CreateDataReader())
                    : Task.FromException<IDataReader>(exception);

            public void Dispose()
            {
            }
        }
    }
}
