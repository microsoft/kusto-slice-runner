using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace KoLite.LocalApp.Retention
{
    // Configuration for the local database retention background service. Every value is optional and
    // falls back to a safe default; the defaults preserve all functional capability and the full
    // slice window-history while bounding the growth of operational telemetry.
    public sealed record LocalRetentionOptions(bool Enabled, TimeSpan Window, TimeSpan Interval, TimeSpan InitialDelay, int BatchSize)
    {
        public static TimeSpan DefaultWindow { get; } = TimeSpan.FromDays(30);
        public static TimeSpan DefaultInterval { get; } = TimeSpan.FromHours(6);
        public static TimeSpan DefaultInitialDelay { get; } = TimeSpan.FromMinutes(2);
        public const int DefaultBatchSize = 2000;

        // Chart-backing slice_attempts are never pruned more aggressively than the dashboard's
        // maximum selectable chart range, so the
        // 30-day charts always stay whole even when a shorter retention window is configured.
        public static TimeSpan MinProtectedWindow { get; } = TimeSpan.FromDays(30);

        public TimeSpan ProtectedWindow => Window > MinProtectedWindow ? Window : MinProtectedWindow;

        public static LocalRetentionOptions From(IConfiguration configuration)
        {
            var enabledText = configuration["KoLite:Retention:Enabled"];
            var enabled = string.IsNullOrWhiteSpace(enabledText) || bool.Parse(enabledText);

            var window = ReadPositiveDays(configuration, "KoLite:Retention:WindowDays", DefaultWindow);
            var interval = ReadPositiveTimeSpan(configuration, "KoLite:Retention:Interval", DefaultInterval);
            var initialDelay = ReadNonNegativeTimeSpan(configuration, "KoLite:Retention:InitialDelay", DefaultInitialDelay);

            var batchSize = DefaultBatchSize;
            var batchText = configuration["KoLite:Retention:BatchSize"];
            if (!string.IsNullOrWhiteSpace(batchText)
                && (!int.TryParse(batchText, NumberStyles.Integer, CultureInfo.InvariantCulture, out batchSize) || batchSize < 1))
            {
                throw new InvalidOperationException("KoLite:Retention:BatchSize must be a positive integer.");
            }

            return new LocalRetentionOptions(enabled, window, interval, initialDelay, batchSize);
        }

        private static TimeSpan ReadPositiveDays(IConfiguration configuration, string key, TimeSpan defaultValue)
        {
            var text = configuration[key];
            if (string.IsNullOrWhiteSpace(text)) return defaultValue;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var days) || days <= 0)
            {
                throw new InvalidOperationException($"{key} must be a positive number of days.");
            }

            return TimeSpan.FromDays(days);
        }

        private static TimeSpan ReadPositiveTimeSpan(IConfiguration configuration, string key, TimeSpan defaultValue)
        {
            var text = configuration[key];
            if (string.IsNullOrWhiteSpace(text)) return defaultValue;
            var value = TimeSpan.Parse(text, CultureInfo.InvariantCulture);
            if (value <= TimeSpan.Zero) throw new InvalidOperationException($"{key} must be greater than zero.");
            return value;
        }

        private static TimeSpan ReadNonNegativeTimeSpan(IConfiguration configuration, string key, TimeSpan defaultValue)
        {
            var text = configuration[key];
            if (string.IsNullOrWhiteSpace(text)) return defaultValue;
            var value = TimeSpan.Parse(text, CultureInfo.InvariantCulture);
            if (value < TimeSpan.Zero) throw new InvalidOperationException($"{key} must be zero or greater.");
            return value;
        }
    }
}
