// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Sqlite.Connections;
using Ksr.Local.Sqlite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Ksr.LocalApp.Ui
{
    public sealed record ChartWindowTiming(
        DateTimeOffset AsOfUtc,
        DateTimeOffset CompleteThroughUtc,
        DateTimeOffset? CurrentBucketStartUtc);

    // A half-open time window [Since, Until) split into fixed-size buckets.
    // Centralizes the bucket-index arithmetic shared by the chart builders so the
    // windowing math has a single source of truth.
    internal sealed record BucketWindow(
        DateTimeOffset Since,
        DateTimeOffset Until,
        TimeSpan BucketSize,
        IReadOnlyList<DateTimeOffset> Buckets,
        ChartWindowTiming Timing)
    {
        public int Count => Buckets.Count;

        // Whole-second bucket width used by the SQL `unixepoch` bucketing path.
        public long BucketSeconds => Math.Max(1L, (long)BucketSize.TotalSeconds);

        // Index of the bucket containing <paramref name="time"/>, or -1 when it
        // falls outside the window. Matches the in-memory tick arithmetic the chart
        // builders previously duplicated.
        public int IndexOf(DateTimeOffset time)
        {
            var utc = time.ToUniversalTime();
            if (utc < Since || utc >= Timing.AsOfUtc)
            {
                return -1;
            }

            var index = (int)((utc - Since).Ticks / BucketSize.Ticks);
            return index < 0 || index >= Buckets.Count ? -1 : index;
        }
    }

    // Builds bucketed time windows and runs windowed queries over a SQLite table,
    // capturing the bucket scaffolding and the connection/command/reader boilerplate
    // shared by the four chart readers.
    internal sealed class BucketedTimeSeries
    {
        private readonly IKsrSqliteConnectionFactory connectionFactory;

        public BucketedTimeSeries(IKsrSqliteConnectionFactory connectionFactory)
        {
            this.connectionFactory = connectionFactory;
        }

        // Keeps the selected number of UTC-aligned buckets, including the current partial bucket
        // when the as-of time falls between boundaries.
        public BucketWindow CreateWindow(DateTimeOffset now, TimeSpan range)
        {
            if (range <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(range), "The chart range must be positive.");
            }

            var size = BucketSizeFor(range);
            if (range.Ticks % size.Ticks != 0)
            {
                throw new ArgumentException("The chart range must contain a whole number of buckets.", nameof(range));
            }

            var asOfUtc = now.ToUniversalTime();
            var completeThroughUtc = AlignDown(asOfUtc, size);
            var currentBucketStartUtc = asOfUtc > completeThroughUtc ? completeThroughUtc : (DateTimeOffset?)null;
            var until = currentBucketStartUtc is null ? completeThroughUtc : completeThroughUtc.Add(size);
            var since = until.Subtract(range);
            return new BucketWindow(
                since,
                until,
                size,
                EnumerateBuckets(since, until, size),
                new ChartWindowTiming(asOfUtc, completeThroughUtc, currentBucketStartUtc));
        }

        // Executes a windowed query. The `$since`/`$until` parameters are bound here;
        // callers supply the SQL (table/select/group-by/filter), bind any extra
        // parameters, and project each row.
        public IReadOnlyList<T> ReadWindow<T>(
            string commandText,
            BucketWindow window,
            Action<SqliteCommand>? bindParameters,
            Func<SqliteDataReader, T> readRow)
        {
            using var connection = connectionFactory.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = commandText;
            command.Add("$since", SqliteStorage.Utc(window.Since));
            command.Add("$until", SqliteStorage.Utc(window.Timing.AsOfUtc));
            bindParameters?.Invoke(command);
            using var reader = command.ExecuteReader();
            var results = new List<T>();
            while (reader.Read())
            {
                results.Add(readRow(reader));
            }

            return results;
        }

        private static IReadOnlyList<DateTimeOffset> EnumerateBuckets(DateTimeOffset since, DateTimeOffset until, TimeSpan bucketSize)
        {
            var buckets = new List<DateTimeOffset>();
            for (var bucket = since; bucket < until; bucket = bucket.Add(bucketSize))
            {
                buckets.Add(bucket);
            }

            return buckets;
        }

        private static TimeSpan BucketSizeFor(TimeSpan range)
        {
            if (range <= TimeSpan.FromHours(1)) return TimeSpan.FromMinutes(1);
            if (range <= TimeSpan.FromDays(1)) return TimeSpan.FromHours(1);
            if (range <= TimeSpan.FromDays(7)) return TimeSpan.FromHours(6);
            return TimeSpan.FromDays(1);
        }

        private static DateTimeOffset AlignDown(DateTimeOffset value, TimeSpan bucketSize)
        {
            var utc = value.ToUniversalTime();
            return new DateTimeOffset(utc.Ticks - utc.Ticks % bucketSize.Ticks, TimeSpan.Zero);
        }
    }
}
