// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using Ksr.Local.Sqlite.Connections;
using Ksr.LocalApp.Ui;

namespace Ksr.LocalApp.Tests
{
    public sealed class BucketedTimeSeriesTests : IDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 9, 11, 16, 6, 1, TimeSpan.Zero);

        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "bucketed-time-series-tests", Guid.NewGuid().ToString("N"));
        private readonly BucketedTimeSeries timeSeries;

        public BucketedTimeSeriesTests()
        {
            Directory.CreateDirectory(testDirectory);
            var factory = new KsrSqliteConnectionFactory(
                new KsrSqliteConnectionOptions(Path.Combine(testDirectory, "time-series.db")));
            timeSeries = new BucketedTimeSeries(factory);
        }

        [Theory]
        [InlineData("01:00:00", "00:01:00", "2026-09-11T16:06:00Z", 60)]
        [InlineData("1.00:00:00", "01:00:00", "2026-09-11T16:00:00Z", 24)]
        [InlineData("7.00:00:00", "06:00:00", "2026-09-11T12:00:00Z", 28)]
        [InlineData("30.00:00:00", "1.00:00:00", "2026-09-11T00:00:00Z", 30)]
        public void Default_windows_include_the_open_bucket_without_growing_the_range(
            string rangeText,
            string bucketText,
            string expectedCurrentStartText,
            int expectedCount)
        {
            var range = TimeSpan.Parse(rangeText, CultureInfo.InvariantCulture);
            var expectedBucketSize = TimeSpan.Parse(bucketText, CultureInfo.InvariantCulture);
            var expectedCurrentStart = DateTimeOffset.Parse(expectedCurrentStartText, CultureInfo.InvariantCulture);

            var window = timeSeries.CreateWindow(Now, range);

            Assert.Equal(expectedBucketSize, window.BucketSize);
            Assert.Equal(expectedCurrentStart.Add(expectedBucketSize), window.Until);
            Assert.Equal(window.Until - range, window.Since);
            Assert.Equal(expectedCount, window.Count);
            Assert.Equal(window.Since, window.Buckets[0]);
            Assert.Equal(expectedCurrentStart, window.Buckets[^1]);
            Assert.Equal(expectedCurrentStart, window.Timing.CompleteThroughUtc);
            Assert.Equal(expectedCurrentStart, window.Timing.CurrentBucketStartUtc);
            Assert.Equal(Now, window.Timing.AsOfUtc);
            Assert.Equal(expectedCount - 1, window.IndexOf(Now.AddTicks(-1)));
            Assert.Equal(-1, window.IndexOf(Now));
            Assert.Equal(-1, window.IndexOf(Now.AddTicks(1)));
            Assert.Equal(0, window.IndexOf(window.Since));
            Assert.Equal(-1, window.IndexOf(window.Since.AddTicks(-1)));
        }

        [Theory]
        [InlineData("01:00:00", "2026-09-11T16:06:00Z", 60)]
        [InlineData("1.00:00:00", "2026-09-11T16:00:00Z", 24)]
        [InlineData("7.00:00:00", "2026-09-11T12:00:00Z", 28)]
        [InlineData("30.00:00:00", "2026-09-11T00:00:00Z", 30)]
        public void Exact_boundary_is_not_rolled_back_one_bucket(string rangeText, string nowText, int expectedCount)
        {
            var now = DateTimeOffset.Parse(nowText, CultureInfo.InvariantCulture);
            var range = TimeSpan.Parse(rangeText, CultureInfo.InvariantCulture);

            var window = timeSeries.CreateWindow(now, range);

            Assert.Equal(now, window.Until);
            Assert.Equal(now - range, window.Since);
            Assert.Equal(expectedCount, window.Count);
            Assert.Equal(now, window.Timing.CompleteThroughUtc);
            Assert.Null(window.Timing.CurrentBucketStartUtc);
            Assert.Equal(expectedCount - 1, window.IndexOf(now.AddTicks(-1)));
            Assert.Equal(-1, window.IndexOf(now));
        }

        [Fact]
        public void Offset_clock_is_aligned_to_utc()
        {
            var now = new DateTimeOffset(2026, 9, 11, 10, 6, 1, TimeSpan.FromHours(-7));

            var window = timeSeries.CreateWindow(now, TimeSpan.FromDays(1));

            Assert.Equal(new DateTimeOffset(2026, 9, 11, 17, 0, 0, TimeSpan.Zero), window.Timing.CurrentBucketStartUtc);
            Assert.Equal(now.ToUniversalTime(), window.Timing.AsOfUtc);
            Assert.Equal(expected: TimeSpan.Zero, actual: window.Timing.AsOfUtc.Offset);
        }

        public void Dispose()
        {
            TestCleanup.DeleteDirectoryBestEffort(testDirectory);
        }
    }
}
