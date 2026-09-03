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
            Assert.Equal(request.SliceKey, request.ExecutionKey);
            Assert.Null(request.ChunkId);
            Assert.Null(request.TotalChunks);
            Assert.Equal("ko-lite:job_kusto|2026-01-01T00:00:00.0000000Z|2026-01-01T00:05:00.0000000Z", request.IdempotencyKey);
            Assert.Contains(".set-or-append OutputTable with (ingestIfNotExists", request.CommandText);
            Assert.Contains("tags = \"[\\\"ingest-by:ko-lite:job_kusto", request.CommandText);
            Assert.DoesNotContain("distributed =", request.CommandText, StringComparison.Ordinal);
            Assert.Contains("KustoFunction(datetime(2026-01-01T00:00:00.0000000Z), datetime(2026-01-01T00:05:00.0000000Z), dynamic({ \"mode\": \"scalar\", \"limit\": 10 }))", request.CommandText);
            Assert.DoesNotContain("sliceStart", request.CommandText, StringComparison.Ordinal);
            Assert.DoesNotContain("sliceEnd", request.CommandText, StringComparison.Ordinal);
        }

        [Fact]
        public void Request_builder_adds_distributed_ingestion_only_when_enabled()
        {
            var request = new KustoRequestBuilder().Build(
                Job() with { Distributed = true },
                new SliceRange("job_kusto", At(0), At(5)));

            Assert.Contains(", distributed = true) <|", request.CommandText, StringComparison.Ordinal);
        }

        [Fact]
        public void Request_builder_constructs_distinct_stable_chunk_ingestion_identities()
        {
            var builder = new KustoRequestBuilder();
            var job = Job() with { Chunks = 2 };
            var slice = new SliceRange("job_kusto", At(0), At(5));

            var first = builder.Build(job, SliceExecutionUnit.Chunk(slice, 0, 2));
            var firstReplay = builder.Build(job, SliceExecutionUnit.Chunk(slice, 0, 2));
            var second = builder.Build(job, SliceExecutionUnit.Chunk(slice, 1, 2));

            Assert.Equal($"{slice.ToKey().Value}|chunk|0|2", first.ExecutionKey);
            Assert.Equal(0, first.ChunkId);
            Assert.Equal(2, first.TotalChunks);
            Assert.Equal(first.IdempotencyKey, firstReplay.IdempotencyKey);
            Assert.Equal(first.IngestByTag, firstReplay.IngestByTag);
            Assert.NotEqual(first.IdempotencyKey, second.IdempotencyKey);
            Assert.Equal($"ko-lite:{first.ExecutionKey}", first.IdempotencyKey);
            Assert.Equal($"ingest-by:{first.IdempotencyKey}", first.IngestByTag);
            Assert.Contains($"ingestIfNotExists = \"[\\\"{first.IdempotencyKey}\\\"]\"", first.CommandText, StringComparison.Ordinal);
            Assert.Contains($"tags = \"[\\\"{first.IngestByTag}\\\"]\"", first.CommandText, StringComparison.Ordinal);
            Assert.Contains("KustoFunction(datetime(2026-01-01T00:00:00.0000000Z), datetime(2026-01-01T00:05:00.0000000Z), 0, 2, dynamic({ \"mode\": \"scalar\", \"limit\": 10 }))", first.CommandText);
        }

        [Fact]
        public void Request_builder_requires_chunk_identity_to_match_the_schedule()
        {
            var builder = new KustoRequestBuilder();
            var slice = new SliceRange("job_kusto", At(0), At(5));

            Assert.Throws<InvalidOperationException>(() => builder.Build(Job() with { Chunks = 2 }, slice));
            Assert.Throws<InvalidOperationException>(() => builder.Build(Job(), SliceExecutionUnit.Chunk(slice, 0, 2)));
            Assert.Throws<InvalidOperationException>(() => builder.Build(Job() with { Chunks = 2 }, SliceExecutionUnit.Chunk(slice, 0, 3)));
        }

        [Fact]
        public void Request_builder_places_chunk_arguments_before_job_settings_and_supports_chunks_one()
        {
            var request = new KustoRequestBuilder().Build(
                Job() with { Chunks = 1 },
                SliceExecutionUnit.Chunk(new SliceRange("job_kusto", At(0), At(5)), 0, 1));

            Assert.Contains(", 0, 1, dynamic({ \"mode\": \"scalar\", \"limit\": 10 }))", request.CommandText, StringComparison.Ordinal);
        }

        [Fact]
        public void Request_builder_generates_unique_ingestion_identity_for_all_32_chunks()
        {
            var builder = new KustoRequestBuilder();
            var job = Job() with { Chunks = 32 };
            var slice = new SliceRange("job_kusto", At(0), At(5));

            var requests = Enumerable.Range(0, 32)
                .Select(chunkId => builder.Build(job, SliceExecutionUnit.Chunk(slice, chunkId, 32)))
                .ToArray();

            Assert.Equal(32, requests.Select(request => request.IdempotencyKey).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(32, requests.Select(request => request.IngestByTag).Distinct(StringComparer.Ordinal).Count());
            Assert.All(requests, request => Assert.Equal($"ingest-by:{request.IdempotencyKey}", request.IngestByTag));
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

        // Regression guard for the bug where Kusto low-memory failures were dead-lettered on the
        // first attempt. The old classifier matched the error text against a fixed keyword list;
        // a low-memory condition contains none of those words, so it was treated as permanent even
        // though Kusto itself reports it as retryable ("@permanent": false). This uses the exact
        // exception type the service raises for that failure.
        [Fact]
        public void Classifier_retries_non_permanent_kusto_failures_such_as_low_memory()
        {
            var exception = new KustoServicePartialQueryFailureLowMemoryConditionException(
                "Request is invalid and cannot be processed: Query execution lacks memory resources to complete "
                + "(80DA0007). Partial query failure: Low memory condition (E_LOW_MEMORY_CONDITION). "
                + "(message: bad allocation (E_LOW_MEMORY_CONDITION))",
                new InvalidOperationException("low memory condition"));

            // Guards the SDK assumption the classifier is built on.
            Assert.False(exception.IsPermanent);

            var error = KustoErrorClassifier.Classify(exception);

            Assert.True(error.IsRetryable);
            Assert.False(error.IsPermanent);
            Assert.Equal(nameof(KustoServicePartialQueryFailureLowMemoryConditionException), error.Code);

            // None of the words the previous heuristic looked for appear in this message, so a
            // text-matching classifier would still get this wrong.
            foreach (var keyword in new[] { "timeout", "throttl", "temporar", "transient", "too many requests", "service unavailable" })
            {
                Assert.DoesNotContain(keyword, error.Message, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void Classifier_does_not_retry_permanent_kusto_failures()
        {
            var exception = new KustoRequestException(
                "Semantic error: 'ThisTableDoesNotExist' could not be resolved.",
                new InvalidOperationException("bad request"));

            Assert.True(exception.IsPermanent);

            var error = KustoErrorClassifier.Classify(exception);

            Assert.False(error.IsRetryable);
            Assert.True(error.IsPermanent);
        }

        [Fact]
        public void Classifier_retries_outer_permanent_cross_cluster_failure_with_nested_non_permanent_payload()
        {
            var exception = PermanentCrossClusterFailure("LowMemoryCondition", isPermanent: false, escapePayload: true);

            Assert.True(exception.IsPermanent);

            var error = KustoErrorClassifier.Classify(exception);

            Assert.True(error.IsRetryable);
            Assert.False(error.IsPermanent);
            Assert.Equal(nameof(KustoRequestException), error.Code);
            Assert.Equal(exception.Message, error.Message);
            Assert.Equal(exception.FailureCode, error.FailureCode);
            Assert.Equal(exception.FailureSubCode, error.FailureSubCode);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Classifier_retries_recognized_remote_non_permanent_payload_regardless_of_error_kind(bool escapePayload)
        {
            var exception = PermanentCrossClusterFailure("InternalServiceError", isPermanent: false, escapePayload);

            var error = KustoErrorClassifier.Classify(exception);

            Assert.True(error.IsRetryable);
            Assert.False(error.IsPermanent);
        }

        [Fact]
        public void Classifier_does_not_override_permanent_failure_when_non_permanent_text_is_not_a_remote_error_payload()
        {
            var exception = new KustoRequestException(
                "Semantic error in a string literal containing \"@permanent\": false.",
                new InvalidOperationException("bad request"));

            var error = KustoErrorClassifier.Classify(exception);

            Assert.False(error.IsRetryable);
            Assert.True(error.IsPermanent);
        }

        [Fact]
        public void Classifier_does_not_override_when_remote_payload_is_explicitly_permanent()
        {
            var exception = PermanentCrossClusterFailure("SemanticError", isPermanent: true, escapePayload: true);

            var error = KustoErrorClassifier.Classify(exception);

            Assert.False(error.IsRetryable);
            Assert.True(error.IsPermanent);
        }

        [Fact]
        public void Classifier_ignores_non_permanent_text_nested_inside_remote_error_message()
        {
            var payload = "{\"error\":{\"code\":\"SemanticError\","
                + "\"message\":\"A literal says \\\"@permanent\\\": false.\","
                + "\"@permanent\":true}}";
            var exception = new KustoRequestException(
                "Cross-cluster query failure (From remote cluster: cluster('https://remote.invalid/'), database: remote) "
                + $"=> Request is invalid and cannot be processed: {payload}",
                new InvalidOperationException("bad request"));

            var error = KustoErrorClassifier.Classify(exception);

            Assert.False(error.IsRetryable);
            Assert.True(error.IsPermanent);
        }

        // Without a Kusto exception there is no permanence signal, so the slice stays retryable and
        // is bounded by the worker's MaxAttempts rather than dead-lettering on the first attempt.
        [Fact]
        public void Classifier_retries_failures_reported_without_a_kusto_exception()
        {
            var error = KustoErrorClassifier.Classify("KustoExecutionFailed", "Kusto execution failed without a detailed error.");

            Assert.True(error.IsRetryable);
            Assert.Null(error.IsPermanent);
            Assert.Null(error.FailureCode);
        }

        [Fact]
        public async Task Local_slice_executor_propagates_permanence_from_kusto_failures()
        {
            var live = new RecordingKustoExecutor(new KustoExecutionResult(
                false,
                null,
                new Dictionary<string, string>(),
                new KustoExecutionError("KustoServiceException", "Low memory condition.", IsRetryable: true, IsPermanent: false, FailureCode: 429, FailureSubCode: "General_TooManyRequests")));
            var executor = new KustoLocalSliceOutputExecutor(new KustoRequestBuilder(), live);

            var result = await executor.ExecuteAsync(Job(), new SliceRange("job_kusto", At(0), At(5)));

            Assert.False(result.Succeeded);
            Assert.True(result.IsRetryable);
            Assert.False(result.IsPermanent);
            Assert.Equal(429, result.FailureCode);
            Assert.Equal("General_TooManyRequests", result.FailureSubCode);
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
        public async Task Sdk_executor_returns_retryable_result_for_outer_permanent_remote_non_permanent_payload()
        {
            var request = new KustoRequestBuilder().Build(Job(), new SliceRange("job_kusto", At(0), At(5)));
            var exception = PermanentCrossClusterFailure("LowMemoryCondition", isPermanent: false, escapePayload: true);
            var executor = new KustoSdkExecutor(new ThrowingControlCommandClientFactory(exception));

            var result = await executor.ExecuteAsync(request);

            Assert.False(result.Succeeded);
            Assert.NotNull(result.Error);
            Assert.True(result.Error.IsRetryable);
            Assert.False(result.Error.IsPermanent);
            Assert.Equal(nameof(KustoRequestException), result.Error.Code);
            Assert.Equal(exception.Message, result.Error.Message);
            Assert.Equal(exception.FailureCode, result.Error.FailureCode);
            Assert.Equal(exception.FailureSubCode, result.Error.FailureSubCode);
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

        private static KustoRequestException PermanentCrossClusterFailure(string errorCode, bool isPermanent, bool escapePayload)
        {
            var quote = escapePayload ? "\\\"" : "\"";
            var payload = $"{{{quote}error{quote}:{{{quote}code{quote}:{quote}{errorCode}{quote},{quote}@permanent{quote}:{isPermanent.ToString().ToLowerInvariant()}}}}}";
            var message = "Request is invalid and cannot be processed: "
                + "Cross-cluster query failure (From remote cluster: cluster('https://remote.invalid/'), database: remote) "
                + $"=> Request is invalid and cannot be processed: {payload}";
            return new KustoRequestException(message, new InvalidOperationException("bad request"));
        }

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
