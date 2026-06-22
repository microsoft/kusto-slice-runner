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
                "job.console",
                "queue-item-1",
                At(10),
                At(15),
                2,
                "console-worker",
                LocalWorkerProgressStatus.Started,
                At(20));

            sink.RecordStarted(progress);
            sink.RecordFinished(progress with
            {
                Status = LocalWorkerProgressStatus.DeadLettered,
                CompletedAtUtc = At(21),
                ErrorCode = "KustoServiceException",
                ErrorMessage = "Kusto command failed",
                DeadLettered = true
            });

            Assert.Contains(provider.Entries, entry =>
                entry.Level == LogLevel.Information
                && entry.Message.Contains("Job slice started", StringComparison.Ordinal)
                && HasValue(entry, "JobId", "job.console")
                && HasValue(entry, "SliceStartUtc", At(10))
                && HasValue(entry, "SliceEndUtc", At(15))
                && HasValue(entry, "Attempt", 2)
                && !entry.Message.Contains("queue-item-1", StringComparison.Ordinal)
                && !entry.Values.ContainsKey("QueueItemId"));
            Assert.Contains(provider.Entries, entry =>
                entry.Level == LogLevel.Error
                && entry.Message.Contains("Kusto command failed", StringComparison.Ordinal)
                && HasValue(entry, "JobId", "job.console")
                && HasValue(entry, "CompletionStatus", LocalWorkerProgressStatus.DeadLettered)
                && HasValue(entry, "ErrorCode", "KustoServiceException")
                && HasValue(entry, "ErrorMessage", "Kusto command failed"));
        }

        public void Dispose()
        {
            TestCleanup.DeleteDirectoryWithRetry(testDirectory);
        }

        private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        private static bool HasValue(LogEntry entry, string key, object expected) =>
            entry.Values.TryGetValue(key, out var actual) && Equals(actual, expected);

        private sealed record LogEntry(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Values);

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

            public RecordingLogger(RecordingLoggerProvider provider)
            {
                this.provider = provider;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                    : new Dictionary<string, object?>(StringComparer.Ordinal);
                provider.Add(new LogEntry(logLevel, formatter(state, exception), values));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose() { }
        }
    }
}
