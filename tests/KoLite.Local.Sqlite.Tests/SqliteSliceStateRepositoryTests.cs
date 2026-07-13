using KoLite.Local.Core.Schedules;
using KoLite.Local.Core.Scheduling;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Schema;
using KoLite.Local.Sqlite.State;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Tests
{
    public sealed class SqliteSliceStateRepositoryTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "state-file-tests", Guid.NewGuid().ToString("N"));
        private readonly KoLiteSqliteConnectionFactory factory;
        private readonly SqliteSliceStateRepository repository;

        public SqliteSliceStateRepositoryTests()
        {
            Directory.CreateDirectory(testDirectory);
            factory = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(Path.Combine(testDirectory, "state.db")) { BusyTimeoutMilliseconds = 10_000 });
            new KoLiteSqliteSchema(factory).EnsureSchema();
            var catalog = new SqliteJobCatalogRepository(factory);
            catalog.Create(Schedule("upstream", "00:30:00", dependsOn: null));
            catalog.Create(Schedule("downstream", "01:00:00", dependsOn: "upstream"));
            repository = new SqliteSliceStateRepository(factory);
        }

        public void Dispose()
        {
            TestCleanup.DeleteDirectoryWithRetry(testDirectory);
        }

        [Fact]
        public void Append_updates_projection_and_rejects_stale_expected_version()
        {
            var start = At(0);
            var end = At(30);
            var queued = repository.Append("op-queued", JobId("upstream"), start, end, DurableSliceStatus.Queued, expectedVersion: 0);
            var completed = repository.Append("op-completed", JobId("upstream"), start, end, DurableSliceStatus.Completed, expectedVersion: queued.State.Version);

            Assert.Equal(DurableSliceStatus.Completed, completed.State.Status);
            Assert.Equal(2, completed.State.Version);
            Assert.Throws<InvalidOperationException>(() => repository.Append("op-stale", JobId("upstream"), start, end, DurableSliceStatus.Failed, expectedVersion: 1));
        }

        [Fact]
        public void Lease_acquire_complete_and_expiry_are_safe()
        {
            var start = At(0);
            var end = At(30);
            repository.Append("op-queued", JobId("upstream"), start, end, DurableSliceStatus.Queued, expectedVersion: 0);
            var now = At(60);

            var lease = repository.AcquireLease("lease-1", JobId("upstream"), start, end, "worker-a", TimeSpan.FromMinutes(5), now);

            Assert.NotNull(lease);
            Assert.Equal(DurableSliceStatus.Running, lease.Status);
            Assert.Null(repository.AcquireLease("lease-2", JobId("upstream"), start, end, "worker-b", TimeSpan.FromMinutes(5), now.AddMinutes(1)));
            var currentLease = repository.AcquireLease("lease-3", JobId("upstream"), start, end, "worker-b", TimeSpan.FromMinutes(5), now.AddMinutes(6));
            Assert.NotNull(currentLease);
            Assert.False(repository.CompleteLease("complete-stale", JobId("upstream"), start, end, "worker-a", lease.LeaseToken!, now.AddMinutes(7)));
            Assert.True(repository.CompleteLease("complete-current", JobId("upstream"), start, end, "worker-b", currentLease.LeaseToken!, now.AddMinutes(7)));
            Assert.Equal(DurableSliceStatus.Completed, repository.Get(JobId("upstream"), start, end).Status);
        }

        [Fact]
        public void Terminal_transitions_require_current_lease_token_after_reacquire()
        {
            var start = At(30);
            var end = At(60);
            repository.Append("op-queued-token", JobId("upstream"), start, end, DurableSliceStatus.Queued, expectedVersion: 0);
            var now = At(90);

            var staleLease = repository.AcquireLease("lease-token-stale", JobId("upstream"), start, end, "same-worker", TimeSpan.FromMinutes(5), now);
            var currentLease = repository.AcquireLease("lease-token-current", JobId("upstream"), start, end, "same-worker", TimeSpan.FromMinutes(5), now.AddMinutes(6));

            Assert.NotNull(staleLease);
            Assert.NotNull(currentLease);
            Assert.False(repository.FailLease("fail-stale-token", JobId("upstream"), start, end, "same-worker", staleLease.LeaseToken!, now.AddMinutes(7), "stale"));
            Assert.Equal(DurableSliceStatus.Running, repository.Get(JobId("upstream"), start, end).Status);
            Assert.True(repository.FailLease("fail-current-token", JobId("upstream"), start, end, "same-worker", currentLease.LeaseToken!, now.AddMinutes(7), "current"));
            Assert.Equal(DurableSliceStatus.Failed, repository.Get(JobId("upstream"), start, end).Status);
        }

        [Fact]
        public void Dependency_readiness_uses_persisted_current_state_per_window()
        {
            repository.Append("u1", JobId("upstream"), At(0), At(30), DurableSliceStatus.Completed, expectedVersion: 0);
            var downstream = Job("downstream", "01:00:00", dependsOn: "upstream");
            var jobs = new Dictionary<string, JobDefinition> { [JobId("upstream")] = Job("upstream", "00:30:00"), [JobId("downstream")] = downstream };

            var notReady = repository.EvaluateDependencyReadiness(downstream, new SliceRange(JobId("downstream"), At(0), At(60)), jobs);
            repository.Append("u2", JobId("upstream"), At(30), At(60), DurableSliceStatus.Completed, expectedVersion: 0);
            var ready = repository.EvaluateDependencyReadiness(downstream, new SliceRange(JobId("downstream"), At(0), At(60)), jobs);

            Assert.False(notReady.IsReady);
            Assert.Contains(SliceKey.Create(JobId("upstream"), At(30), At(60)), notReady.MissingSlices);
            Assert.True(ready.IsReady);
        }

        [Fact]
        public async Task Concurrent_lease_attempts_allow_only_one_owner()
        {
            repository.Append("op-queued", JobId("upstream"), At(0), At(30), DurableSliceStatus.Queued, expectedVersion: 0);
            var now = At(60);

            var leases = await Task.WhenAll(
                Task.Run(() => repository.AcquireLease("lease-a", JobId("upstream"), At(0), At(30), "worker-a", TimeSpan.FromMinutes(5), now)),
                Task.Run(() => repository.AcquireLease("lease-b", JobId("upstream"), At(0), At(30), "worker-b", TimeSpan.FromMinutes(5), now)));

            Assert.Single(leases, l => l is not null);
        }

        [Fact]
        public void ListSliceStates_reports_status_and_event_count_version_per_slice()
        {
            var jobId = JobId("upstream");
            var s0 = At(0);
            var e0 = At(30);
            var s1 = At(30);
            var e1 = At(60);

            // Slice 0: two events -> version 2, terminal Completed.
            var queued = repository.Append("op-0-queued", jobId, s0, e0, DurableSliceStatus.Queued, expectedVersion: 0);
            repository.Append("op-0-done", jobId, s0, e0, DurableSliceStatus.Completed, expectedVersion: queued.State.Version);

            // Slice 1: one event -> version 1, Queued.
            repository.Append("op-1-queued", jobId, s1, e1, DurableSliceStatus.Queued, expectedVersion: 0);

            var map = repository.ListSliceStates(jobId);

            Assert.Equal(2, map.Count);
            Assert.Equal(new SliceSchedulingState(DurableSliceStatus.Completed, 2), map[s0.ToUniversalTime()]);
            Assert.Equal(new SliceSchedulingState(DurableSliceStatus.Queued, 1), map[s1.ToUniversalTime()]);
        }

        [Fact]
        public void ListSliceStates_is_empty_for_a_job_without_materialized_slices()
        {
            Assert.Empty(repository.ListSliceStates(JobId("downstream")));
        }

        private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        private static string JobId(string activityId)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(activityId));
            return new Guid(bytes).ToString("N");
        }

        private static JobDefinition Job(string activityId, string window, string? dependsOn = null) => new()
        {
            Id = JobId(activityId),
            ActivityId = activityId,
            FunctionName = "Fn",
            OutputTable = "Out",
            QueryWindowSize = TimeSpan.Parse(window, System.Globalization.CultureInfo.InvariantCulture),
            DelayFromUtcNow = TimeSpan.Zero,
            MaxParallelism = 1,
            QueryTimeout = TimeSpan.FromMinutes(1),
            StartFrom = At(0),
            Target = new JobTarget { ClusterUri = "https://kolite-example.invalid", Database = "DemoDb" },
            DependsOn = dependsOn is null ? [] : [new DependentJob { Id = JobId(dependsOn) }]
        };

        private static string Schedule(string activityId, string window, string? dependsOn) => $$"""
        {
          "id": "{{JobId(activityId)}}",
          "activityId": "{{activityId}}",
          "functionName": "StateFunction",
          "outputTable": "StateOutput",
          "queryWindowSize": "{{window}}",
          "delayFromUtcNow": "00:00:00",
          "maxParallelism": 1,
          "queryTimeout": "00:01:00",
          "isPaused": false,
          "startFrom": "2026-01-01T00:00:00Z",
          "dependsOn": {{(dependsOn is null ? "[]" : $"[{{ \"activityId\": \"{dependsOn}\" }}]")}},
          "target": { "clusterUri": "https://kolite-example.invalid", "database": "DemoDb" }
        }
        """;
    }
}
