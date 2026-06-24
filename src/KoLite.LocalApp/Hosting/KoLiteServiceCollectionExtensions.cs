using System.Globalization;
using KoLite.Local.Core.Orchestration;
using KoLite.Local.Core.Throttling;
using KoLite.Local.Core.Time;
using KoLite.Local.Kusto.Execution;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Lifecycle;
using KoLite.Local.Sqlite.Migrations;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Orchestration;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.Repair;
using KoLite.Local.Sqlite.Rerun;
using KoLite.Local.Sqlite.State;
using KoLite.Local.Sqlite.Throttling;
using KoLite.LocalApp.Ui;
using KoLite.LocalApp.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KoLite.LocalApp
{
    public static class KoLiteServiceCollectionExtensions
    {
        public static IServiceCollection AddKoLiteServices(this IServiceCollection services, IConfiguration configuration)
        {
            AddPersistence(services, configuration);
            AddReadModels(services);
            AddExecution(services, configuration);
            AddWorkerHost(services);
            AddUpdates(services);
            return services;
        }

        private static void AddPersistence(IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton(_ => new KoLiteSqliteConnectionOptions(ResolveDatabasePath(configuration)));
            services.AddSingleton<IKoLiteSqliteConnectionFactory, KoLiteSqliteConnectionFactory>();
            services.AddSingleton<KoLiteSqliteMigrator>();
            services.AddScoped<SqliteJobCatalogRepository>();
            services.AddScoped<SqliteOperationalReadModelRepository>();
            services.AddScoped<SqliteLifecycleReadModelRepository>();
            services.AddScoped<SqliteDiagnosticsReadModelRepository>();
            services.AddScoped<SqliteWorkQueueRepository>();
            services.AddScoped<SqliteSliceStateRepository>();
            services.AddScoped<SqliteJobLifecycleService>();
            services.AddSingleton<SqliteIngestionThrottleRepository>();
            services.AddSingleton<IngestionThrottleObserver>();
            services.AddSingleton(_ => ResolveThrottleAdvisorOptions(configuration));
            services.AddScoped<SqliteThrottleAdvisorReadModel>();
            services.AddScoped<SqliteRerunService>();
            services.AddScoped<SqliteRepairService>();
        }

        private static void AddReadModels(IServiceCollection services)
        {
            services.AddScoped<DashboardPageQuery>();
            services.AddScoped<DependencyGraphQuery>();
            services.AddScoped<DependencyGraphKustoEnricher>();
            services.AddScoped<JobDetailsPageQuery>();
            services.AddScoped<JobChartQuery>();
            services.AddScoped<ThrottleSeverityQuery>();
            services.AddScoped<LifecycleReadModel>();
            services.AddScoped<OperationalDetailsReadModel>();
        }

        private static void AddExecution(IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton<IClock>(SystemClock.Instance);
            services.AddSingleton<IKustoRequestBuilder, KustoRequestBuilder>();
            services.AddSingleton(_ => ResolveKustoOptions(configuration));
            services.AddScoped<IKustoControlCommandClientFactory, KoLiteKustoConnectionFactory>();
            services.AddScoped<IKustoExecutor, KustoSdkExecutor>();
            services.AddScoped<IKustoEntityDependencyReader, KustoSdkEntityDependencyReader>();
            services.AddScoped<ILocalSliceOutputExecutor, KustoLocalSliceOutputExecutor>();
            services.AddSingleton<LocalShutdownDrainCoordinator>();
            services.AddSingleton(new LocalWorkerOptions(WorkerId: "local-web-worker", EnforceJobParallelism: true));
            services.AddScoped<LocalWorkerFactory>();
            services.AddScoped(sp => new SqliteLocalScheduler(
                sp.GetRequiredService<SqliteJobCatalogRepository>(),
                sp.GetRequiredService<SqliteSliceStateRepository>(),
                sp.GetRequiredService<SqliteWorkQueueRepository>(),
                sp.GetRequiredService<SqliteOperationalReadModelRepository>(),
                sp.GetRequiredService<IClock>(),
                new LocalSchedulerOptions()));
            services.AddScoped(sp => sp.GetRequiredService<LocalWorkerFactory>().Create(sp.GetRequiredService<LocalWorkerOptions>().WorkerId));
            services.AddSingleton<ILocalWorkerProgressSink, LoggingLocalWorkerProgressSink>();
        }

        private static void AddWorkerHost(IServiceCollection services)
        {
            services.AddSingleton(sp => LocalBackgroundSchedulerOptions.From(sp.GetRequiredService<IConfiguration>()));
            services.AddSingleton(sp => LocalBackgroundWorkerPoolOptions.From(
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<LocalBackgroundSchedulerOptions>()));
            services.AddSingleton<LocalWorkerPoolRuntimeState>();
            services.AddHostedService<LocalBackgroundSchedulerService>();
            services.AddHostedService<LocalBackgroundWorkerService>();
        }

        private static void AddUpdates(IServiceCollection services)
        {
            services.AddSingleton(sp => LocalUpdateCheckOptions.From(sp.GetRequiredService<IConfiguration>()));
            services.AddSingleton(new AppBuildVersion(BuildInfo.GetCommitSha()));
            services.AddSingleton(sp => new UpdateCheckRuntimeState(UpdateCheckSnapshot.Initial(
                sp.GetRequiredService<LocalUpdateCheckOptions>().Enabled,
                sp.GetRequiredService<AppBuildVersion>().CommitSha)));
            services.AddSingleton<IRepositoryUpdateChecker, GhCliRepositoryUpdateChecker>();
            services.AddScoped<UpdateBadgeReadModel>();
            services.AddHostedService<LocalUpdateCheckBackgroundService>();
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

        // Binds KoLite:Throttling:* to the advisory options. Every value is optional and falls back to
        // the safe defaults; invalid or non-positive numbers are ignored rather than rejected.
        static ThrottleAdvisorOptions ResolveThrottleAdvisorOptions(IConfiguration configuration)
        {
            var defaults = ThrottleAdvisorOptions.Default;
            return new ThrottleAdvisorOptions
            {
                Enabled = bool.TryParse(configuration["KoLite:Throttling:Enabled"], out var enabled) ? enabled : defaults.Enabled,
                Window = TimeSpan.FromMinutes(ReadPositiveDouble(configuration, "KoLite:Throttling:WindowMinutes", defaults.Window.TotalMinutes)),
                MinThrottledSlices = ReadPositiveInt(configuration, "KoLite:Throttling:MinThrottledSlices", defaults.MinThrottledSlices),
                RateThresholdPercent = ReadPositiveDouble(configuration, "KoLite:Throttling:RateThresholdPercent", defaults.RateThresholdPercent),
                MinAttemptsForRate = ReadPositiveInt(configuration, "KoLite:Throttling:MinAttemptsForRate", defaults.MinAttemptsForRate),
                CleanPeriod = TimeSpan.FromMinutes(ReadPositiveDouble(configuration, "KoLite:Throttling:CleanPeriodMinutes", defaults.CleanPeriod.TotalMinutes)),
                TerminalFailureLookback = TimeSpan.FromMinutes(ReadPositiveDouble(configuration, "KoLite:Throttling:TerminalFailureLookbackMinutes", defaults.TerminalFailureLookback.TotalMinutes)),
                CatchUpTargetDuration = TimeSpan.FromHours(ReadPositiveDouble(configuration, "KoLite:Throttling:CatchUpTargetHours", defaults.CatchUpTargetDuration.TotalHours)),
                DurationLookback = TimeSpan.FromHours(ReadPositiveDouble(configuration, "KoLite:Throttling:DurationLookbackHours", defaults.DurationLookback.TotalHours)),
                MinDurationSamples = ReadPositiveInt(configuration, "KoLite:Throttling:MinDurationSamples", defaults.MinDurationSamples),
                DurationPercentile = Math.Clamp(ReadPositiveDouble(configuration, "KoLite:Throttling:DurationPercentile", defaults.DurationPercentile), 0.01, 1.0),
                KeepUpSafetyFactor = ReadPositiveDouble(configuration, "KoLite:Throttling:KeepUpSafetyFactor", defaults.KeepUpSafetyFactor)
            };
        }

        private static int ReadPositiveInt(IConfiguration configuration, string key, int defaultValue) =>
            int.TryParse(configuration[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 1 ? value : defaultValue;

        private static double ReadPositiveDouble(IConfiguration configuration, string key, double defaultValue) =>
            double.TryParse(configuration[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : defaultValue;
    }
}
