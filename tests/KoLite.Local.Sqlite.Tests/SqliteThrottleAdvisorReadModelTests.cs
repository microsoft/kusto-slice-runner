using KoLite.Local.Core.Throttling;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Migrations;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.State;
using KoLite.Local.Sqlite.Throttling;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Tests
{
    public sealed class SqliteThrottleAdvisorReadModelTests : IDisposable
    {
        private const string ClusterA = "https://sample-data.centralus.kusto.windows.net";
        private const string ClusterB = "https://other.centralus.kusto.windows.net";

        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-06-23T22:00:00Z");

        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "throttle-advisor-tests", Guid.NewGuid().ToString("N"));
        private readonly KoLiteSqliteConnectionFactory factory;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteIngestionThrottleRepository throttleStore;
        private readonly SqliteOperationalReadModelRepository readModels;
        private readonly SqliteWorkQueueRepository queue;
        private readonly SqliteSliceStateRepository state;
        private readonly ManualClock clock = new(Now);

        public SqliteThrottleAdvisorReadModelTests()
        {
            Directory.CreateDirectory(testDirectory);
            factory = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(Path.Combine(testDirectory, "advisor.db")) { BusyTimeoutMilliseconds = 10_000 });
            new KoLiteSqliteMigrator(factory).Migrate();
            catalog = new SqliteJobCatalogRepository(factory);
            throttleStore = new SqliteIngestionThrottleRepository(factory);
            readModels = new SqliteOperationalReadModelRepository(factory);
            queue = new SqliteWorkQueueRepository(factory);
            state = new SqliteSliceStateRepository(factory);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }

        [Fact]
        public void Ranks_active_jobs_on_a_sustained_cluster_by_headroom()
        {
            var big = Create("job.big", ClusterA, maxParallelism: 12);
            var small = Create("job.small", ClusterA, maxParallelism: 3);
            // Each job has a ~2 minute successful slice duration => keep-up floor of 1 (window 5m, factor 1).
            SeedSuccessfulDurations(big, minutes: 2, count: 6);
            SeedSuccessfulDurations(small, minutes: 2, count: 6);
            // Three distinct throttled slices on cluster A => sustained. Both jobs are throttled => active.
            Throttle(big, "2026-06-23T06:55:00Z");
            Throttle(big, "2026-06-23T07:00:00Z");
            Throttle(small, "2026-06-23T07:45:00Z");

            var advisories = BuildAdvisor().BuildAdvisories();

            var cluster = Assert.Single(advisories);
            Assert.Equal(ClusterA, cluster.ClusterUri);
            Assert.Equal(3, cluster.ThrottledSliceCount);
            Assert.Collection(
                cluster.Recommendations,
                r => { Assert.Equal("job.big", r.ActivityId); Assert.Equal(ParallelismRecommendationStatus.Recommended, r.Status); Assert.Equal(1, r.RecommendedMaxParallelism); Assert.Equal(11, r.Headroom); },
                r => { Assert.Equal("job.small", r.ActivityId); Assert.Equal(2, r.Headroom); });
        }

        [Fact]
        public void Includes_an_in_flight_job_that_did_not_itself_throttle()
        {
            var noisy = Create("job.noisy", ClusterA, maxParallelism: 8);
            var quiet = Create("job.quiet", ClusterA, maxParallelism: 6);
            SeedSuccessfulDurations(noisy, minutes: 2, count: 6);
            SeedSuccessfulDurations(quiet, minutes: 2, count: 6);
            // Cluster A is sustained by the noisy job alone.
            Throttle(noisy, "2026-06-23T06:55:00Z");
            Throttle(noisy, "2026-06-23T07:00:00Z");
            Throttle(noisy, "2026-06-23T07:05:00Z");
            // The quiet job is currently running a slice (leased) but never threw a throttle.
            LeaseSlice(quiet, "2026-06-23T07:50:00Z");

            var advisories = BuildAdvisor().BuildAdvisories();

            var cluster = Assert.Single(advisories);
            Assert.Contains(cluster.Recommendations, r => r.ActivityId == "job.quiet" && r.InFlightCount == 1);
            Assert.Contains(cluster.Recommendations, r => r.ActivityId == "job.noisy");
        }

        [Fact]
        public void Does_not_surface_a_cluster_below_the_sustained_threshold()
        {
            var job = Create("job.few", ClusterA, maxParallelism: 8);
            SeedSuccessfulDurations(job, minutes: 2, count: 6);
            Throttle(job, "2026-06-23T06:55:00Z");
            Throttle(job, "2026-06-23T07:00:00Z"); // only 2 distinct slices, threshold is 3

            Assert.Empty(BuildAdvisor().BuildAdvisories());
        }

        [Fact]
        public void Excludes_jobs_on_other_clusters()
        {
            var onA = Create("job.a", ClusterA, maxParallelism: 8);
            var onB = Create("job.b", ClusterB, maxParallelism: 8);
            SeedSuccessfulDurations(onA, minutes: 2, count: 6);
            SeedSuccessfulDurations(onB, minutes: 2, count: 6);
            Throttle(onA, "2026-06-23T06:55:00Z");
            Throttle(onA, "2026-06-23T07:00:00Z");
            Throttle(onA, "2026-06-23T07:05:00Z");
            LeaseSlice(onB, "2026-06-23T07:50:00Z");

            var cluster = Assert.Single(BuildAdvisor().BuildAdvisories());
            Assert.Equal(ClusterA, cluster.ClusterUri);
            Assert.DoesNotContain(cluster.Recommendations, r => r.ActivityId == "job.b");
        }

        [Fact]
        public void Reports_insufficient_data_without_enough_duration_samples()
        {
            var job = Create("job.cold", ClusterA, maxParallelism: 8);
            SeedSuccessfulDurations(job, minutes: 2, count: 2); // below MinDurationSamples (3)
            Throttle(job, "2026-06-23T06:55:00Z");
            Throttle(job, "2026-06-23T07:00:00Z");
            Throttle(job, "2026-06-23T07:05:00Z");

            var cluster = Assert.Single(BuildAdvisor().BuildAdvisories());
            var rec = Assert.Single(cluster.Recommendations);
            Assert.Equal(ParallelismRecommendationStatus.InsufficientData, rec.Status);
            Assert.Null(rec.KeepUpFloor);
        }

        [Fact]
        public void Returns_nothing_when_disabled()
        {
            var job = Create("job.x", ClusterA, maxParallelism: 8);
            SeedSuccessfulDurations(job, minutes: 2, count: 6);
            Throttle(job, "2026-06-23T06:55:00Z");
            Throttle(job, "2026-06-23T07:00:00Z");
            Throttle(job, "2026-06-23T07:05:00Z");

            var advisor = BuildAdvisor(Options() with { Enabled = false });
            Assert.Empty(advisor.BuildAdvisories());
        }

        private SqliteThrottleAdvisorReadModel BuildAdvisor(ThrottleAdvisorOptions? options = null) =>
            new(factory, catalog, throttleStore, clock, options ?? Options());

        private static ThrottleAdvisorOptions Options() => new()
        {
            Window = TimeSpan.FromMinutes(30),
            MinThrottledSlices = 3,
            DurationLookback = TimeSpan.FromHours(6),
            MinDurationSamples = 3,
            DurationPercentile = 0.75,
            KeepUpSafetyFactor = 1.0
        };

        private string Create(string activityId, string cluster, int maxParallelism)
        {
            var record = catalog.Create(Schedule(activityId, cluster, maxParallelism));
            return record.JobId;
        }

        private void Throttle(string jobId, string sliceStart)
        {
            var start = DateTimeOffset.Parse(sliceStart);
            throttleStore.Record(new IngestionThrottleObservation(jobId, ClusterFor(jobId), start, start.AddMinutes(5), Attempt: 2, ReportedCapacity: 18, Now.AddMinutes(-5)));
        }

        private string ClusterFor(string jobId) => catalog.Get(jobId)!.Definition.Target.ClusterUri;

        private void SeedSuccessfulDurations(string jobId, int minutes, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var sliceStart = new DateTimeOffset(2026, 6, 23, 0, 0, 0, TimeSpan.Zero).AddMinutes(i * 5);
                var sliceEnd = sliceStart.AddMinutes(5);
                state.Append($"{jobId}-state-{i}", jobId, sliceStart, sliceEnd, DurableSliceStatus.Completed, expectedVersion: 0);
                var startedAt = Now.AddHours(-1).AddMinutes(i);
                readModels.RecordAttempt($"{jobId}-succ-{i}", jobId, sliceStart, sliceEnd, 1, "Succeeded", "worker", startedAt, startedAt.AddMinutes(minutes));
            }
        }

        private void LeaseSlice(string jobId, string sliceStart)
        {
            var start = DateTimeOffset.Parse(sliceStart);
            var end = start.AddMinutes(5);
            state.Append($"{jobId}-lease-state", jobId, start, end, DurableSliceStatus.Queued, expectedVersion: 0);
            var key = $"{jobId}|{start:O}";
            queue.Enqueue(jobId, start, end, key, Now.AddMinutes(-10));
            var claimed = queue.Claim("default", "worker", TimeSpan.FromMinutes(10), Now);
            Assert.NotNull(claimed);
        }

        private static string Schedule(string activityId, string cluster, int maxParallelism) => $$"""
        {
          "id": "{{JobGuid(activityId)}}",
          "activityId": "{{activityId}}",
          "functionName": "ObsFunction",
          "outputTable": "ObsOutput",
          "queryWindowSize": "00:05:00",
          "delayFromUtcNow": "00:00:00",
          "maxParallelism": {{maxParallelism}},
          "queryTimeout": "00:01:00",
          "isPaused": false,
          "startFrom": "2026-06-23T00:00:00Z",
          "target": { "clusterUri": "{{cluster}}", "database": "DemoDb" }
        }
        """;

        private static string JobGuid(string activityId)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(activityId));
            return new Guid(bytes).ToString("N");
        }
    }
}
