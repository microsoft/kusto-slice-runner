using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using KoLite.Local.Core.Orchestration;
using KoLite.Local.Core.Time;
using KoLite.Local.Kusto.Execution;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Lifecycle;
using KoLite.Local.Sqlite.Migrations;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Orchestration;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.Rerun;
using KoLite.Local.Sqlite.State;
using KoLite.LocalApp.Api;
using KoLite.LocalApp.Ui;
using KoLite.LocalApp.Updates;
using Microsoft.AspNetCore.Antiforgery;

namespace KoLite.LocalApp
{
    public partial class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddRazorPages();
            builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
            builder.Services.AddSingleton(_ => new KoLiteSqliteConnectionOptions(ResolveDatabasePath(builder.Configuration)));
            builder.Services.AddSingleton<IKoLiteSqliteConnectionFactory, KoLiteSqliteConnectionFactory>();
            builder.Services.AddSingleton<KoLiteSqliteMigrator>();
            builder.Services.AddScoped<SqliteJobCatalogRepository>();
            builder.Services.AddScoped<SqliteOperationalReadModelRepository>();
            builder.Services.AddScoped<SqliteWorkQueueRepository>();
            builder.Services.AddScoped<SqliteSliceStateRepository>();
            builder.Services.AddScoped<SqliteJobLifecycleService>();
            builder.Services.AddScoped<SqliteRerunService>();
            builder.Services.AddScoped<DashboardPageQuery>();
            builder.Services.AddScoped<JobDetailsPageQuery>();
            builder.Services.AddScoped<JobChartQuery>();
            builder.Services.AddScoped<LifecycleReadModel>();
            builder.Services.AddScoped<OperationalDetailsReadModel>();
            builder.Services.AddSingleton<IClock>(SystemClock.Instance);
            builder.Services.AddSingleton<IKustoRequestBuilder, KustoRequestBuilder>();
            builder.Services.AddSingleton(_ => ResolveKustoOptions(builder.Configuration));
            builder.Services.AddScoped<IKustoControlCommandClientFactory, KoLiteKustoConnectionFactory>();
            builder.Services.AddScoped<IKustoExecutor, KustoSdkExecutor>();
            builder.Services.AddScoped<ILocalSliceOutputExecutor, KustoLocalSliceOutputExecutor>();
            builder.Services.AddSingleton<LocalShutdownDrainCoordinator>();
            builder.Services.AddSingleton(new LocalWorkerOptions(WorkerId: "local-web-worker"));
            builder.Services.AddScoped<LocalWorkerFactory>();
            builder.Services.AddScoped(sp => new SqliteLocalScheduler(
                sp.GetRequiredService<SqliteJobCatalogRepository>(),
                sp.GetRequiredService<SqliteSliceStateRepository>(),
                sp.GetRequiredService<SqliteWorkQueueRepository>(),
                sp.GetRequiredService<SqliteOperationalReadModelRepository>(),
                sp.GetRequiredService<IClock>(),
                new LocalSchedulerOptions(MaxSlicesPerTick: 20)));
            builder.Services.AddScoped(sp => sp.GetRequiredService<LocalWorkerFactory>().Create(sp.GetRequiredService<LocalWorkerOptions>().WorkerId));
            builder.Services.AddSingleton<ILocalWorkerProgressSink, LoggingLocalWorkerProgressSink>();
            builder.Services.AddSingleton(sp => LocalBackgroundSchedulerOptions.From(sp.GetRequiredService<IConfiguration>()));
            builder.Services.AddSingleton(sp => LocalBackgroundWorkerPoolOptions.From(
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<LocalBackgroundSchedulerOptions>()));
            builder.Services.AddSingleton<LocalWorkerPoolRuntimeState>();
            builder.Services.AddHostedService<LocalBackgroundSchedulerService>();
            builder.Services.AddHostedService<LocalBackgroundWorkerService>();
            builder.Services.AddSingleton(sp => LocalUpdateCheckOptions.From(sp.GetRequiredService<IConfiguration>()));
            builder.Services.AddSingleton(new AppBuildVersion(BuildInfo.GetCommitSha()));
            builder.Services.AddSingleton(sp => new UpdateCheckRuntimeState(UpdateCheckSnapshot.Initial(
                sp.GetRequiredService<LocalUpdateCheckOptions>().Enabled,
                sp.GetRequiredService<AppBuildVersion>().CommitSha)));
            builder.Services.AddSingleton<IRepositoryUpdateChecker, GhCliRepositoryUpdateChecker>();
            builder.Services.AddScoped<UpdateBadgeReadModel>();
            builder.Services.AddHostedService<LocalUpdateCheckBackgroundService>();
            builder.WebHost.UseUrls(builder.Configuration["KoLite:Urls"] ?? "http://127.0.0.1:5057");

            var app = builder.Build();

            app.Services.GetRequiredService<KoLiteSqliteMigrator>().Migrate();

