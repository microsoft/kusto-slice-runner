// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using Ksr.Local.Core.Schedules;

namespace Ksr.Local.Core.Scheduling
{
    public readonly record struct SliceKey(string Value) : IComparable<SliceKey>
    {
        public static SliceKey Create(string jobId, DateTimeOffset startUtc, DateTimeOffset endUtc)
        {
            if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("Job id is required.", nameof(jobId));
            if (endUtc <= startUtc) throw new ArgumentOutOfRangeException(nameof(endUtc), "Slice end must be after slice start.");
            return new SliceKey($"{jobId}|{Format(startUtc)}|{Format(endUtc)}");
        }

        public static bool TryParse(string value, out SliceKey key, out SliceRange range)
        {
            key = default;
            range = default!;
            var parts = value.Split('|');
            if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[0]) ||
                !DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start) ||
                !DateTimeOffset.TryParse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var end) ||
                end <= start)
            {
                return false;
            }

            key = new SliceKey(value);
            range = new SliceRange(parts[0], start, end);
            return true;
        }

        public int CompareTo(SliceKey other) => string.CompareOrdinal(Value, other.Value);
        public override string ToString() => Value;

        private static string Format(DateTimeOffset value) => value.ToUniversalTime().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
    }

    public sealed record SliceRange(string JobId, DateTimeOffset StartUtc, DateTimeOffset EndUtc)
    {
        public SliceKey ToKey() => SliceKey.Create(JobId, StartUtc, EndUtc);
        public bool Overlaps(DateTimeOffset startUtc, DateTimeOffset endUtc) => StartUtc < endUtc && EndUtc > startUtc;
    }

    public sealed record SliceExecutionUnit
    {
        public SliceExecutionUnit(SliceRange slice, int? chunkId = null, int? totalChunks = null)
        {
            Slice = slice ?? throw new ArgumentNullException(nameof(slice));
            if (chunkId.HasValue != totalChunks.HasValue)
            {
                throw new ArgumentException("Chunk id and total chunks must either both be present or both be absent.");
            }

            if (totalChunks is { } count
                && (count < JobChunks.MinCount
                    || count > JobChunks.MaxCount
                    || chunkId < 0
                    || chunkId >= count))
            {
                throw new ArgumentOutOfRangeException(nameof(chunkId), $"Chunk id must be between 0 and total chunks - 1, and total chunks must be between {JobChunks.MinCount} and {JobChunks.MaxCount}.");
            }

            ChunkId = chunkId;
            TotalChunks = totalChunks;
        }

        public SliceRange Slice { get; }
        public int? ChunkId { get; }
        public int? TotalChunks { get; }
        public bool IsChunked => ChunkId.HasValue;

        public string ExecutionKey => IsChunked
            ? string.Create(CultureInfo.InvariantCulture, $"{Slice.ToKey().Value}|chunk|{ChunkId!.Value}|{TotalChunks!.Value}")
            : Slice.ToKey().Value;

        public static SliceExecutionUnit Unchunked(SliceRange slice) => new(slice);

        public static SliceExecutionUnit Chunk(SliceRange slice, int chunkId, int totalChunks) => new(slice, chunkId, totalChunks);
    }
}
