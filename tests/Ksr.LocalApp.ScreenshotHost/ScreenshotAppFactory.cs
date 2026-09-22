// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Core.FailureSummaries;
using Ksr.Local.Core.Orchestration;
using Ksr.Local.Core.Performance;
using Ksr.Local.Core.Schedules;
using Ksr.Local.Core.Scheduling;
using Ksr.Local.Core.Time;
using Ksr.Local.Kusto.Execution;
using Ksr.LocalApp.FailureAnalysis;
using Ksr.LocalApp.Updates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ksr.LocalApp.ScreenshotHost
{
    public sealed class ScreenshotAppFactory : WebApplicationFactory<Ksr.LocalApp.Program>
    {
        public const string IdentityHeader = "X-Ksr-Screenshot-Fixture";
        private readonly ScreenshotSandbox sandbox;

        public ScreenshotAppFactory(ScreenshotSandbox sandbox)
        {
            this.sandbox = sandbox;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Screenshot");
            builder.UseContentRoot(Path.Combine(sandbox.Workspace, "src", "Ksr.LocalApp"));
            builder.UseWebRoot(Path.Combine(AppContext.BaseDirectory, "wwwroot"));
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:KsrSqlite"] = sandbox.DatabasePath,
                ["Ksr:DatabasePath"] = sandbox.DatabasePath,
                ["Ksr:Urls"] = sandbox.BaseUrl,
                ["Ksr:Scheduler:Enabled"] = "false",
                ["Ksr:Retention:Enabled"] = "false",
                ["Ksr:UpdateCheck:Enabled"] = "false",
                ["Ksr:AllowMultipleInstances"] = "false",
                ["Ksr:CopilotAnalysis:Enabled"] = "false",
                ["Ksr:Kusto:AuthMode"] = "AzureCli"
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(new ManualClock(ScreenshotDataset.Now));
                services.RemoveAll<IKustoEntityDependencyReader>();
                services.AddSingleton<IKustoEntityDependencyReader, ScreenshotLineageReader>();
                services.RemoveAll<IFailureSummaryRunner>();
                services.AddSingleton<IFailureSummaryRunner, ScreenshotAnalysisRunner>();

                var blocked = new ForbiddenExternalServices();
                services.AddSingleton(blocked);
                Replace<ILocalSliceOutputExecutor>(services, blocked);
                Replace<IKustoExecutor>(services, blocked);
                Replace<IKustoCommandStatisticsReader>(services, blocked);
                Replace<IKustoControlCommandClientFactory>(services, blocked);
                Replace<ICopilotCliInvoker>(services, blocked);
                Replace<IRepositoryUpdateChecker>(services, blocked);

                services.AddDataProtection()
                    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(sandbox.RunDirectory, "keys")))
                    .SetApplicationName("Ksr.ScreenshotFixture." + sandbox.RunId);
                services.AddSingleton<IStartupFilter>(new ScreenshotIdentityFilter(sandbox.RunId));
            });
        }

        private static void Replace<T>(IServiceCollection services, T instance) where T : class
        {
            services.RemoveAll<T>();
            services.AddSingleton(instance);
        }

        private sealed class ScreenshotIdentityFilter : IStartupFilter
        {
            private readonly string runId;

            public ScreenshotIdentityFilter(string runId)
            {
                this.runId = runId;
            }

            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                app.Use(async (context, continuation) =>
                {
                    context.Response.Headers[IdentityHeader] = runId;
                    await continuation(context);
                });
                next(app);
            };
        }
    }

    public sealed class ForbiddenExternalServices : ILocalSliceOutputExecutor, IKustoExecutor,
        IKustoCommandStatisticsReader, IKustoControlCommandClientFactory, ICopilotCliInvoker, IRepositoryUpdateChecker
    {
        private int attempts;
        public int Attempts => Volatile.Read(ref attempts);

        private InvalidOperationException Blocked()
        {
            Interlocked.Increment(ref attempts);
            return new InvalidOperationException("External execution is forbidden in the documentation screenshot fixture.");
        }

        public Task<LocalSliceOutputResult> ExecuteAsync(JobDefinition job, SliceRange slice, CancellationToken cancellationToken = default) => throw Blocked();
        public Task<KustoExecutionResult> ExecuteAsync(KustoExecutionRequest request, CancellationToken cancellationToken = default) => throw Blocked();
        public Task<IReadOnlyList<KustoCommandStatistics>> ReadAsync(KustoCommandStatisticsQuery query, CancellationToken cancellationToken = default) => throw Blocked();
        public IKustoControlCommandClient Create(KustoExecutionRequest request) => throw Blocked();
        public IKustoControlCommandClient CreateForDatabase(Uri clusterUri, string database) => throw Blocked();
        public KsrKustoConnectionDescriptor Describe(KustoExecutionRequest request) => throw Blocked();
        public Task<CopilotCliOutcome> InvokeAsync(string prompt, CancellationToken cancellationToken) => throw Blocked();
        public Task<RepositoryUpdateCheckResult> CheckAsync(string repository, string? builtSha, CancellationToken cancellationToken) => throw Blocked();
    }
}
