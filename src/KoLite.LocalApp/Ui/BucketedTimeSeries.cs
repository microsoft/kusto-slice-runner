using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace KoLite.LocalApp.Ui
{
    // A half-open time window [Since, Until) split into fixed-size buckets.
    // Centralizes the bucket-index arithmetic shared by the chart builders so the
    // windowing math has a single source of truth.
    internal sealed record BucketWindow(
        DateTimeOffset Since,
        DateTimeOffset Until,
        TimeSpan BucketSize,
        IReadOnlyList<DateTimeOffset> Buckets)
    {
        public int Count => Buckets.Count;

        // Whole-second bucket width used by the SQL `unixepoch` bucketing path.
        public long BucketSeconds => Math.Max(1L, (long)BucketSize.TotalSeconds);

        // Index of the bucket containing <paramref name="time"/>, or -1 when it
        // falls outside the window. Matches the in-memory tick arithmetic the chart
        // builders previously duplicated.
        public int IndexOf(DateTimeOffset time)
        {
            var index = (int)((time.ToUniversalTime() - Since).Ticks / BucketSize.Ticks);
            return index < 0 || index >= Buckets.Count ? -1 : index;
        }
    }

    // Builds bucketed time windows and runs windowed queries over a SQLite table,
    // capturing the bucket scaffolding and the connection/command/reader boilerplate
    // shared by the four chart readers.
    internal sealed class BucketedTimeSeries
    {
        private readonly IKoLiteSqliteConnectionFactory connectionFactory;

        public BucketedTimeSeries(IKoLiteSqliteConnectionFactory connectionFactory)
        {
            this.connectionFactory = connectionFactory;
        }

        // Aligns a [now - range, now) span to bucket boundaries and enumerates the buckets.
        public BucketWindow CreateWindow(DateTimeOffset now, TimeSpan range)
        {
            return CreateWindow(now, range, BucketSizeFor(range));
        }

        // As above, but with an explicit bucket size (e.g. for the throttle-severity chart, which
        // wants finer-than-default resolution over a multi-hour range).
        public BucketWindow CreateWindow(DateTimeOffset now, TimeSpan range, TimeSpan bucketSize)
        {
            var size = bucketSize > TimeSpan.Zero ? bucketSize : BucketSizeFor(range);
            var until = AlignUp(now, size);
            var since = AlignDown(now.Subtract(range), size);
            return new BucketWindow(since, until, size, EnumerateBuckets(since, until, size));
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
            command.Add("$until", SqliteStorage.Utc(window.Until));
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

        private static DateTimeOffset AlignUp(DateTimeOffset value, TimeSpan bucketSize)
        {
            var down = AlignDown(value, bucketSize);
            return down == value.ToUniversalTime() ? down : down.Add(bucketSize);
        }
    }
}
