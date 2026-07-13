using KoLite.Local.Core.Throttling;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Schema;
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
            new KoLiteSqliteSchema(factory).EnsureSchema();
            catalog = new SqliteJobCatalogRepository(factory);
            throttleStore = new SqliteIngestionThrottleRepository(factory);
            readModels = new SqliteOperationalReadModelRepository(factory);
            queue = new SqliteWorkQueueRepository(factory);
            state = new SqliteSliceStateRepository(factory);
        }

        public void Dispose()
        {
            TestCleanup.DeleteDirectoryWithRetry(testDirectory);
        }

        [Fact]
        public void Ranks_active_jobs_on_a_displayed_cluster_by_headroom()
        {
            var big = Create("job.big", ClusterA, maxParallelism: 12);
            var small = Create("job.small", ClusterA, maxParallelism: 3);
            // ~2 minute successful slices => keep-up floor 1 (window 5m, factor 1). With a 5m query
            // window over those fast slices the catch-up floor is also 1, so the suggestion stays 1.
            SeedSuccessfulDurations(big, minutes: 2, count: 6);
            SeedSuccessfulDurations(small, minutes: 2, count: 6);
            // Three distinct throttled slices/attempts on cluster A => rate gate met, both jobs active.
            Throttle(big, "2026-06-23T06:55:00Z");
            Throttle(big, "2026-06-23T07:00:00Z");
            Throttle(small, "2026-06-23T07:45:00Z");

            var advisories = BuildAdvisor().BuildAdvisories();

            var cluster = Assert.Single(advisories);
            Assert.Equal(ClusterA, cluster.ClusterUri);
            Assert.Equal(3, cluster.ThrottledSliceCount);
            Assert.Equal(3, cluster.ThrottledAttemptCount);
            Assert.Equal(3, cluster.TotalAttemptCount);
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
        public void Does_not_surface_a_cluster_below_the_distinct_slice_threshold()
        {
            var job = Create("job.few", ClusterA, maxParallelism: 8);
            SeedSuccessfulDurations(job, minutes: 2, count: 6);
            Throttle(job, "2026-06-23T06:55:00Z");
            Throttle(job, "2026-06-23T07:00:00Z"); // only 2 distinct slices, threshold is 3

            Assert.Empty(BuildAdvisor().BuildAdvisories());
        }

        [Fact]
        public void Does_not_surface_a_cluster_below_the_rate_threshold()
        {
            var job = Create("job.dilute", ClusterA, maxParallelism: 8);
            SeedSuccessfulDurations(job, minutes: 2, count: 6);
            Throttle(job, "2026-06-23T06:55:00Z");
            Throttle(job, "2026-06-23T07:00:00Z");
            Throttle(job, "2026-06-23T07:05:00Z");
            // 60 clean in-window attempts dilute the rate to 3/63 (~4.8%), below the 5% gate.
            SeedInWindowSuccesses(job, count: 60);

            Assert.Empty(BuildAdvisor().BuildAdvisories());
        }

        [Fact]
        public void Does_not_surface_a_cluster_that_has_been_clean_for_the_clean_period()
        {
            var job = Create("job.cleared", ClusterA, maxParallelism: 8);
            SeedSuccessfulDurations(job, minutes: 2, count: 6);
            // Three throttles, but the latest is 25 minutes ago (> 15m clean period) so it has cleared.
            Throttle(job, "2026-06-23T06:55:00Z", Now.AddMinutes(-25));
            Throttle(job, "2026-06-23T07:00:00Z", Now.AddMinutes(-25));
            Throttle(job, "2026-06-23T07:05:00Z", Now.AddMinutes(-25));

            Assert.Empty(BuildAdvisor().BuildAdvisories());
        }

        [Fact]
        public void Terminal_throttle_failure_forces_display_and_is_listed()
        {
            var job = Create("job.lost", ClusterA, maxParallelism: 8);
            SeedSuccessfulDurations(job, minutes: 2, count: 6);
            // A single slice that dead-lettered on throttling 25 minutes ago: the rate gate is not met
            // (one distinct slice, cleared) yet it must still surface and be listed.
            ThrottleTerminal(job, "2026-06-23T07:30:00Z", Now.AddMinutes(-25));

            var cluster = Assert.Single(BuildAdvisor().BuildAdvisories());
            var failure = Assert.Single(cluster.TerminalFailures);
            Assert.Equal("job.lost", failure.ActivityId);
            Assert.Equal("DeadLettered", failure.CurrentState);
            Assert.Contains(cluster.Recommendations, r => r.ActivityId == "job.lost");
        }

        [Fact]
        public void Backfilling_job_is_recommended_to_its_catch_up_floor_with_an_eta()
        {
            // ~20 minute slices over a 5m window => keep-up floor 4. The job started at 00:00 with only
            // 6 completed slices, so it has a large backlog and is sized to a higher catch-up floor.
            var job = Create("job.backfill", ClusterA, maxParallelism: 20);
            SeedSuccessfulDurations(job, minutes: 20, count: 6);
            Throttle(job, "2026-06-23T06:55:00Z");
            Throttle(job, "2026-06-23T07:00:00Z");
            Throttle(job, "2026-06-23T07:05:00Z");

            var cluster = Assert.Single(BuildAdvisor().BuildAdvisories());
            var rec = Assert.Single(cluster.Recommendations);
            Assert.True(rec.IsBackfilling);
            Assert.Equal(4, rec.KeepUpFloor);
            Assert.NotNull(rec.CatchUpFloor);
            Assert.True(rec.CatchUpFloor > rec.KeepUpFloor, "catch-up floor should exceed the keep-up floor for a real backlog");
            Assert.Equal(rec.CatchUpFloor, rec.RecommendedMaxParallelism);
            Assert.True(rec.BacklogDataTime > TimeSpan.Zero);
            Assert.NotNull(rec.CurrentCatchUpEta);
            Assert.NotNull(rec.ProjectedCatchUpEta);
            Assert.True(rec.ProjectedCatchUpEta > rec.CurrentCatchUpEta, "reducing parallelism should lengthen the catch-up ETA");
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
            new(factory, catalog, throttleStore, readModels, clock, options ?? Options());

        private static ThrottleAdvisorOptions Options() => new()
        {
            Window = TimeSpan.FromMinutes(30),
            MinThrottledSlices = 3,
            RateThresholdPercent = 5.0,
            MinAttemptsForRate = 3,
            CleanPeriod = TimeSpan.FromMinutes(15),
            TerminalFailureLookback = TimeSpan.FromMinutes(60),
            CatchUpTargetDuration = TimeSpan.FromHours(24),
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

        // Records a non-terminal ingestion throttle: a slice state, a retryable attempt (the rate
        // denominator) and the throttle observation (the numerator), all at observedAt.
        private void Throttle(string jobId, string sliceStart, DateTimeOffset? observedAt = null)
        {
            var at = observedAt ?? Now.AddMinutes(-5);
            var start = DateTimeOffset.Parse(sliceStart);
            var end = start.AddMinutes(5);
            state.Append($"{jobId}-thr-{sliceStart}", jobId, start, end, DurableSliceStatus.Running, expectedVersion: 0);
            readModels.RecordAttempt($"{jobId}-thr-att-{sliceStart}", jobId, start, end, 2, "FailedRetryable", "worker", at.AddMinutes(-1), at, "KustoRequestThrottledException", "Origin: 'CapacityPolicy/Ingestion'");
            throttleStore.Record(new IngestionThrottleObservation(jobId, ClusterFor(jobId), start, end, Attempt: 2, ReportedCapacity: 18, at, Terminal: false));
        }

        // Records a slice that dead-lettered on throttling: a DeadLettered slice state, a dead-letter
        // attempt and a terminal throttle observation.
        private void ThrottleTerminal(string jobId, string sliceStart, DateTimeOffset observedAt)
        {
            var start = DateTimeOffset.Parse(sliceStart);
            var end = start.AddMinutes(5);
            state.Append($"{jobId}-dl-{sliceStart}", jobId, start, end, DurableSliceStatus.DeadLettered, expectedVersion: 0, reason: "throttled");
            readModels.RecordAttempt($"{jobId}-dl-att-{sliceStart}", jobId, start, end, 3, "DeadLettered", "worker", observedAt.AddMinutes(-1), observedAt, "KustoRequestThrottledException", "Origin: 'CapacityPolicy/Ingestion'");
            throttleStore.Record(new IngestionThrottleObservation(jobId, ClusterFor(jobId), start, end, Attempt: 3, ReportedCapacity: 18, observedAt, Terminal: true));
        }

        private string ClusterFor(string jobId) => catalog.Get(jobId)!.Definition.Target.ClusterUri;

        // Completed slices whose successful attempts feed the keep-up-floor duration estimate (sampled
        // over the 6h duration lookback, deliberately outside the 30m rate window).
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

        // Clean (successful) attempts inside the rate window, to swell the denominator without throttles.
        private void SeedInWindowSuccesses(string jobId, int count)
        {
            var completedAt = Now.AddMinutes(-5);
            for (var i = 0; i < count; i++)
            {
                var sliceStart = new DateTimeOffset(2026, 6, 23, 10, 0, 0, TimeSpan.Zero).AddMinutes(i);
                var sliceEnd = sliceStart.AddMinutes(1);
                state.Append($"{jobId}-win-state-{i}", jobId, sliceStart, sliceEnd, DurableSliceStatus.Completed, expectedVersion: 0);
                readModels.RecordAttempt($"{jobId}-win-succ-{i}", jobId, sliceStart, sliceEnd, 1, "Succeeded", "worker", completedAt.AddMinutes(-1), completedAt);
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
