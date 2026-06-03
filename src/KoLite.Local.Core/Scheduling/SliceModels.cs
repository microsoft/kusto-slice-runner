using System.Globalization;

namespace KoLite.Local.Core.Scheduling
{
    public readonly record struct SliceKey(string Value) : IComparable<SliceKey>
    {
        public static SliceKey Create(string activityId, DateTimeOffset startUtc, DateTimeOffset endUtc)
        {
            if (string.IsNullOrWhiteSpace(activityId)) throw new ArgumentException("Activity id is required.", nameof(activityId));
            if (endUtc <= startUtc) throw new ArgumentOutOfRangeException(nameof(endUtc), "Slice end must be after slice start.");
            return new SliceKey($"{activityId}|{Format(startUtc)}|{Format(endUtc)}");
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

    public sealed record SliceRange(string ActivityId, DateTimeOffset StartUtc, DateTimeOffset EndUtc)
    {
        public SliceKey ToKey() => SliceKey.Create(ActivityId, StartUtc, EndUtc);
        public bool Overlaps(DateTimeOffset startUtc, DateTimeOffset endUtc) => StartUtc < endUtc && EndUtc > startUtc;
    }
}
