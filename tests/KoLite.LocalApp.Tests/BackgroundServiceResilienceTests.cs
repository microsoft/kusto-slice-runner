using KoLite.Local.Core.Orchestration;
using KoLite.Local.Core.Scheduling;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Orchestration;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.State;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KoLite.LocalApp.Tests
{
    // Regression coverage for the outage where a single transient SQLite error in a scheduler pass
    // propagated out of the BackgroundService and, via the default
    // HostOptions.BackgroundServiceExceptionBehavior = StopHost, stopped the entire host. Every
    // background loop must log-and-continue instead of faulting.
    public sealed class BackgroundServiceResilienceTests
    {
        [Fact]
        public async Task Scheduler_survives_a_failing_pass_and_keeps_the_host_running()
        {
            var provider = BuildFailingSqliteProvider();
            var service = new LocalBackgroundSchedulerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new LocalBackgroundSchedulerOptions(Enabled: true, TickInterval: TimeSpan.FromMilliseconds(20), LogEveryPass: false),
                new LocalShutdownDrainCoordinator(),
                NullLogger<LocalBackgroundSchedulerService>.Instance);

            await service.StartAsync(CancellationToken.None);
            await Task.Delay(250);

            Assert.NotNull(service.ExecuteTask);
            Assert.False(service.ExecuteTask!.IsFaulted, "A failing scheduler pass must not fault the host.");
            Assert.False(service.ExecuteTask!.IsCompleted, "The scheduler loop must keep running across failing passes.");

            await service.StopAsync(CancellationToken.None);
        }

        [Fact]
        public async Task Worker_dispatcher_survives_a_failing_cycle_and_keeps_the_host_running()
        {
            var provider = BuildFailingSqliteProvider();
            var service = new LocalBackgroundWorkerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new LocalBackgroundWorkerPoolOptions(
                    Enabled: true,
                    EnabledSource: "test",
                    Mode: LocalBackgroundWorkerPoolOptions.FixedMode,
                    MaxConcurrency: 4,
                    MaxConcurrencySource: "test",
                    IdleDelay: TimeSpan.FromMilliseconds(20),
                    IdleDelaySource: "test",
                    MaxDispatchStartsPerCycle: 10,
                    MaxDispatchStartsPerCycleSource: "test",
                    LogEveryPass: false),
                new LocalWorkerPoolRuntimeState(),
                new LocalWorkerOptions(WorkerId: "test-worker", EnforceJobParallelism: true),
                new LocalShutdownDrainCoordinator(),
                new ManualClock(DateTimeOffset.UnixEpoch),
                NullLogger<LocalBackgroundWorkerService>.Instance);

            await service.StartAsync(CancellationToken.None);
            await Task.Delay(250);

            Assert.NotNull(service.ExecuteTask);
            Assert.False(service.ExecuteTask!.IsFaulted, "A failing worker dispatch cycle must not fault the host.");
            Assert.False(service.ExecuteTask!.IsCompleted, "The worker dispatcher loop must keep running across failing cycles.");

            await service.StopAsync(CancellationToken.None);
        }

        // Wires the real repositories and scheduler over a connection factory that always throws a
        // transient SQLITE_ABORT, so every scheduler/worker DB access fails the same way the live
        // incident did.
        private static ServiceProvider BuildFailingSqliteProvider()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IKoLiteSqliteConnectionFactory>(new ThrowingConnectionFactory());
            services.AddSingleton<SqliteJobCatalogRepository>();
            services.AddSingleton<SqliteSliceStateRepository>();
            services.AddSingleton<SqliteWorkQueueRepository>();
            services.AddSingleton<SqliteOperationalReadModelRepository>();
            services.AddSingleton(new LocalSchedulerOptions());
            services.AddSingleton<SqliteLocalScheduler>();
            services.AddSingleton<IClock>(new ManualClock(DateTimeOffset.UnixEpoch));
            return services.BuildServiceProvider();
        }

        private sealed class ThrowingConnectionFactory : IKoLiteSqliteConnectionFactory
        {
            public SqliteConnection OpenConnection() => throw new SqliteException("query aborted", 4);
        }
    }
}
