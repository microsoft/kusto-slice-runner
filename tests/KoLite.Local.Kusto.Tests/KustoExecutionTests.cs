using System.Data;
using System.Text.Json;
using Kusto.Data.Common;
using Kusto.Data.Exceptions;
using KoLite.Local.Core.Orchestration;
using KoLite.Local.Core.Schedules;
using KoLite.Local.Core.Scheduling;
using KoLite.Local.Kusto.Execution;

namespace KoLite.Local.Kusto.Tests
{
    public sealed class KustoExecutionTests
    {
        [Fact]
        public void Request_builder_constructs_safe_command_with_parameters()
        {
            var request = new KustoRequestBuilder().Build(Job(), new SliceRange("job_kusto", At(0), At(5)));

            Assert.Equal("DemoDb", request.Database);
            Assert.Equal("KustoFunction", request.FunctionName);
            Assert.Equal("OutputTable", request.OutputTable);
            Assert.Equal("https://kolite-example.invalid/", request.ClusterUri.ToString());
            Assert.Equal("job_kusto|2026-01-01T00:00:00.0000000Z|2026-01-01T00:05:00.0000000Z", request.SliceKey);
            Assert.Equal("ko-lite:job_kusto|2026-01-01T00:00:00.0000000Z|2026-01-01T00:05:00.0000000Z", request.IdempotencyKey);
            Assert.Contains(".set-or-append OutputTable with (ingestIfNotExists", request.CommandText);
            Assert.Contains("tags = \"[\\\"ingest-by:ko-lite:job_kusto", request.CommandText);
            Assert.Contains("KustoFunction(datetime(2026-01-01T00:00:00.0000000Z), datetime(2026-01-01T00:05:00.0000000Z), dynamic({ \"mode\": \"scalar\", \"limit\": 10 }))", request.CommandText);
            Assert.DoesNotContain("sliceStart", request.CommandText, StringComparison.Ordinal);
            Assert.DoesNotContain("sliceEnd", request.CommandText, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("[]")]
        [InlineData("null")]
        public void Request_builder_omits_empty_job_settings(string settingsJson)
        {
            var request = new KustoRequestBuilder().Build(Job(settingsJson), new SliceRange("job_kusto", At(0), At(5)));

            Assert.Contains("KustoFunction(datetime(2026-01-01T00:00:00.0000000Z), datetime(2026-01-01T00:05:00.0000000Z))", request.CommandText);
            Assert.DoesNotContain("dynamic(", request.CommandText, StringComparison.Ordinal);
        }

        [Fact]
        public void Request_builder_does_not_include_job_description()
        {
            const string sentinel = "DESCRIPTION_MUST_STAY_LOCAL_7E35A9";
            var request = new KustoRequestBuilder().Build(
                Job() with { Description = sentinel },
                new SliceRange("job_kusto", At(0), At(5)));

            Assert.DoesNotContain(sentinel, JsonSerializer.Serialize(request), StringComparison.Ordinal);
        }

        [Fact]
        public void Request_builder_rejects_unsafe_identifiers_and_mismatched_activity()
        {
            var badOutput = Job() with { OutputTable = "OutputTable;drop" };
            Assert.Throws<InvalidOperationException>(() => new KustoRequestBuilder().Build(badOutput, new SliceRange("job_kusto", At(0), At(5))));
            Assert.Throws<InvalidOperationException>(() => new KustoRequestBuilder().Build(Job(), new SliceRange("other_job", At(0), At(5))));
        }

        [Fact]
        public async Task Local_slice_executor_maps_live_kusto_results()
        {
            var live = new RecordingKustoExecutor(new KustoExecutionResult(true, "ko-lite:job_kusto", new Dictionary<string, string>()));
            var executor = new KustoLocalSliceOutputExecutor(new KustoRequestBuilder(), live);

            var result = await executor.ExecuteAsync(Job(), new SliceRange("job_kusto", At(0), At(5)));

            Assert.True(result.Succeeded);
            Assert.Equal("ko-lite:job_kusto", result.OutputReference);
            Assert.Single(live.Requests);
            Assert.Equal("OutputTable", live.Requests[0].OutputTable);
        }

        [Fact]
        public async Task Sdk_executor_dispatches_control_command_with_idempotency_metadata()
        {
            var request = new KustoRequestBuilder().Build(Job(), new SliceRange("job_kusto", At(0), At(5)));
            var factory = new RecordingControlCommandClientFactory();
            var executor = new KustoSdkExecutor(factory);

            var result = await executor.ExecuteAsync(request);

            Assert.True(result.Succeeded);
            Assert.Equal(request.OutputReference, result.OutputReference);
            Assert.Single(factory.Clients);
            Assert.Equal("DemoDb", factory.Clients[0].Database);
            Assert.Equal(request.CommandText, factory.Clients[0].CommandText);
            Assert.Equal(request.ClientRequestId, factory.Clients[0].Properties?.ClientRequestId);
            Assert.Contains("ingestIfNotExists", factory.Clients[0].CommandText, StringComparison.Ordinal);
        }

        [Fact]
        public void Request_builder_reuses_stable_idempotency_metadata_for_replayed_slice()
        {
            var builder = new KustoRequestBuilder();
            var slice = new SliceRange("job_kusto", At(0), At(5));

            var first = builder.Build(Job(), slice);
            var replay = builder.Build(Job(), slice);

            Assert.Equal(first.SliceKey, replay.SliceKey);
            Assert.Equal(first.OperationId, replay.OperationId);
            Assert.Equal(first.IdempotencyKey, replay.IdempotencyKey);
            Assert.Equal(first.IngestByTag, replay.IngestByTag);
            Assert.Equal(first.ClientRequestId, replay.ClientRequestId);
            Assert.Equal(first.CommandText, replay.CommandText);
            Assert.Equal($"output|{first.SliceKey}", first.OperationId);
            Assert.Equal($"ko-lite:{first.SliceKey}", first.IdempotencyKey);
            Assert.Equal($"ingest-by:{first.IdempotencyKey}", first.IngestByTag);
            Assert.Contains($"ingestIfNotExists = \"[\\\"{first.IdempotencyKey}\\\"]\"", first.CommandText, StringComparison.Ordinal);
            Assert.Contains($"tags = \"[\\\"{first.IngestByTag}\\\"]\"", first.CommandText, StringComparison.Ordinal);
        }

        [Theory]
        [MemberData(nameof(DuplicateIngestByExceptions))]
        public async Task Sdk_executor_treats_duplicate_ingest_by_failure_as_success(string exceptionKind, Func<string, Exception> exceptionFactory)
        {
            Assert.False(string.IsNullOrWhiteSpace(exceptionKind));
            var request = new KustoRequestBuilder().Build(Job(), new SliceRange("job_kusto", At(0), At(5)));
            var factory = new ThrowingControlCommandClientFactory(exceptionFactory(request.IdempotencyKey));
            var executor = new KustoSdkExecutor(factory);

            var result = await executor.ExecuteAsync(request);

            Assert.True(result.Succeeded);
            Assert.Equal(request.OutputReference, result.OutputReference);
            Assert.Equal(request.ClientRequestId, result.Metadata["clientRequestId"]);
            Assert.Equal(request.IdempotencyKey, result.Metadata["idempotencyKey"]);
            Assert.Equal("true", result.Metadata["duplicateSuppressed"]);
        }

        public static IEnumerable<object[]> DuplicateIngestByExceptions()
        {
            yield return
            [
                "request",
                (Func<string, Exception>)(idempotencyKey => new KustoRequestException(
                    $"Duplicate ingestIfNotExists key for {idempotencyKey}.",
                    new InvalidOperationException("duplicate ingest-by tag")))
            ];
            yield return
            [
                "service",
                (Func<string, Exception>)(idempotencyKey => new KustoServiceException(
                    $"Duplicate ingest-by tag for {idempotencyKey}.",
                    new InvalidOperationException("duplicate ingest-by tag")))
            ];
        }

        private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        private static JobDefinition Job(string settingsJson = "{ \"mode\": \"scalar\", \"limit\": 10 }")
        {
            using var document = System.Text.Json.JsonDocument.Parse(settingsJson);
            return new JobDefinition
            {
                Id = "job_kusto",
                ActivityId = "job_kusto",
                FunctionName = "KustoFunction",
                OutputTable = "OutputTable",
                QueryWindowSize = TimeSpan.FromMinutes(5),
                DelayFromUtcNow = TimeSpan.Zero,
                MaxParallelism = 1,
                QueryTimeout = TimeSpan.FromMinutes(2),
                StartFrom = At(0),
                Target = new JobTarget { ClusterUri = "https://kolite-example.invalid", Database = "DemoDb" },
                JobSettings = document.RootElement.Clone()
            };
        }

        private sealed class RecordingKustoExecutor : IKustoExecutor
        {
            private readonly Queue<KustoExecutionResult> results;
            public List<KustoExecutionRequest> Requests { get; } = [];

            public RecordingKustoExecutor(params KustoExecutionResult[] results) => this.results = new Queue<KustoExecutionResult>(results);

            public Task<KustoExecutionResult> ExecuteAsync(KustoExecutionRequest request, CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                return Task.FromResult(this.results.Count == 0
                    ? new KustoExecutionResult(false, null, new Dictionary<string, string>(), KustoErrorClassifier.Classify("NoResult", "No test result queued."))
                    : this.results.Dequeue());
            }
        }


        private sealed class RecordingControlCommandClientFactory : IKustoControlCommandClientFactory
        {
            public List<RecordingControlCommandClient> Clients { get; } = [];

            public IKustoControlCommandClient Create(KustoExecutionRequest request)
            {
                var client = new RecordingControlCommandClient();
                Clients.Add(client);
                return client;
            }

            public IKustoControlCommandClient CreateForDatabase(Uri clusterUri, string database)
            {
                var client = new RecordingControlCommandClient();
                Clients.Add(client);
                return client;
            }

            public KoLiteKustoConnectionDescriptor Describe(KustoExecutionRequest request) =>
                new(request.ClusterUri.ToString(), request.Database, KoLiteKustoAuthMode.AzureCli, null);
        }

        private sealed class ThrowingControlCommandClientFactory : IKustoControlCommandClientFactory
        {
            private readonly Exception exception;

            public ThrowingControlCommandClientFactory(Exception exception)
            {
                this.exception = exception;
            }

            public IKustoControlCommandClient Create(KustoExecutionRequest request) => new ThrowingControlCommandClient(exception);

            public IKustoControlCommandClient CreateForDatabase(Uri clusterUri, string database) => new ThrowingControlCommandClient(exception);

            public KoLiteKustoConnectionDescriptor Describe(KustoExecutionRequest request) =>
                new(request.ClusterUri.ToString(), request.Database, KoLiteKustoAuthMode.AzureCli, null);
        }

        private sealed class RecordingControlCommandClient : IKustoControlCommandClient
        {
            public string? Database { get; private set; }
            public string? CommandText { get; private set; }
            public ClientRequestProperties? Properties { get; private set; }

            public Task<IDataReader> ExecuteControlCommandAsync(string database, string commandText, ClientRequestProperties properties, CancellationToken cancellationToken)
            {
                Database = database;
                CommandText = commandText;
                Properties = properties;
                var table = new DataTable();
                table.Columns.Add("ExtentId", typeof(string));
                table.Rows.Add("extent-1");
                return Task.FromResult<IDataReader>(table.CreateDataReader());
            }

            public void Dispose()
            {
            }
        }

        private sealed class ThrowingControlCommandClient : IKustoControlCommandClient
        {
            private readonly Exception exception;

            public ThrowingControlCommandClient(Exception exception)
            {
                this.exception = exception;
            }

            public Task<IDataReader> ExecuteControlCommandAsync(string database, string commandText, ClientRequestProperties properties, CancellationToken cancellationToken)
            {
                throw exception;
            }

            public void Dispose()
            {
            }
        }
    }
}
