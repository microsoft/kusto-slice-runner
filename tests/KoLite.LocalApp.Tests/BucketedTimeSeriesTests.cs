// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using KoLite.Local.Sqlite.Connections;
using KoLite.LocalApp.Ui;

namespace KoLite.LocalApp.Tests
{
    public sealed class BucketedTimeSeriesTests : IDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 9, 11, 16, 6, 1, TimeSpan.Zero);

        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "bucketed-time-series-tests", Guid.NewGuid().ToString("N"));
        private readonly BucketedTimeSeries timeSeries;

        public BucketedTimeSeriesTests()
        {
            Directory.CreateDirectory(testDirectory);
            var factory = new KoLiteSqliteConnectionFactory(
                new KoLiteSqliteConnectionOptions(Path.Combine(testDirectory, "time-series.db")));
            timeSeries = new BucketedTimeSeries(factory);
        }

        [Theory]
        [InlineData("01:00:00", "00:01:00", "2026-09-11T16:06:00Z", 60)]
        [InlineData("1.00:00:00", "01:00:00", "2026-09-11T16:00:00Z", 24)]
        [InlineData("7.00:00:00", "06:00:00", "2026-09-11T12:00:00Z", 28)]
        [InlineData("30.00:00:00", "1.00:00:00", "2026-09-11T00:00:00Z", 30)]
        public void Default_windows_contain_only_complete_aligned_buckets(
            string rangeText,
            string bucketText,
            string expectedUntilText,
            int expectedCount)
        {
            var range = TimeSpan.Parse(rangeText, CultureInfo.InvariantCulture);
            var expectedBucketSize = TimeSpan.Parse(bucketText, CultureInfo.InvariantCulture);
            var expectedUntil = DateTimeOffset.Parse(expectedUntilText, CultureInfo.InvariantCulture);

            var window = timeSeries.CreateWindow(Now, range);

            Assert.Equal(expectedBucketSize, window.BucketSize);
            Assert.Equal(expectedUntil, window.Until);
            Assert.Equal(expectedUntil - range, window.Since);
            Assert.Equal(expectedCount, window.Count);
            Assert.Equal(window.Since, window.Buckets[0]);
            Assert.Equal(window.Until - window.BucketSize, window.Buckets[^1]);
        }

        [Fact]
        public void Exact_boundary_is_not_rolled_back_one_bucket()
        {
            var now = new DateTimeOffset(2026, 9, 11, 16, 0, 0, TimeSpan.Zero);

            var window = timeSeries.CreateWindow(now, TimeSpan.FromDays(1));

            Assert.Equal(now, window.Until);
            Assert.Equal(now - TimeSpan.FromDays(1), window.Since);
            Assert.Equal(24, window.Count);
        }

        public void Dispose()
        {
            TestCleanup.DeleteDirectoryBestEffort(testDirectory);
        }
    }
}
