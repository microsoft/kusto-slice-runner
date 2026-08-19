using KoLite.Local.Core.Orchestration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KoLite.LocalApp.Tests
{
    public sealed class LocalWorkerProgressLoggingTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "localapp-progress-logging-tests", Guid.NewGuid().ToString("N"));
        private readonly string databasePath;

        public LocalWorkerProgressLoggingTests()
        {
            Directory.CreateDirectory(testDirectory);
            databasePath = Path.Combine(testDirectory, "progress-logging.db");
        }

        [Fact]
        public void Registered_progress_sink_logs_start_finish_status_and_error_details()
        {
            var provider = new RecordingLoggerProvider();
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:KoLiteSqlite"] = databasePath,
                        ["KoLite:Scheduler:Enabled"] = "false"
                    });
                });
                builder.ConfigureServices(services =>
                {
                    services.AddLogging(logging =>
                    {
                        logging.ClearProviders();
                        logging.AddProvider(provider);
                    });
                    services.PostConfigure<LoggerFilterOptions>(options =>
                    {
                        options.Rules.Clear();
                        options.MinLevel = LogLevel.Trace;
                    });
                });
            });
            using var scope = factory.Services.CreateScope();
            var sink = scope.ServiceProvider.GetRequiredService<ILocalWorkerProgressSink>();
            var progress = new LocalWorkerProgressEvent(
                "ccaa54932f874b3c8c15faf7e52bcc70",
                "queue-item-1",
                At(10),
                At(15),
                2,
                "console-worker",
                LocalWorkerProgressStatus.Started,
                At(20),
                DisplayName: "Daily Console Export");

            sink.RecordStarted(progress);
            sink.RecordFinished(progress with
            {
                Status = LocalWorkerProgressStatus.DeadLettered,
                CompletedAtUtc = At(21),
                ErrorCode = "KustoServiceException",
                ErrorMessage = "Kusto command failed",
                DeadLettered = true
            });

            // The printed message identifies the job by its display name, never the opaque GUID,
            // and never leaks the queue item id. The GUID is retained only as a structured scope
            // property so diagnostics/structured sinks can still correlate by durable id.
            Assert.Contains(provider.Entries, entry =>
                entry.Level == LogLevel.Information
                && entry.Message.Contains("Job slice started", StringComparison.Ordinal)
                && entry.Message.Contains("Daily Console Export", StringComparison.Ordinal)
                && !entry.Message.Contains("ccaa54932f874b3c8c15faf7e52bcc70", StringComparison.Ordinal)
                && HasValue(entry, "JobDisplayName", "Daily Console Export")
                && HasValue(entry, "SliceStartUtc", At(10))
                && HasValue(entry, "SliceEndUtc", At(15))
                && HasValue(entry, "Attempt", 2)
                && !entry.Values.ContainsKey("JobId")
                && !entry.Message.Contains("queue-item-1", StringComparison.Ordinal)
                && !entry.Values.ContainsKey("QueueItemId")
                && HasScopeValue(entry, "JobId", "ccaa54932f874b3c8c15faf7e52bcc70"));
            Assert.Contains(provider.Entries, entry =>
                entry.Level == LogLevel.Error
                && entry.Message.Contains("Kusto command failed", StringComparison.Ordinal)
                && entry.Message.Contains("Daily Console Export", StringComparison.Ordinal)
                && !entry.Message.Contains("ccaa54932f874b3c8c15faf7e52bcc70", StringComparison.Ordinal)
                && HasValue(entry, "JobDisplayName", "Daily Console Export")
                && HasValue(entry, "CompletionStatus", LocalWorkerProgressStatus.DeadLettered)
                && HasValue(entry, "ErrorCode", "KustoServiceException")
                && HasValue(entry, "ErrorMessage", "Kusto command failed")
                && HasScopeValue(entry, "JobId", "ccaa54932f874b3c8c15faf7e52bcc70"));
        }

        [Fact]
        public void Chunked_progress_logs_raw_chunk_identity_in_message_and_scope()
        {
            var provider = new RecordingLoggerProvider();
            using var factory = CreateFactory(provider);
            using var scope = factory.Services.CreateScope();
            var sink = scope.ServiceProvider.GetRequiredService<ILocalWorkerProgressSink>();
            var progress = new LocalWorkerProgressEvent(
                "chunked-job-id",
                "queue-item-chunk-2",
                At(10),
                At(15),
                3,
                "console-worker",
                LocalWorkerProgressStatus.Started,
                At(20),
                DisplayName: "Chunked Export",
                ChunkId: 2,
                TotalChunks: 4);

            sink.RecordStarted(progress);
            sink.RecordFinished(progress with
            {
                Status = LocalWorkerProgressStatus.DeadLettered,
                CompletedAtUtc = At(21),
                ErrorCode = "Permanent",
                ErrorMessage = "chunk failed",
                DeadLettered = true
            });

            Assert.Contains(provider.Entries, entry =>
                entry.Level == LogLevel.Information
                && entry.Message.Contains("chunk 2/4", StringComparison.Ordinal)
                && HasValue(entry, "ChunkId", 2)
                && HasValue(entry, "TotalChunks", 4)
                && HasScopeValue(entry, "ChunkId", 2)
                && HasScopeValue(entry, "TotalChunks", 4));
            Assert.Contains(provider.Entries, entry =>
                entry.Level == LogLevel.Error
                && entry.Message.Contains("chunk 2/4", StringComparison.Ordinal)
                && entry.Message.Contains("chunk failed", StringComparison.Ordinal)
                && HasValue(entry, "ChunkId", 2)
                && HasValue(entry, "TotalChunks", 4));
        }

        public void Dispose()
        {
            TestCleanup.DeleteDirectoryBestEffort(testDirectory);
        }

        private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        private WebApplicationFactory<Program> CreateFactory(RecordingLoggerProvider provider) =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:KoLiteSqlite"] = databasePath,
                        ["KoLite:Scheduler:Enabled"] = "false"
                    });
                });
                builder.ConfigureServices(services =>
                {
                    services.AddLogging(logging =>
                    {
                        logging.ClearProviders();
                        logging.AddProvider(provider);
                    });
                    services.PostConfigure<LoggerFilterOptions>(options =>
                    {
                        options.Rules.Clear();
                        options.MinLevel = LogLevel.Trace;
                    });
                });
            });

        private static bool HasValue(LogEntry entry, string key, object expected) =>
            entry.Values.TryGetValue(key, out var actual) && Equals(actual, expected);

        private static bool HasScopeValue(LogEntry entry, string key, object expected) =>
            entry.Scopes.TryGetValue(key, out var actual) && Equals(actual, expected);

        private sealed record LogEntry(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Values, IReadOnlyDictionary<string, object?> Scopes);

        private sealed class RecordingLoggerProvider : ILoggerProvider
        {
            private readonly object gate = new();
            public List<LogEntry> Entries { get; } = [];

            public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);
            public void Dispose() { }

            public void Add(LogEntry entry)
            {
                lock (gate)
                {
                    Entries.Add(entry);
                }
            }
        }

        private sealed class RecordingLogger : ILogger
        {
            private readonly RecordingLoggerProvider provider;
            private readonly List<object?> activeScopes = [];

            public RecordingLogger(RecordingLoggerProvider provider)
            {
                this.provider = provider;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                activeScopes.Add(state);
                return new ScopeTracker(activeScopes, state);
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                    : new Dictionary<string, object?>(StringComparer.Ordinal);
                var scopes = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var scope in activeScopes)
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> scopePairs)
                    {
                        foreach (var pair in scopePairs) scopes[pair.Key] = pair.Value;
                    }
                }

                provider.Add(new LogEntry(logLevel, formatter(state, exception), values, scopes));
            }
        }

        private sealed class ScopeTracker : IDisposable
        {
            private readonly List<object?> scopes;
            private readonly object? state;

            public ScopeTracker(List<object?> scopes, object? state)
            {
                this.scopes = scopes;
                this.state = state;
            }

            public void Dispose() => scopes.Remove(state);
        }
    }
}
