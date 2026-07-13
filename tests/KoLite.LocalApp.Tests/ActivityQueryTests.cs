using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Schema;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.State;
using KoLite.LocalApp.Ui;

namespace KoLite.LocalApp.Tests
{
    public sealed class ActivityQueryTests : IDisposable
    {
        private const string Cluster = "https://kolite-activity.invalid";

        // Fixed "now" well past the 2026-01-01 slice base so trailing 1d/7d/30d windows are positive.
        private static readonly DateTimeOffset Now = At(60 * 24 * 60);

        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "activity-query-tests", Guid.NewGuid().ToString("N"));
        private readonly KoLiteSqliteConnectionFactory factory;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteSliceStateRepository state;
        private readonly SqliteOperationalReadModelRepository readModels;
        private readonly SqliteDiagnosticsReadModelRepository diagnostics;

        public ActivityQueryTests()
        {
            Directory.CreateDirectory(testDirectory);
            factory = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(Path.Combine(testDirectory, "activity.db")) { BusyTimeoutMilliseconds = 10_000 });
            new KoLiteSqliteSchema(factory).EnsureSchema();
            catalog = new SqliteJobCatalogRepository(factory);
            state = new SqliteSliceStateRepository(factory);
            readModels = new SqliteOperationalReadModelRepository(factory);
            diagnostics = new SqliteDiagnosticsReadModelRepository(factory);
        }

        public void Dispose()
        {
            TestCleanup.DeleteDirectoryBestEffort(testDirectory);
        }

        [Fact]
        public void Aggregates_running_all_time_windowed_and_chart_counts()
        {
            var jobId = catalog.Create(Schedule("activity.job")).JobId;

            // All-time outcome per slice (from current_slice_state, never pruned).
            SeedSlice(jobId, At(0), DurableSliceStatus.Completed);
            SeedSlice(jobId, At(5), DurableSliceStatus.Completed);
            SeedSlice(jobId, At(10), DurableSliceStatus.Failed);
            SeedSlice(jobId, At(15), DurableSliceStatus.DeadLettered);
            SeedSlice(jobId, At(20), DurableSliceStatus.Running);
            SeedSlice(jobId, At(25), DurableSliceStatus.Queued);
            // A completed slice whose only success attempt is older than 30 days: it counts toward the
            // all-time total but must be excluded from the attempt-based 1d/7d/30d windows and chart.
            SeedSlice(jobId, At(30), DurableSliceStatus.Completed);

            // Recent attempt completions (within the last day) drive the windows and the chart.
            RecordAttempt(jobId, At(0), "Succeeded", Now.AddMinutes(-5));
            RecordAttempt(jobId, At(5), "Succeeded", Now.AddMinutes(-10));
            RecordAttempt(jobId, At(10), "Failed", Now.AddMinutes(-15));
            RecordAttempt(jobId, At(15), "DeadLettered", Now.AddMinutes(-20));
            RecordAttempt(jobId, At(30), "Succeeded", Now.AddDays(-40));

            var query = new ActivityQuery(factory, new ManualClock(Now), readModels, diagnostics);
            var data = query.GetActivity(TimeSpan.FromDays(1));

            Assert.Equal(1, data.RunningNow.RunningCount);
            Assert.Equal(1, data.RunningNow.QueuedCount);
            Assert.Single(data.RunningNow.RunningSlices);
            Assert.Equal("activity.job", data.RunningNow.RunningSlices[0].Slice.ActivityId);
            // No in-flight Started attempt was recorded, so the start falls back to the last state change.
            Assert.NotEqual(default, data.RunningNow.RunningSlices[0].StartedAtUtc);

            // All-time: Completed = 3 (includes the 40-day-old one); Failed + DeadLettered = 2.
            Assert.Equal(3, data.AllTime.Succeeded);
            Assert.Equal(2, data.AllTime.Failed);

            // Windows are attempt-based and exclude the 40-day-old success.
            Assert.Equal(2, data.LastDay.Succeeded);
            Assert.Equal(2, data.LastDay.Failed);
            Assert.Equal(2, data.Last7Days.Succeeded);
            Assert.Equal(2, data.Last7Days.Failed);
            Assert.Equal(2, data.Last30Days.Succeeded);
            Assert.Equal(2, data.Last30Days.Failed);

            Assert.True(data.Chart.HasData);
            Assert.Equal(2, data.Chart.Points.Sum(p => p.SucceededCount));
            Assert.Equal(2, data.Chart.Points.Sum(p => p.FailedCount));
        }

        [Fact]
        public void Reports_zeroes_for_an_empty_store()
        {
            catalog.Create(Schedule("activity.empty"));

            var query = new ActivityQuery(factory, new ManualClock(Now), readModels, diagnostics);
            var data = query.GetActivity(TimeSpan.FromDays(1));

            Assert.Equal(0, data.RunningNow.RunningCount);
            Assert.Equal(0, data.RunningNow.QueuedCount);
            Assert.Empty(data.RunningNow.RunningSlices);
            Assert.Equal(0, data.AllTime.Total);
            Assert.Equal(0, data.LastDay.Total);
            Assert.Equal(0, data.Last7Days.Total);
            Assert.Equal(0, data.Last30Days.Total);
            Assert.False(data.Chart.HasData);
        }

        [Fact]
        public void Running_slices_expose_start_time_and_median_eta()
        {
            var jobId = catalog.Create(Schedule("eta.job")).JobId;

            // Prior successful runs (each needs a slice-state row for the attempt foreign key).
            // Durations 6, 20, 10 minutes -> median 10 minutes.
            SeedSlice(jobId, At(0), DurableSliceStatus.Completed);
            SeedSlice(jobId, At(5), DurableSliceStatus.Completed);
            SeedSlice(jobId, At(10), DurableSliceStatus.Completed);
            RecordSucceeded(jobId, At(0), Now.AddHours(-3), TimeSpan.FromMinutes(6));
            RecordSucceeded(jobId, At(5), Now.AddHours(-2), TimeSpan.FromMinutes(20));
            RecordSucceeded(jobId, At(10), Now.AddHours(-1), TimeSpan.FromMinutes(10));

            // The in-flight slice with a Started attempt.
            var runningStart = At(100);
            SeedSlice(jobId, runningStart, DurableSliceStatus.Running);
            var startedAt = Now.AddMinutes(-4);
            RecordStarted(jobId, runningStart, startedAt);

            var query = new ActivityQuery(factory, new ManualClock(Now), readModels, diagnostics);
            var data = query.GetActivity(TimeSpan.FromDays(1));

            var view = Assert.Single(data.RunningNow.RunningSlices);
            Assert.Equal(startedAt, view.StartedAtUtc);
            Assert.Equal(startedAt + TimeSpan.FromMinutes(10), view.EtaUtc);
            Assert.Equal(Now, data.GeneratedAtUtc);
        }

        [Fact]
        public void Running_slice_without_successful_history_has_no_eta()
        {
            var jobId = catalog.Create(Schedule("eta.nohistory")).JobId;
            var runningStart = At(100);
            SeedSlice(jobId, runningStart, DurableSliceStatus.Running);
            var startedAt = Now.AddMinutes(-2);
            RecordStarted(jobId, runningStart, startedAt);

            var query = new ActivityQuery(factory, new ManualClock(Now), readModels, diagnostics);
            var data = query.GetActivity(TimeSpan.FromDays(1));

            var view = Assert.Single(data.RunningNow.RunningSlices);
            Assert.Equal(startedAt, view.StartedAtUtc);
            Assert.Null(view.EtaUtc);
        }

        private void SeedSlice(string jobId, DateTimeOffset sliceStart, DurableSliceStatus status)
        {
            var sliceEnd = sliceStart.AddMinutes(5);
            state.Append($"{jobId}-st-{sliceStart.Ticks}", jobId, sliceStart, sliceEnd, status, expectedVersion: 0);
        }

        private void RecordAttempt(string jobId, DateTimeOffset sliceStart, string status, DateTimeOffset completedAt)
        {
            var sliceEnd = sliceStart.AddMinutes(5);
            readModels.RecordAttempt($"{jobId}-att-{sliceStart.Ticks}", jobId, sliceStart, sliceEnd, 1, status, "worker", completedAt.AddMinutes(-1), completedAt);
        }

        // In-flight attempt: started, not yet completed (drives the running slice's StartedAtUtc).
        private void RecordStarted(string jobId, DateTimeOffset sliceStart, DateTimeOffset startedAt)
        {
            var sliceEnd = sliceStart.AddMinutes(5);
            readModels.RecordAttempt($"{jobId}-att-{sliceStart.Ticks}", jobId, sliceStart, sliceEnd, 1, "Started", "worker", startedAt, completedAtUtc: null);
        }

        // A completed successful attempt with an explicit wall-clock duration (drives the ETA median).
        private void RecordSucceeded(string jobId, DateTimeOffset sliceStart, DateTimeOffset completedAt, TimeSpan duration)
        {
            var sliceEnd = sliceStart.AddMinutes(5);
            readModels.RecordAttempt($"{jobId}-att-{sliceStart.Ticks}", jobId, sliceStart, sliceEnd, 1, "Succeeded", "worker", completedAt - duration, completedAt);
        }

        private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        private static string JobId(string activityId)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(activityId));
            return new Guid(bytes).ToString("N");
        }

        private static string Schedule(string activityId) => $$"""
        {
          "id": "{{JobId(activityId)}}",
          "activityId": "{{activityId}}",
          "functionName": "ActivityFunction",
          "outputTable": "Output",
          "queryWindowSize": "00:05:00",
          "delayFromUtcNow": "00:00:00",
          "maxParallelism": 1,
          "queryTimeout": "00:01:00",
          "isPaused": false,
          "startFrom": "2026-01-01T00:00:00Z",
          "target": { "clusterUri": "{{Cluster}}", "database": "DemoDb" }
        }
        """;
    }
}
