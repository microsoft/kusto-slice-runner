using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Schema;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.State;
using KoLite.LocalApp.Ui;

namespace KoLite.LocalApp.Tests
{
    public sealed class JobChartQueryTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "job-chart-query-tests", Guid.NewGuid().ToString("N"));
        private readonly KoLiteSqliteConnectionFactory factory;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteSliceStateRepository state;
        private readonly SqliteOperationalReadModelRepository readModels;

        public JobChartQueryTests()
        {
            Directory.CreateDirectory(testDirectory);
            factory = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(Path.Combine(testDirectory, "charts.db")) { BusyTimeoutMilliseconds = 10_000 });
            new KoLiteSqliteSchema(factory).EnsureSchema();
            catalog = new SqliteJobCatalogRepository(factory);
            state = new SqliteSliceStateRepository(factory);
            readModels = new SqliteOperationalReadModelRepository(factory);
        }

        [Fact]
        public void Retry_success_counts_as_half_attempt_success_and_full_final_success()
        {
            catalog.Create(Schedule("job.chart"));
            state.Append("queued-chart", JobId("job.chart"), At(60), At(65), DurableSliceStatus.Queued, expectedVersion: 0);
            var firstLease = state.AcquireLease("lease-chart-1", JobId("job.chart"), At(60), At(65), "worker", TimeSpan.FromMinutes(10), At(66));
            Assert.NotNull(firstLease);
            Assert.True(state.FailLease("fail-chart-1", JobId("job.chart"), At(60), At(65), "worker", firstLease.LeaseToken!, At(67), "transient"));
            var retryLease = state.AcquireLease("lease-chart-2", JobId("job.chart"), At(60), At(65), "worker", TimeSpan.FromMinutes(10), At(68));
            Assert.NotNull(retryLease);
            Assert.True(state.CompleteLease("complete-chart-2", JobId("job.chart"), At(60), At(65), "worker", retryLease.LeaseToken!, At(69)));
            readModels.RecordAttempt("attempt-chart-1", JobId("job.chart"), At(60), At(65), 1, "FailedRetryable", "worker", At(66), At(67), "Transient", "try again");
            readModels.RecordAttempt("attempt-chart-2", JobId("job.chart"), At(60), At(65), 2, "Succeeded", "worker", At(68), At(69));
            var query = new JobChartQuery(factory, new ManualClock(At(120)));

            var charts = query.GetDashboardCharts(TimeSpan.FromDays(1));
            var attemptPoint = charts.FirstAttemptSuccess.Series.Single(s => s.JobId == JobId("job.chart")).Points.Single(p => p.Denominator == 2);
            var finalPoint = charts.SuccessAfterRetries.Series.Single(s => s.JobId == JobId("job.chart")).Points.Single(p => p.Denominator == 1);

            // The series correlates by GUID but is labelled with the friendly activityId.
            Assert.Equal("job.chart", charts.FirstAttemptSuccess.Series.Single(s => s.JobId == JobId("job.chart")).Name);

            Assert.Equal(1, attemptPoint.Numerator);
            Assert.Equal(50.0, attemptPoint.Percent);
            Assert.Equal(1, finalPoint.Numerator);
            Assert.Equal(100.0, finalPoint.Percent);
        }

        [Fact]
        public void Chart_bucket_labels_are_iso_utc()
        {
            var bucketStart = new DateTimeOffset(2026, 8, 19, 22, 0, 0, TimeSpan.FromHours(-7));

            Assert.Equal("2026-08-20T05:00:00Z", new SuccessRatePoint(bucketStart, 1, 1).BucketLabel);
            Assert.Equal("2026-08-20T05:00:00Z", new JobAttemptResultPoint(bucketStart, 1, 0, 0).BucketLabel);
            Assert.Equal("2026-08-20T05:00:00Z", new JobSuccessfulDurationPoint(bucketStart, 1, 0, 1000).BucketLabel);
        }

        [Fact]
        public void Dashboard_charts_can_be_limited_to_selected_jobs()
        {
            catalog.Create(Schedule("job.chart.selected"));
            catalog.Create(Schedule("job.chart.other"));
            state.Append("slice-selected", JobId("job.chart.selected"), At(60), At(65), DurableSliceStatus.Completed, expectedVersion: 0);
            state.Append("slice-other", JobId("job.chart.other"), At(60), At(65), DurableSliceStatus.Completed, expectedVersion: 0);
            readModels.RecordAttempt("attempt-selected", JobId("job.chart.selected"), At(60), At(65), 1, "Succeeded", "worker", At(60), At(61));
            readModels.RecordAttempt("attempt-other", JobId("job.chart.other"), At(60), At(65), 1, "Succeeded", "worker", At(60), At(61));
            var query = new JobChartQuery(factory, new ManualClock(At(120)));

            var charts = query.GetDashboardCharts(TimeSpan.FromDays(1), [JobId("job.chart.selected")]);

            Assert.Equal([JobId("job.chart.selected")], charts.FirstAttemptSuccess.Series.Select(series => series.JobId).ToArray());
            Assert.Equal([JobId("job.chart.selected")], charts.SuccessAfterRetries.Series.Select(series => series.JobId).ToArray());
            Assert.Equal(["job.chart.selected"], charts.FirstAttemptSuccess.Series.Select(series => series.Name).ToArray());
            Assert.True(charts.FirstAttemptSuccess.HasData);
        }

        [Theory]
        [InlineData("Succeeded", JobAttemptResultBucket.Success)]
        [InlineData("FailedRetryable", JobAttemptResultBucket.Retry)]
        [InlineData("Failed", JobAttemptResultBucket.Error)]
        [InlineData("DeadLettered", JobAttemptResultBucket.Error)]
        [InlineData("LeaseLost", JobAttemptResultBucket.Error)]
        [InlineData("Started", JobAttemptResultBucket.Ignore)]
        [InlineData("Unexpected", JobAttemptResultBucket.Ignore)]
        public void Attempt_status_taxonomy_maps_chart_buckets(string status, JobAttemptResultBucket expected)
        {
            Assert.Equal(expected, JobAttemptStatusTaxonomy.BucketFor(status));
        }

        [Fact]
        public void Per_job_result_chart_counts_status_buckets_and_ignores_non_terminal_rows()
        {
            catalog.Create(Schedule("job.chart"));
            catalog.Create(Schedule("job.other"));
            state.Append("result-slice", JobId("job.chart"), At(60), At(65), DurableSliceStatus.Completed, expectedVersion: 0);
            state.Append("other-result-slice", JobId("job.other"), At(60), At(65), DurableSliceStatus.Completed, expectedVersion: 0);
            readModels.RecordAttempt("result-success", JobId("job.chart"), At(60), At(65), 1, "Succeeded", "worker", At(60), At(61));
            readModels.RecordAttempt("result-retry", JobId("job.chart"), At(60), At(65), 2, "FailedRetryable", "worker", At(61), At(62));
            readModels.RecordAttempt("result-failed", JobId("job.chart"), At(60), At(65), 3, "Failed", "worker", At(62), At(63));
            readModels.RecordAttempt("result-deadletter", JobId("job.chart"), At(60), At(65), 4, "DeadLettered", "worker", At(63), At(64));
            readModels.RecordAttempt("result-lease-lost", JobId("job.chart"), At(60), At(65), 5, "LeaseLost", "worker", At(64), At(65));
            readModels.RecordAttempt("result-started", JobId("job.chart"), At(60), At(65), 6, "Started", "worker", At(65), null);
            readModels.RecordAttempt("result-unknown", JobId("job.chart"), At(60), At(65), 7, "Unexpected", "worker", At(65), At(66));
            readModels.RecordAttempt("other-success", JobId("job.other"), At(60), At(65), 1, "Succeeded", "worker", At(60), At(61));
            var query = new JobChartQuery(factory, new ManualClock(At(120)));

            var charts = query.GetJobDetailsCharts(JobId("job.chart"), TimeSpan.FromDays(1));
            var point = charts.AttemptResults.Points.Single(p => p.TotalCount > 0);

            Assert.Equal("Query Results by Time of Execution", charts.AttemptResults.Title);
            Assert.True(charts.AttemptResults.HasData);
            Assert.Equal(1, point.SuccessCount);
            Assert.Equal(1, point.RetryCount);
            Assert.Equal(3, point.ErrorCount);
            Assert.Equal(5, point.TotalCount);
        }

        [Fact]
        public void Per_job_duration_chart_uses_metrics_then_started_completed_fallback()
        {
            catalog.Create(Schedule("job.chart"));
            state.Append("duration-slice", JobId("job.chart"), At(60), At(65), DurableSliceStatus.Completed, expectedVersion: 0);
            readModels.RecordAttempt("duration-metric", JobId("job.chart"), At(60), At(65), 1, "Succeeded", "worker", At(60), At(65), metricsJson: "{\"queryDurationMs\":120000}");
            readModels.RecordAttempt("duration-fallback", JobId("job.chart"), At(60), At(65), 2, "Started", "worker", At(70), null);
            readModels.RecordAttempt("duration-fallback", JobId("job.chart"), At(60), At(65), 2, "Succeeded", "worker", null, At(75));
            readModels.RecordAttempt("duration-retry", JobId("job.chart"), At(60), At(65), 3, "FailedRetryable", "worker", At(75), At(76));
            var query = new JobChartQuery(factory, new ManualClock(At(120)));

            var charts = query.GetJobDetailsCharts(JobId("job.chart"), TimeSpan.FromDays(1));
            var point = charts.SuccessfulDurations.Points.Single(p => p.Count > 0);

            Assert.Equal("Successful Query Duration by Time of Execution", charts.SuccessfulDurations.Title);
            Assert.True(charts.SuccessfulDurations.HasData);
            Assert.Equal(1, charts.SuccessfulDurations.MetricsDurationCount);
            Assert.Equal(1, charts.SuccessfulDurations.AttemptDurationFallbackCount);
            Assert.Equal(0, charts.SuccessfulDurations.RecoveredDurationCount);
            Assert.Equal(0, charts.SuccessfulDurations.MissingDurationCount);
            Assert.Equal(2, point.Count);
            Assert.Equal(0, point.MissingDurationCount);
            Assert.Equal(210000, point.AverageDurationMilliseconds);
            Assert.Equal("00:03:30", point.AverageDurationText);
        }

        [Fact]
        public void Per_job_duration_chart_recovers_started_time_from_running_state_events_and_counts_missing_samples()
        {
            catalog.Create(Schedule("job.chart"));
            var recoveredLease = state.AcquireLease("recover-running", JobId("job.chart"), At(60), At(65), "worker", TimeSpan.FromMinutes(10), At(70));
            Assert.NotNull(recoveredLease);
            Assert.True(state.CompleteLease("recover-complete", JobId("job.chart"), At(60), At(65), "worker", recoveredLease.LeaseToken!, At(75)));
            readModels.RecordAttempt("duration-recovered", JobId("job.chart"), At(60), At(65), 1, "Succeeded", "worker", null, At(75));
            state.Append("missing-duration-complete", JobId("job.chart"), At(80), At(85), DurableSliceStatus.Completed, expectedVersion: 0);
            readModels.RecordAttempt("duration-missing", JobId("job.chart"), At(80), At(85), 1, "Succeeded", "worker", null, At(85));
            var query = new JobChartQuery(factory, new ManualClock(At(120)));

            var charts = query.GetJobDetailsCharts(JobId("job.chart"), TimeSpan.FromDays(1));
            var point = charts.SuccessfulDurations.Points.Single(p => p.Count > 0 || p.MissingDurationCount > 0);

            Assert.True(charts.SuccessfulDurations.HasData);
            Assert.Equal(0, charts.SuccessfulDurations.MetricsDurationCount);
            Assert.Equal(0, charts.SuccessfulDurations.AttemptDurationFallbackCount);
            Assert.Equal(1, charts.SuccessfulDurations.RecoveredDurationCount);
            Assert.Equal(1, charts.SuccessfulDurations.MissingDurationCount);
            Assert.Equal(2, charts.SuccessfulDurations.TotalSuccessfulCount);
            Assert.Equal(1, point.Count);
            Assert.Equal(1, point.MissingDurationCount);
            Assert.Equal(300000, point.AverageDurationMilliseconds);
        }

        [Fact]
        public void Per_job_charts_return_empty_points_for_empty_ranges()
        {
            catalog.Create(Schedule("job.chart"));
            var query = new JobChartQuery(factory, new ManualClock(At(120)));

            var charts = query.GetJobDetailsCharts(JobId("job.chart"), TimeSpan.FromHours(1));

            Assert.False(charts.AttemptResults.HasData);
            Assert.False(charts.SuccessfulDurations.HasData);
            Assert.All(charts.AttemptResults.Points, point => Assert.Equal(0, point.TotalCount));
            Assert.All(charts.SuccessfulDurations.Points, point => Assert.Equal(0, point.Count));
        }

        public void Dispose()
        {
            TestCleanup.DeleteDirectoryBestEffort(testDirectory);
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
          "functionName": "ChartFunction",
          "outputTable": "ChartOutput",
          "queryWindowSize": "00:05:00",
          "delayFromUtcNow": "00:00:00",
          "maxParallelism": 2,
          "queryTimeout": "00:01:00",
          "isPaused": false,
          "startFrom": "2026-01-01T00:00:00Z",
          "target": { "clusterUri": "https://kolite-example.invalid", "database": "DemoDb" }
        }
        """;
    }
}
