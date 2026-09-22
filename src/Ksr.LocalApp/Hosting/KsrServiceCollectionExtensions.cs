// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Core.Orchestration;
using Ksr.Local.Core.FailureSummaries;
using Ksr.Local.Core.Performance;
using Ksr.Local.Core.Time;
using Ksr.Local.Kusto.Execution;
using Ksr.Local.Sqlite.Catalog;
using Ksr.Local.Sqlite.Connections;
using Ksr.Local.Sqlite.Lifecycle;
using Ksr.Local.Sqlite.Observability;
using Ksr.Local.Sqlite.Orchestration;
using Ksr.Local.Sqlite.Performance;
using Ksr.Local.Sqlite.Queue;
using Ksr.Local.Sqlite.Repair;
using Ksr.Local.Sqlite.Rerun;
using Ksr.Local.Sqlite.Schema;
using Ksr.Local.Sqlite.State;
using Ksr.LocalApp.FailureAnalysis;
using Ksr.LocalApp.Application.Jobs;
using Ksr.LocalApp.Application.Lineage;
using Ksr.LocalApp.Application.Operations;
using Ksr.LocalApp.Application.Repair;
using Ksr.LocalApp.Application.System;
using Ksr.LocalApp.Http;
using Ksr.LocalApp.Performance;
using Ksr.LocalApp.Repair;
using Ksr.LocalApp.Retention;
using Ksr.LocalApp.Ui;
using Ksr.LocalApp.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ksr.LocalApp
{
    public static class KsrServiceCollectionExtensions
    {
        public static IServiceCollection AddKsrServices(this IServiceCollection services, IConfiguration configuration)
        {
            AddPersistence(services, configuration);
            AddReadModels(services);
            AddExecution(services, configuration);
            AddWorkerHost(services);
            AddPerformance(services);
            AddRetention(services);
            AddUpdates(services);
            AddFailureAnalysis(services, configuration);
            AddApplicationServices(services);
            return services;
        }

        private static void AddApplicationServices(IServiceCollection services)
        {
            services.AddSingleton<ILocalRequestPolicy, LoopbackLocalRequestPolicy>();
            services.AddScoped<JobApplicationService>();
            services.AddScoped<JobProjectionApplicationService>();
            services.AddScoped<OperationsApplicationService>();
            services.AddScoped<RepairApplicationService>();
            services.AddScoped<LineageApplicationService>();
            services.AddScoped<SystemStatusApplicationService>();
        }

        private static void AddPersistence(IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton(_ => new KsrSqliteConnectionOptions(ResolveDatabasePath(configuration)));
            services.AddSingleton<IKsrSqliteConnectionFactory, KsrSqliteConnectionFactory>();
            services.AddSingleton<KsrSqliteSchema>();
            services.AddScoped<SqliteJobCatalogRepository>();
            services.AddScoped<SqliteOperationalReadModelRepository>();
            services.AddScoped<SqliteLifecycleReadModelRepository>();
            services.AddScoped<SqliteDiagnosticsReadModelRepository>();
            services.AddScoped<SqliteWorkQueueRepository>();
            services.AddScoped<SqliteSliceStateRepository>();
            services.AddScoped<SqliteChunkStateRepository>();
            services.AddScoped<SqliteJobLifecycleService>();
            services.AddScoped<SqliteRerunService>();
            services.AddScoped<SqliteRepairService>();
            services.AddScoped<RepairApprovalCoordinator>();
        }

        private static void AddReadModels(IServiceCollection services)
        {
            services.AddScoped<DashboardPageQuery>();
            services.AddScoped<DependencyGraphQuery>();
            services.AddScoped<DependencyGraphKustoEnricher>();
            services.AddScoped<JobDetailsPageQuery>();
            services.AddScoped<JobChartQuery>();
            services.AddScoped<ActivityQuery>();
            services.AddScoped<PerformancePageQuery>();
            services.AddScoped<LifecycleReadModel>();
            services.AddScoped<OperationalDetailsReadModel>();
        }

        private static void AddExecution(IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton<IClock>(SystemClock.Instance);
            services.AddSingleton<IKustoRequestBuilder, KustoRequestBuilder>();
            services.AddSingleton(_ => ResolveKustoOptions(configuration));
            services.AddScoped<IKustoControlCommandClientFactory, KsrKustoConnectionFactory>();
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
                new LocalSchedulerOptions(),
                sp.GetRequiredService<SqliteChunkStateRepository>()));
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

        private static void AddRetention(IServiceCollection services)
        {
            services.AddSingleton(sp => LocalRetentionOptions.From(sp.GetRequiredService<IConfiguration>()));
            services.AddSingleton(sp => new RetentionRuntimeState(
                RetentionSnapshot.Initial(sp.GetRequiredService<LocalRetentionOptions>().Enabled)));
            services.AddHostedService<LocalRetentionBackgroundService>();
        }

        private static void AddPerformance(IServiceCollection services)
        {
            services.AddScoped<SqlitePerformanceRepository>();
            services.AddScoped<IPerformanceReportRepository>(sp => sp.GetRequiredService<SqlitePerformanceRepository>());
            services.AddScoped<IPerformanceCollectionStore>(sp => sp.GetRequiredService<SqlitePerformanceRepository>());
            services.AddScoped<IKustoCommandStatisticsReader, KustoCommandStatisticsReader>();
            services.AddScoped<IPerformanceCollectionPass, PerformanceCollectionPass>();
            services.AddSingleton(new PerformanceCollectionSchedule());
            services.AddHostedService<PerformanceCollectionBackgroundService>();
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

        // "Analyze failures with Copilot": a non-interactive, no-tools Copilot CLI process behind the
        // existing IFailureSummaryRunner seam, plus the ephemeral in-memory run registry and background
        // orchestrator. Nothing invokes Copilot until an operator triggers a run.
        private static void AddFailureAnalysis(IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton(_ => CopilotAnalysisOptions.From(configuration));
            services.AddSingleton<ICopilotCliInvoker, CopilotCliInvoker>();
            services.AddSingleton<IFailureSummaryRunner, CopilotCliFailureSummaryRunner>();
            services.AddSingleton<FailureAnalysisRunRegistry>();
            services.AddSingleton<FailureAnalysisOrchestrator>();
            services.AddScoped<FailureAnalysisPromptBuilder>();
        }

        static string ResolveDatabasePath(IConfiguration configuration)
        {
            var configured = configuration.GetConnectionString("KsrSqlite") ?? configuration["Ksr:DatabasePath"];
            if (!string.IsNullOrWhiteSpace(configured)) return configured;
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "Ksr", "ksr.db");
        }

        static KsrKustoOptions ResolveKustoOptions(IConfiguration configuration)
        {
            var rawMode = configuration["Ksr:Kusto:AuthMode"];
            var authMode = string.IsNullOrWhiteSpace(rawMode)
                ? KsrKustoAuthMode.AzureCli
                : Enum.TryParse<KsrKustoAuthMode>(rawMode, ignoreCase: true, out var parsed)
                    ? parsed
                    : throw new InvalidOperationException($"Unsupported Ksr:Kusto:AuthMode '{rawMode}'. Supported values: {string.Join(", ", Enum.GetNames<KsrKustoAuthMode>())}.");

            return new KsrKustoOptions
            {
                AuthMode = authMode,
                ManagedIdentityClientId = configuration["Ksr:Kusto:ManagedIdentityClientId"]
            };
        }
    }
}
