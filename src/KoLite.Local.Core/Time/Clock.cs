namespace KoLite.Local.Core.Time
{
    public interface IClock
    {
        DateTimeOffset UtcNow { get; }
    }

    public sealed class SystemClock : IClock
    {
        public static SystemClock Instance { get; } = new();
        private SystemClock() { }
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    public sealed class ManualClock : IClock
    {
        public ManualClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow.ToUniversalTime();
        }

        public DateTimeOffset UtcNow { get; private set; }
        public void Advance(TimeSpan duration) => UtcNow += duration;
        public void SetUtcNow(DateTimeOffset utcNow) => UtcNow = utcNow.ToUniversalTime();
    }
}
