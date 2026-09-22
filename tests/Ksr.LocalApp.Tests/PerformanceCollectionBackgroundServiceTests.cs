// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Core.Time;
using Ksr.LocalApp.Performance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ksr.LocalApp.Tests
{
    public sealed class PerformanceCollectionBackgroundServiceTests
    {
        [Fact]
        public void Collection_is_registered_without_a_feature_enable_switch()
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ksr:Performance:Enabled"] = "false"
            }).Build();
            var services = new ServiceCollection();
            services.AddKsrServices(configuration);

            Assert.True(LocalBackgroundSchedulerOptions.From(configuration).Enabled);
            Assert.Contains(services, item => item.ServiceType == typeof(IHostedService)
                && item.ImplementationType == typeof(PerformanceCollectionBackgroundService));
            Assert.DoesNotContain(typeof(PerformanceCollectionSchedule).GetProperties(), item => item.Name == "Enabled");
        }

        [Fact]
        public async Task Execution_disabled_host_never_resolves_a_collection_pass()
        {
            var resolutions = 0;
            var services = new ServiceCollection();
            services.AddScoped<IPerformanceCollectionPass>(_ =>
            {
                Interlocked.Increment(ref resolutions);
                throw new InvalidOperationException("The collection pass must not be resolved.");
            });
            using var provider = services.BuildServiceProvider();
            using var service = CreateService(provider, enabled: false);

            await service.StartAsync(CancellationToken.None);
            Assert.NotNull(service.ExecuteTask);
            await service.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(5));
            await service.StopAsync(CancellationToken.None);

            Assert.Equal(0, resolutions);
        }

        [Fact]
        public async Task Normal_instance_collects_with_no_workers_and_alternates_recent_and_older_work()
        {
            var pass = new RecordingPass();
            using var provider = Provider(pass);
            var drain = new LocalShutdownDrainCoordinator();
            using var service = CreateService(provider, drain: drain);

            await service.StartAsync(CancellationToken.None);
            await pass.SecondCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.StopAsync(CancellationToken.None);

            Assert.True(pass.Initializations[0]);
            Assert.False(pass.Initializations[1]);
            Assert.False(pass.OldestFirst[0]);
            Assert.True(pass.OldestFirst[1]);
            Assert.Equal(0, drain.GetSnapshot().ActiveWorkerCount);
        }

        [Fact]
        public async Task A_failing_pass_is_retried_without_faulting_the_host()
        {
            var pass = new RecordingPass(failFirst: true);
            using var provider = Provider(pass);
            using var service = CreateService(provider);

            await service.StartAsync(CancellationToken.None);
            await pass.SecondCall.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(service.ExecuteTask);
            Assert.False(service.ExecuteTask.IsFaulted);
            Assert.True(pass.Initializations[1]);
            await service.StopAsync(CancellationToken.None);
        }

        [Fact]
        public async Task Drain_prevents_new_collection_and_backfill_passes()
        {
            var pass = new RecordingPass();
            using var provider = Provider(pass);
            var drain = new LocalShutdownDrainCoordinator();
            drain.RequestDrain(DateTimeOffset.UtcNow, "test");
            using var service = CreateService(provider, drain: drain);

            await service.StartAsync(CancellationToken.None);
            await Task.Delay(80);
            await service.StopAsync(CancellationToken.None);

            Assert.Empty(pass.Initializations);
        }

        [Fact]
        public async Task Shutdown_cancels_collection_without_waiting_for_history()
        {
            var pass = new BlockingPass();
            using var provider = Provider(pass);
            using var service = CreateService(provider);

            await service.StartAsync(CancellationToken.None);
            await pass.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(pass.WasCancelled);
            Assert.False(service.ExecuteTask!.IsFaulted);
        }

        [Fact]
        public async Task Local_backfill_is_continued_without_restarting_its_checkpoint()
        {
            var pass = new RecordingPass(firstHistoryIncomplete: true);
            using var provider = Provider(pass);
            using var service = CreateService(provider);

            await service.StartAsync(CancellationToken.None);
            await pass.SecondCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.StopAsync(CancellationToken.None);

            Assert.True(pass.Initializations[0]);
            Assert.False(pass.Initializations[1]);
            Assert.False(pass.OldestFirst[1]);
        }

        private static ServiceProvider Provider(IPerformanceCollectionPass pass)
        {
            var services = new ServiceCollection();
            services.AddSingleton(pass);
            return services.BuildServiceProvider();
        }

        private static PerformanceCollectionBackgroundService CreateService(
            ServiceProvider provider,
            bool enabled = true,
            LocalShutdownDrainCoordinator? drain = null)
        {
            return new PerformanceCollectionBackgroundService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new LocalBackgroundSchedulerOptions(enabled, TimeSpan.FromSeconds(10), false),
                drain ?? new LocalShutdownDrainCoordinator(),
                new PerformanceCollectionSchedule
                {
                    PassInterval = TimeSpan.FromMilliseconds(20),
                    BackfillInterval = TimeSpan.FromMilliseconds(5)
                },
                new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
                NullLogger<PerformanceCollectionBackgroundService>.Instance);
        }

        private sealed class RecordingPass : IPerformanceCollectionPass
        {
            private readonly bool failFirst;
            private readonly bool firstHistoryIncomplete;

            public RecordingPass(bool failFirst = false, bool firstHistoryIncomplete = false)
            {
                this.failFirst = failFirst;
                this.firstHistoryIncomplete = firstHistoryIncomplete;
            }

            public List<bool> Initializations { get; } = [];
            public List<bool> OldestFirst { get; } = [];
            public TaskCompletionSource SecondCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<bool> RunAsync(bool beginReconciliation, bool oldestFirst, CancellationToken cancellationToken)
            {
                Initializations.Add(beginReconciliation);
                OldestFirst.Add(oldestFirst);
                var call = Initializations.Count;
                if (call >= 2)
                {
                    SecondCall.TrySetResult();
                }

                if (failFirst && call == 1)
                {
                    throw new InvalidOperationException("Simulated temporary collector failure.");
                }

                return Task.FromResult(!(firstHistoryIncomplete && call == 1));
            }
        }

        private sealed class BlockingPass : IPerformanceCollectionPass
        {
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool WasCancelled { get; private set; }

            public async Task<bool> RunAsync(bool beginReconciliation, bool oldestFirst, CancellationToken cancellationToken)
            {
                Entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    WasCancelled = true;
                    throw;
                }

                return true;
            }
        }
    }
}
