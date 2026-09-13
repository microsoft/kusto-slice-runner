using System.Text.Json;
using KoLite.Local.Sqlite.Observability;
using KoLite.LocalApp.Retention;

namespace KoLite.LocalApp.Tests
{
    public sealed class RetentionSnapshotTests
    {
        [Fact]
        public void Performance_facts_are_included_in_attempt_telemetry_subtotal_without_new_status_fields()
        {
            var result = new RetentionCleanupResult("run", 1, 2, 3, 4, 5) { PerformanceAttemptsDeleted = 6 };
            var now = DateTimeOffset.UtcNow;

            var snapshot = RetentionSnapshot.Completed(now, result);

            Assert.Equal(8, snapshot.AttemptsDeleted);
            Assert.Equal(21, snapshot.TotalDeleted);
            Assert.Equal(snapshot.LogsDeleted + snapshot.AttemptsDeleted + snapshot.ScheduledSlicesDeleted
                + snapshot.IngestionThrottlesDeleted + snapshot.QueueRowsDeleted, snapshot.TotalDeleted);
            Assert.Equal(now, snapshot.LastRunUtc);
            Assert.Null(snapshot.LastError);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(snapshot));
            Assert.Equal(9, json.RootElement.EnumerateObject().Count());
            Assert.False(json.RootElement.TryGetProperty("PerformanceAttemptsDeleted", out _));
        }
    }
}