            app.Use(async (context, next) =>
            {
                try
                {
                    await next(context);
                }
                catch (AntiforgeryValidationException ex) when (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    context.Response.ContentType = "text/html; charset=utf-8";
                    await context.Response.WriteAsync($"""
                    <!doctype html>
                    <html lang="en">
                    <head><meta charset="utf-8"><title>Invalid request</title></head>
                    <body><h1>Invalid request</h1><p>{WebUtility.HtmlEncode(ex.Message)}</p></body>
                    </html>
                    """);
                }
            });

            app.UseStaticFiles();

            app.MapGet("/status/health", (
                KoLiteSqliteConnectionOptions options,
                IKoLiteSqliteConnectionFactory connections,
                SqliteJobCatalogRepository catalog,
                SqliteWorkQueueRepository queue,
                SqliteOperationalReadModelRepository observability,
                LocalBackgroundSchedulerOptions schedulerOptions,
                LocalBackgroundWorkerPoolOptions workerPoolOptions,
                LocalWorkerPoolRuntimeState workerPoolState,
                LocalWorkerOptions localWorkerOptions,
                KoLiteKustoOptions kustoOptions,
                LocalShutdownDrainCoordinator shutdownDrain,
                UpdateCheckRuntimeState updateCheckState,
                LocalUpdateCheckOptions updateCheckOptions,
                IClock clock) =>
            {
                using var connection = connections.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1;";
                command.ExecuteScalar();
                var nowUtc = clock.UtcNow;
                var queueStatus = observability.GetQueueStatus(localWorkerOptions.QueueName, nowUtc);
                var claimableBacklog = queue.CountClaimable(localWorkerOptions.QueueName, nowUtc);
                var updateSnapshot = updateCheckState.GetSnapshot();
                return Results.Json(new
                {
                    status = "Healthy",
                    sqlite = "OK",
                    databasePath = options.DatabasePath,
                    jobCount = catalog.List().Count,
                    liveKustoExecution = "Enabled",
                    kustoAuthMode = kustoOptions.AuthMode.ToString(),
                    scheduler = new
                    {
                        enabled = schedulerOptions.Enabled,
                        tickInterval = schedulerOptions.TickInterval.ToString(),
                        maxWorkerIterations = workerPoolOptions.MaxDispatchStartsPerCycle,
                        workerConcurrency = workerPoolOptions.MaxConcurrency,
                        logEveryPass = schedulerOptions.LogEveryPass
                    },
                    workerPool = workerPoolState.GetSnapshot(workerPoolOptions, queueStatus, claimableBacklog),
                    updateCheck = new
                    {
                        status = updateSnapshot.Status.ToString(),
                        reason = updateSnapshot.Reason.ToString(),
                        enabled = updateCheckOptions.Enabled,
                        repository = updateCheckOptions.Repository,
                        branch = updateCheckOptions.Branch,
                        interval = updateCheckOptions.Interval.ToString(),
                        builtSha = updateSnapshot.BuiltSha,
                        remoteSha = updateSnapshot.RemoteSha,
                        commitsBehind = updateSnapshot.CommitsBehind,
                        commitsAhead = updateSnapshot.CommitsAhead,
                        lastCheckedUtc = updateSnapshot.LastCheckedUtc,
                        error = updateSnapshot.ErrorMessage
                    },
                    shutdown = shutdownDrain.GetSnapshot()
                });
            });

            app.MapGet("/status/shutdown", (LocalShutdownDrainCoordinator shutdownDrain) => Results.Json(shutdownDrain.GetSnapshot()));

            app.MapPost("/status/shutdown/drain", (
                HttpContext context,
                LocalShutdownDrainCoordinator shutdownDrain,
                IHostApplicationLifetime appLifetime,
                IServiceScopeFactory scopes,
                IClock clock,
                ILoggerFactory loggerFactory) =>
            {
                if (!LocalShutdownDrainCoordinator.IsAllowedDrainRemote(context.Connection.RemoteIpAddress))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                var snapshot = shutdownDrain.RequestDrain(clock.UtcNow, context.Request.Query["reason"].ToString());

                // Defer the drain-wait-then-stop work until the HTTP response has been fully
                // sent. When the app is already drained (worker pool disabled or no active work),
                // WaitForDrainedAsync completes immediately, so calling StopApplication() inline
                // can abort Kestrel's in-flight response before it is flushed and surface as
                // "The response ended prematurely." on the client. The start-once guard is taken
                // inside the callback so a dropped response does not permanently consume it.
                context.Response.OnCompleted(() =>
                {
                    if (shutdownDrain.TryStartStopWhenDrained())
                    {
                        var logger = loggerFactory.CreateLogger("LocalShutdownDrain");
                        _ = Task.Run(async () =>
                        {
                            await shutdownDrain.WaitForDrainedAsync().ConfigureAwait(false);
                            var stoppingSnapshot = shutdownDrain.MarkStopping(clock.UtcNow);
                            try
                            {
                                using var scope = scopes.CreateScope();
                                var observability = scope.ServiceProvider.GetRequiredService<SqliteOperationalReadModelRepository>();
                                observability.RecordLog(
                                    "Information",
                                    "Graceful drain completed; stopping KO Lite local app.",
                                    "shutdown-drain",
                                    propertiesJson: JsonSerializer.Serialize(stoppingSnapshot));
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, "Failed to record graceful drain completion before stopping the app.");
                            }

                            logger.LogInformation("Graceful drain completed; stopping KO Lite local app.");
                            appLifetime.StopApplication();
                        });
                    }

                    return Task.CompletedTask;
                });

                return Results.Json(snapshot);
            });

            LocalCatalogApi.Map(app);

            app.MapRazorPages();

            app.Run();
        }

        static string ResolveDatabasePath(IConfiguration configuration)
        {
            var configured = configuration.GetConnectionString("KoLiteSqlite") ?? configuration["KoLite:DatabasePath"];
            if (!string.IsNullOrWhiteSpace(configured)) return configured;
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "KoLite", "ko-lite.db");
        }

        static KoLiteKustoOptions ResolveKustoOptions(IConfiguration configuration)
        {
            var rawMode = configuration["KoLite:Kusto:AuthMode"];
            var authMode = string.IsNullOrWhiteSpace(rawMode)
                ? KoLiteKustoAuthMode.AzureCli
                : Enum.TryParse<KoLiteKustoAuthMode>(rawMode, ignoreCase: true, out var parsed)
                    ? parsed
                    : throw new InvalidOperationException($"Unsupported KoLite:Kusto:AuthMode '{rawMode}'. Supported values: {string.Join(", ", Enum.GetNames<KoLiteKustoAuthMode>())}.");

            return new KoLiteKustoOptions
            {
                AuthMode = authMode,
                ManagedIdentityClientId = configuration["KoLite:Kusto:ManagedIdentityClientId"]
            };
        }
    }

    public sealed record LocalBackgroundSchedulerOptions(bool Enabled, TimeSpan TickInterval, bool LogEveryPass)
    {
        public static TimeSpan DefaultTickInterval { get; } = TimeSpan.FromSeconds(10);

        public static LocalBackgroundSchedulerOptions From(IConfiguration configuration)
        {
            var enabledText = configuration["KoLite:Scheduler:Enabled"];
            var enabled = string.IsNullOrWhiteSpace(enabledText) || bool.Parse(enabledText);
            var configuredTickInterval = configuration["KoLite:Scheduler:TickInterval"];
            var tickInterval = string.IsNullOrWhiteSpace(configuredTickInterval)
                ? DefaultTickInterval
                : TimeSpan.Parse(configuredTickInterval, CultureInfo.InvariantCulture);
            if (tickInterval <= TimeSpan.Zero) throw new InvalidOperationException("KoLite:Scheduler:TickInterval must be greater than zero.");
            var logEveryPassText = configuration["KoLite:Scheduler:LogEveryPass"];
            var logEveryPass = !string.IsNullOrWhiteSpace(logEveryPassText) && bool.Parse(logEveryPassText);
            return new LocalBackgroundSchedulerOptions(enabled, tickInterval, logEveryPass);
        }
    }

    public sealed record LocalBackgroundWorkerPoolOptions(
        bool Enabled,
        string EnabledSource,
        string Mode,
        int MaxConcurrency,
        string MaxConcurrencySource,
        TimeSpan IdleDelay,
        string IdleDelaySource,
        int MaxDispatchStartsPerCycle,
        string MaxDispatchStartsPerCycleSource,
        bool LogEveryPass)
    {
        public const string FixedMode = "Fixed";
        public const int DefaultMaxConcurrency = 10;
        public const int DefaultMaxDispatchStartsPerCycle = 100;
        public static TimeSpan DefaultIdleDelay { get; } = TimeSpan.FromMilliseconds(250);

        public static LocalBackgroundWorkerPoolOptions From(IConfiguration configuration, LocalBackgroundSchedulerOptions schedulerOptions)
        {
            var configuredMode = configuration["KoLite:WorkerPool:Mode"];
            var mode = string.IsNullOrWhiteSpace(configuredMode) ? FixedMode : configuredMode.Trim();
            if (!string.Equals(mode, FixedMode, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unsupported KoLite:WorkerPool:Mode '{mode}'. Supported values: {FixedMode}.");
            }

            var maxConcurrency = ReadPositiveInt(
                configuration,
                "KoLite:WorkerPool:MaxConcurrency",
                "KoLite:Scheduler:WorkerConcurrency",
                DefaultMaxConcurrency,
                out var maxConcurrencySource);
            var idleDelay = ReadPositiveTimeSpan(
                configuration,
                "KoLite:WorkerPool:IdleDelay",
                DefaultIdleDelay,
                out var idleDelaySource);
            var maxDispatchStartsPerCycle = ReadPositiveInt(
                configuration,
                "KoLite:WorkerPool:MaxDispatchStartsPerCycle",
                "KoLite:Scheduler:MaxWorkerIterations",
                DefaultMaxDispatchStartsPerCycle,
                out var maxDispatchStartsPerCycleSource);

            return new LocalBackgroundWorkerPoolOptions(
                schedulerOptions.Enabled,
                "KoLite:Scheduler:Enabled",
                FixedMode,
                maxConcurrency,
                maxConcurrencySource,
                idleDelay,
                idleDelaySource,
                maxDispatchStartsPerCycle,
                maxDispatchStartsPerCycleSource,
                schedulerOptions.LogEveryPass);
        }

        private static int ReadPositiveInt(IConfiguration configuration, string primaryKey, string aliasKey, int defaultValue, out string source)
        {
            var configured = configuration[primaryKey];
            if (!string.IsNullOrWhiteSpace(configured))
            {
                source = primaryKey;
                return ParsePositiveInt(primaryKey, configured);
            }

            configured = configuration[aliasKey];
            if (!string.IsNullOrWhiteSpace(configured))
            {
                source = aliasKey;
                return ParsePositiveInt(aliasKey, configured);
            }

            source = "Default";
            return defaultValue;
        }

        private static int ParsePositiveInt(string key, string value)
        {
            var parsed = int.Parse(value, CultureInfo.InvariantCulture);
            if (parsed <= 0) throw new InvalidOperationException($"{key} must be greater than zero.");
            return parsed;
        }

        private static TimeSpan ReadPositiveTimeSpan(IConfiguration configuration, string key, TimeSpan defaultValue, out string source)
        {
            var configured = configuration[key];
            if (string.IsNullOrWhiteSpace(configured))
            {
                source = "Default";
                return defaultValue;
            }

            var parsed = TimeSpan.Parse(configured, CultureInfo.InvariantCulture);
            if (parsed <= TimeSpan.Zero) throw new InvalidOperationException($"{key} must be greater than zero.");
            source = key;
            return parsed;
        }
    }

    internal sealed record WorkerPoolSnapshot(
        string Mode,
        bool Enabled,
        string EnabledSource,
        int MaxConcurrency,
        string MaxConcurrencySource,
        string IdleDelay,
        string IdleDelaySource,
        int MaxDispatchStartsPerCycle,
        string MaxDispatchStartsPerCycleSource,
        int ActiveWorkerCount,
        int AvailableSlots,
        int ClaimableBacklog,
        int ActiveQueueRows,
        int QueuedQueueRows,
        int LeasedQueueRows,
        int ExpiredLeaseRows,
        bool IsIdle,
        bool IsSaturated,
        long DispatchCycles,
        long IdleCycles,
        long SaturatedCycles,
        long Starts,
        long Succeeded,
        long RetryableFailures,
        long DeadLettered,
        long Faulted,
        IReadOnlyList<string> ActiveWorkerIds,
        DateTimeOffset? LastUpdatedAtUtc);

    internal sealed class LocalWorkerPoolRuntimeState
    {
        private readonly object gate = new();
        private int activeWorkerCount;
        private string[] activeWorkerIds = [];
        private long dispatchCycles;
        private long idleCycles;
        private long saturatedCycles;
        private long starts;
        private long succeeded;
        private long retryableFailures;
        private long deadLettered;
        private long faulted;
        private DateTimeOffset? lastUpdatedAtUtc;

        public void RecordDispatchCycle(int activeWorkers, IReadOnlyList<string> workerIds, int started, bool isIdle, bool isSaturated, DateTimeOffset recordedAtUtc)
        {
            lock (gate)
            {
                activeWorkerCount = activeWorkers;
                activeWorkerIds = workerIds.Order(StringComparer.Ordinal).ToArray();
                starts += started;
                dispatchCycles++;
                if (isIdle) idleCycles++;
                if (isSaturated) saturatedCycles++;
                lastUpdatedAtUtc = recordedAtUtc;
            }
        }

        public void RecordWorkerResult(WorkerPassResult result, DateTimeOffset recordedAtUtc)
        {
            lock (gate)
            {
                if (result.ClaimedWork)
                {
                    if (result.Succeeded) succeeded++;
                    else if (result.DeadLettered) deadLettered++;
                    else retryableFailures++;
                }

                lastUpdatedAtUtc = recordedAtUtc;
            }
        }

        public void RecordWorkerFault(DateTimeOffset recordedAtUtc)
        {
            lock (gate)
            {
                faulted++;
                lastUpdatedAtUtc = recordedAtUtc;
            }
        }

        public WorkerPoolSnapshot GetSnapshot(LocalBackgroundWorkerPoolOptions options, QueueStatusSummary queueStatus, int claimableBacklog)
        {
            lock (gate)
            {
                var activeQueueRows = queueStatus.QueuedCount + queueStatus.LeasedCount;
                var availableSlots = Math.Max(0, options.MaxConcurrency - activeWorkerCount);
                return new WorkerPoolSnapshot(
                    options.Mode,
                    options.Enabled,
                    options.EnabledSource,
                    options.MaxConcurrency,
                    options.MaxConcurrencySource,
                    options.IdleDelay.ToString(),
                    options.IdleDelaySource,
                    options.MaxDispatchStartsPerCycle,
                    options.MaxDispatchStartsPerCycleSource,
                    activeWorkerCount,
                    availableSlots,
                    claimableBacklog,
                    activeQueueRows,
                    queueStatus.QueuedCount,
                    queueStatus.LeasedCount,
                    queueStatus.ExpiredLeaseCount,
                    activeWorkerCount == 0 && claimableBacklog == 0,
                    activeWorkerCount >= options.MaxConcurrency && claimableBacklog > 0,
                    dispatchCycles,
                    idleCycles,
                    saturatedCycles,
                    starts,
                    succeeded,
                    retryableFailures,
                    deadLettered,
                    faulted,
                    activeWorkerIds,
                    lastUpdatedAtUtc);
            }
        }
    }

    internal sealed record WorkerPassResult(bool ClaimedWork, bool Succeeded, bool DeadLettered);

    internal sealed class LocalWorkerFactory
    {
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteSliceStateRepository state;
        private readonly SqliteWorkQueueRepository queue;
        private readonly SqliteOperationalReadModelRepository observability;
        private readonly ILocalSliceOutputExecutor executor;
        private readonly IClock clock;
        private readonly LocalWorkerOptions options;
        private readonly ILocalWorkerProgressSink progressSink;

        public LocalWorkerFactory(
            SqliteJobCatalogRepository catalog,
            SqliteSliceStateRepository state,
            SqliteWorkQueueRepository queue,
            SqliteOperationalReadModelRepository observability,
            ILocalSliceOutputExecutor executor,
            IClock clock,
            LocalWorkerOptions options,
            ILocalWorkerProgressSink progressSink)
        {
            this.catalog = catalog;
            this.state = state;
            this.queue = queue;
            this.observability = observability;
            this.executor = executor;
            this.clock = clock;
            this.options = options;
            this.progressSink = progressSink;
        }

        public SqliteLocalWorker Create(string workerId)
        {
            if (string.IsNullOrWhiteSpace(workerId)) throw new InvalidOperationException("Local worker ID must not be empty.");
            return new SqliteLocalWorker(catalog, state, queue, observability, executor, clock, options with { WorkerId = workerId }, progressSink);
        }
    }

    internal sealed class LoggingLocalWorkerProgressSink : ILocalWorkerProgressSink
    {
        private readonly ILogger<LoggingLocalWorkerProgressSink> logger;

        public LoggingLocalWorkerProgressSink(ILogger<LoggingLocalWorkerProgressSink> logger)
        {
            this.logger = logger;
        }

        public void RecordStarted(LocalWorkerProgressEvent progress)
        {
            logger.LogInformation(
                "Job slice started for activity {ActivityId}: slice {SliceStartUtc:O} to {SliceEndUtc:O}, attempt {Attempt}.",
                progress.ActivityId,
                progress.SliceStartUtc,
                progress.SliceEndUtc,
                progress.Attempt);
        }

        public void RecordFinished(LocalWorkerProgressEvent progress)
        {
            if (progress.Status == LocalWorkerProgressStatus.Succeeded)
            {
                logger.LogInformation(
                    "Job slice finished for activity {ActivityId}: slice {SliceStartUtc:O} to {SliceEndUtc:O}, attempt {Attempt}, status {CompletionStatus}.",
                    progress.ActivityId,
                    progress.SliceStartUtc,
                    progress.SliceEndUtc,
                    progress.Attempt,
                    progress.Status);
                return;
            }

            if (progress.Status == LocalWorkerProgressStatus.DeadLettered)
            {
                logger.LogError(
                    "Job slice finished for activity {ActivityId}: slice {SliceStartUtc:O} to {SliceEndUtc:O}, attempt {Attempt}, status {CompletionStatus}, error {ErrorCode}: {ErrorMessage}.",
                    progress.ActivityId,
                    progress.SliceStartUtc,
                    progress.SliceEndUtc,
                    progress.Attempt,
                    progress.Status,
                    progress.ErrorCode,
                    progress.ErrorMessage);
                return;
            }

            logger.LogWarning(
                "Job slice finished for activity {ActivityId}: slice {SliceStartUtc:O} to {SliceEndUtc:O}, attempt {Attempt}, status {CompletionStatus}, error {ErrorCode}: {ErrorMessage}.",
                progress.ActivityId,
                progress.SliceStartUtc,
                progress.SliceEndUtc,
                progress.Attempt,
                progress.Status,
                progress.ErrorCode,
                progress.ErrorMessage);
        }
    }

    internal sealed class LocalBackgroundSchedulerService : BackgroundService
    {
        private readonly IServiceScopeFactory scopes;
        private readonly LocalBackgroundSchedulerOptions options;
        private readonly LocalShutdownDrainCoordinator shutdownDrain;
        private readonly ILogger<LocalBackgroundSchedulerService> logger;

        public LocalBackgroundSchedulerService(
            IServiceScopeFactory scopes,
            LocalBackgroundSchedulerOptions options,
            LocalShutdownDrainCoordinator shutdownDrain,
            ILogger<LocalBackgroundSchedulerService> logger)
        {
            this.scopes = scopes;
            this.options = options;
            this.shutdownDrain = shutdownDrain;
            this.logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!options.Enabled) return;

            while (!stoppingToken.IsCancellationRequested)
            {
                if (!shutdownDrain.IsDrainRequested)
                {
                    await RunPassAsync(stoppingToken);
                }

                await Task.Delay(options.TickInterval, stoppingToken);
            }
        }

        private async Task RunPassAsync(CancellationToken cancellationToken)
        {
            using var scope = scopes.CreateScope();
            var scheduler = scope.ServiceProvider.GetRequiredService<SqliteLocalScheduler>();
            var observability = scope.ServiceProvider.GetRequiredService<SqliteOperationalReadModelRepository>();
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();
            var passStartedAtUtc = clock.UtcNow;
            var passStartedTimestamp = Stopwatch.GetTimestamp();

            var tick = scheduler.Tick();
            var passDuration = Stopwatch.GetElapsedTime(passStartedTimestamp);
            var passCompletedAtUtc = clock.UtcNow;

            if (tick.Enqueued > 0)
            {
                logger.LogInformation("Local scheduler pass enqueued {Enqueued} slices.", tick.Enqueued);
            }

            if (options.LogEveryPass)
            {
                LogSchedulerPass(observability, passStartedAtUtc, passCompletedAtUtc, passDuration, tick);
            }
        }

        private void LogSchedulerPass(
            SqliteOperationalReadModelRepository observability,
            DateTimeOffset passStartedAtUtc,
            DateTimeOffset passCompletedAtUtc,
            TimeSpan passDuration,
            LocalSchedulerTickResult tick)
        {
            var durationMs = Math.Round(passDuration.TotalMilliseconds, 3);
            var propertiesJson = JsonSerializer.Serialize(new
            {
                passStartedAtUtc,
                passCompletedAtUtc,
                durationMs,
                configuredTickInterval = options.TickInterval.ToString(),
                enqueued = tick.Enqueued,
                dependencyBlocked = tick.DependencyBlocked,
                skippedCompleted = tick.SkippedCompleted,
                skippedMaxParallelism = tick.SkippedMaxParallelism,
                workerDispatchDecoupled = true
            }, SchedulerPassLogJson.Options);

            observability.RecordLog(
                "Information",
                "Scheduler pass completed.",
                "scheduler-pass",
                propertiesJson: propertiesJson);
            logger.LogInformation(
                "Scheduler pass completed in {DurationMs} ms with interval {ConfiguredTickInterval}: enqueued {Enqueued}, dependency blocked {DependencyBlocked}, skipped completed {SkippedCompleted}, skipped max parallelism {SkippedMaxParallelism}.",
                durationMs,
                options.TickInterval,
                tick.Enqueued,
                tick.DependencyBlocked,
                tick.SkippedCompleted,
                tick.SkippedMaxParallelism);
        }
    }

    internal sealed class LocalBackgroundWorkerService : BackgroundService
    {
        private readonly IServiceScopeFactory scopes;
        private readonly LocalBackgroundWorkerPoolOptions options;
        private readonly LocalWorkerPoolRuntimeState workerPoolState;
        private readonly LocalWorkerOptions localWorkerOptions;
        private readonly LocalShutdownDrainCoordinator shutdownDrain;
        private readonly IClock clock;
        private readonly ILogger<LocalBackgroundWorkerService> logger;
        private readonly List<InFlightWorker> inFlightWorkers = [];

        public LocalBackgroundWorkerService(
            IServiceScopeFactory scopes,
            LocalBackgroundWorkerPoolOptions options,
            LocalWorkerPoolRuntimeState workerPoolState,
            LocalWorkerOptions localWorkerOptions,
            LocalShutdownDrainCoordinator shutdownDrain,
            IClock clock,
            ILogger<LocalBackgroundWorkerService> logger)
        {
            this.scopes = scopes;
            this.options = options;
            this.workerPoolState = workerPoolState;
            this.localWorkerOptions = localWorkerOptions;
            this.shutdownDrain = shutdownDrain;
            this.clock = clock;
            this.logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!options.Enabled)
            {
                shutdownDrain.NotifyWorkerDispatcherDrained(clock.UtcNow);
                return;
            }

            using var executionCancellation = new CancellationTokenSource();
            using var stopRegistration = stoppingToken.Register(() =>
            {
                if (!shutdownDrain.IsStoppingAfterDrain)
                {
                    executionCancellation.Cancel();
                }
            });
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await RemoveCompletedWorkersAsync(stoppingToken).ConfigureAwait(false);
                    if (shutdownDrain.IsDrainRequested)
                    {
                        if (inFlightWorkers.Count == 0)
                        {
                            shutdownDrain.NotifyWorkerDispatcherDrained(clock.UtcNow);
                            break;
                        }

                        await WaitForWorkerOrIdleDelayAsync(stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    var availableSlots = Math.Max(0, options.MaxConcurrency - inFlightWorkers.Count);
                    var started = 0;
                    var includeExpiredLeases = inFlightWorkers.Count == 0;
                    var queueSnapshot = ReadQueueSnapshot(includeExpiredLeases);
                    if (availableSlots > 0 && queueSnapshot.ClaimableBacklog > 0)
                    {
                        var workersToStart = Math.Min(Math.Min(availableSlots, options.MaxDispatchStartsPerCycle), queueSnapshot.ClaimableBacklog);
                        foreach (var slotNumber in AvailableWorkerSlots().Take(workersToStart))
                        {
                            var workerId = WorkerIdForSlot(slotNumber);
                            inFlightWorkers.Add(new InFlightWorker(
                                slotNumber,
                                workerId,
                                RunTrackedWorkerOnceAsync(workerId, includeExpiredLeases, executionCancellation.Token)));
                            started++;
                        }

                        if (started > 0)
                        {
                            logger.LogDebug("Local worker dispatcher started {Started} workers with {InFlight} in flight.", started, inFlightWorkers.Count);
                        }
                    }

                    var activeWorkerIds = inFlightWorkers.Select(worker => worker.WorkerId).ToArray();
                    var isIdle = inFlightWorkers.Count == 0 && queueSnapshot.ClaimableBacklog == 0 && started == 0;
                    var isSaturated = inFlightWorkers.Count >= options.MaxConcurrency && queueSnapshot.ClaimableBacklog > started;
                    workerPoolState.RecordDispatchCycle(
                        inFlightWorkers.Count,
                        activeWorkerIds,
                        started,
                        isIdle,
                        isSaturated,
                        clock.UtcNow);
                    if (options.LogEveryPass)
                    {
                        LogWorkerDispatch(queueSnapshot, started, inFlightWorkers.Count, includeExpiredLeases, isIdle, isSaturated, activeWorkerIds);
                    }

                    if (started == 0)
                    {
                        await WaitForWorkerOrIdleDelayAsync(stoppingToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            finally
            {
                await DrainInFlightWorkersAsync().ConfigureAwait(false);
            }
        }

        private WorkerQueueSnapshot ReadQueueSnapshot(bool includeExpiredLeases)
        {
            using var scope = scopes.CreateScope();
            var queue = scope.ServiceProvider.GetRequiredService<SqliteWorkQueueRepository>();
            var observability = scope.ServiceProvider.GetRequiredService<SqliteOperationalReadModelRepository>();
            var scopedClock = scope.ServiceProvider.GetRequiredService<IClock>();
            var nowUtc = scopedClock.UtcNow;
            var claimable = includeExpiredLeases
                ? queue.CountClaimable(localWorkerOptions.QueueName, nowUtc)
                : queue.CountQueuedClaimable(localWorkerOptions.QueueName, nowUtc);
            var queueStatus = observability.GetQueueStatus(localWorkerOptions.QueueName, nowUtc);
            return new WorkerQueueSnapshot(
                claimable,
                queueStatus.QueuedCount + queueStatus.LeasedCount,
                queueStatus.QueuedCount,
                queueStatus.LeasedCount,
                queueStatus.ExpiredLeaseCount);
        }

        private async Task<WorkerPassResult> RunWorkerOnceAsync(string workerId, bool includeExpiredLeases, CancellationToken cancellationToken)
        {
            using var scope = scopes.CreateScope();
            var worker = scope.ServiceProvider.GetRequiredService<LocalWorkerFactory>().Create(workerId);
            var run = await worker.RunOnceAsync(cancellationToken, includeExpiredLeases).ConfigureAwait(false);
            return new WorkerPassResult(run.ClaimedWork, run.Succeeded, run.DeadLettered);
        }

        private async Task<WorkerPassResult> RunTrackedWorkerOnceAsync(string workerId, bool includeExpiredLeases, CancellationToken cancellationToken)
        {
            shutdownDrain.RecordWorkerStarted(clock.UtcNow);
            try
            {
                return await RunWorkerOnceAsync(workerId, includeExpiredLeases, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                shutdownDrain.RecordWorkerCompleted(clock.UtcNow);
            }
        }

        private async Task RemoveCompletedWorkersAsync(CancellationToken cancellationToken)
        {
            for (var i = inFlightWorkers.Count - 1; i >= 0; i--)
            {
                var worker = inFlightWorkers[i];
                if (!worker.Task.IsCompleted)
                {
                    continue;
                }

                inFlightWorkers.RemoveAt(i);
                await ObserveWorkerCompletionAsync(worker, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task WaitForWorkerOrIdleDelayAsync(CancellationToken cancellationToken)
        {
            var delay = Task.Delay(options.IdleDelay, cancellationToken);
            if (inFlightWorkers.Count == 0)
            {
                await delay.ConfigureAwait(false);
                return;
            }

            var completed = await Task.WhenAny(inFlightWorkers.Select(worker => (Task)worker.Task).Append(delay)).ConfigureAwait(false);
            if (ReferenceEquals(completed, delay))
            {
                await delay.ConfigureAwait(false);
                return;
            }

            await RemoveCompletedWorkersAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task DrainInFlightWorkersAsync()
        {
            if (inFlightWorkers.Count == 0)
            {
                return;
            }

            foreach (var worker in inFlightWorkers.ToArray())
            {
                await ObserveWorkerCompletionDuringDrainAsync(worker).ConfigureAwait(false);
            }

            inFlightWorkers.Clear();
            workerPoolState.RecordDispatchCycle(0, [], 0, isIdle: true, isSaturated: false, clock.UtcNow);
        }

        private async Task ObserveWorkerCompletionAsync(InFlightWorker worker, CancellationToken cancellationToken)
        {
            try
            {
                var result = await worker.Task.ConfigureAwait(false);
                workerPoolState.RecordWorkerResult(result, clock.UtcNow);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                workerPoolState.RecordWorkerFault(clock.UtcNow);
                logger.LogError(ex, "Local worker execution failed; the worker dispatcher will continue processing later work.");
            }
        }

        private async Task ObserveWorkerCompletionDuringDrainAsync(InFlightWorker worker)
        {
            try
            {
                var result = await worker.Task.ConfigureAwait(false);
                workerPoolState.RecordWorkerResult(result, clock.UtcNow);
            }
            catch (OperationCanceledException)
            {
                workerPoolState.RecordWorkerFault(clock.UtcNow);
                logger.LogDebug("Local worker dispatcher stopped while in-flight worker {WorkerId} was observing cancellation.", worker.WorkerId);
            }
            catch (Exception ex)
            {
                workerPoolState.RecordWorkerFault(clock.UtcNow);
                logger.LogError(ex, "Local worker dispatcher observed a failed in-flight worker {WorkerId} during shutdown.", worker.WorkerId);
            }
        }

        private IEnumerable<int> AvailableWorkerSlots()
        {
            var activeSlots = inFlightWorkers.Select(worker => worker.SlotNumber).ToHashSet();
            for (var slotNumber = 1; slotNumber <= options.MaxConcurrency; slotNumber++)
            {
                if (!activeSlots.Contains(slotNumber))
                {
                    yield return slotNumber;
                }
            }
        }

        private string WorkerIdForSlot(int slotNumber) => $"{localWorkerOptions.WorkerId}-{slotNumber.ToString(CultureInfo.InvariantCulture)}";

        private void LogWorkerDispatch(WorkerQueueSnapshot queueSnapshot, int started, int inFlight, bool includeExpiredLeases, bool isIdle, bool isSaturated, IReadOnlyList<string> activeWorkerIds)
        {
            using var scope = scopes.CreateScope();
            var observability = scope.ServiceProvider.GetRequiredService<SqliteOperationalReadModelRepository>();
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();
            var propertiesJson = JsonSerializer.Serialize(new
            {
                dispatchRecordedAtUtc = clock.UtcNow,
                mode = options.Mode,
                claimableBeforeDispatch = queueSnapshot.ClaimableBacklog,
                activeQueueRows = queueSnapshot.ActiveQueueRows,
                queuedQueueRows = queueSnapshot.QueuedQueueRows,
                leasedQueueRows = queueSnapshot.LeasedQueueRows,
                expiredLeaseRows = queueSnapshot.ExpiredLeaseRows,
                started,
                inFlight,
                availableSlots = Math.Max(0, options.MaxConcurrency - inFlight),
                includeExpiredLeases,
                workerConcurrency = options.MaxConcurrency,
                maxWorkerIterations = options.MaxDispatchStartsPerCycle,
                maxConcurrency = options.MaxConcurrency,
                maxDispatchStartsPerCycle = options.MaxDispatchStartsPerCycle,
                idleDelay = options.IdleDelay.ToString(),
                isIdle,
                isSaturated,
                activeWorkerIds
            }, SchedulerPassLogJson.Options);

            observability.RecordLog(
                "Information",
                "Worker dispatch cycle started.",
                "worker-dispatch",
                propertiesJson: propertiesJson);
        }

        private sealed record InFlightWorker(int SlotNumber, string WorkerId, Task<WorkerPassResult> Task);

        private sealed record WorkerQueueSnapshot(
            int ClaimableBacklog,
            int ActiveQueueRows,
            int QueuedQueueRows,
            int LeasedQueueRows,
            int ExpiredLeaseRows);
    }


    internal static class SchedulerPassLogJson
    {
        public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };
    }
}
