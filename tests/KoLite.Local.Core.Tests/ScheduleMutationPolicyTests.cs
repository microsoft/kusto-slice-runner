using System.Globalization;
using KoLite.Local.Core.Schedules;

namespace KoLite.Local.Core.Tests
{
    public sealed class ScheduleMutationPolicyTests
    {
        [Fact]
        public void Started_job_rejects_activity_window_and_start_changes()
        {
            var current = Job("job.policy", "2026-01-01T00:00:00Z", TimeSpan.FromMinutes(5));
            var proposed = current with
            {
                ActivityId = "job.policy.renamed",
                QueryWindowSize = TimeSpan.FromMinutes(10),
                StartFrom = Utc("2026-01-01T00:05:00Z")
            };

            var violations = ScheduleMutationPolicy.ValidateUpdate(current, proposed, hasStarted: true);

            Assert.Equal(["activityId", "queryWindowSize", "startFrom"], violations.Select(v => v.Field).ToArray());
            Assert.All(violations, v => Assert.Contains("cannot change", v.Message, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Unstarted_job_allows_window_and_start_changes_but_not_activity_rename()
        {
            var current = Job("job.policy", "2026-01-01T00:00:00Z", TimeSpan.FromMinutes(5));
            var retimed = current with
            {
                QueryWindowSize = TimeSpan.FromMinutes(10),
                StartFrom = Utc("2026-01-01T00:05:00Z")
            };
            var renamed = retimed with { ActivityId = "job.policy.renamed" };

            Assert.Empty(ScheduleMutationPolicy.ValidateUpdate(current, retimed, hasStarted: false));

            var violations = ScheduleMutationPolicy.ValidateUpdate(current, renamed, hasStarted: false);
            Assert.Equal(["activityId"], violations.Select(v => v.Field).ToArray());
        }

        [Fact]
        public void Started_job_allows_non_protected_schedule_changes_under_selected_scope()
        {
            var current = Job("job.policy", "2026-01-01T00:00:00Z", TimeSpan.FromMinutes(5));
            var proposed = current with
            {
                FunctionName = "OtherFunction",
                OutputTable = "OtherOutput",
                DelayFromUtcNow = TimeSpan.FromMinutes(30),
                MaxParallelism = 5,
                QueryTimeout = TimeSpan.FromMinutes(10),
                IsPaused = true,
                EndOn = Utc("2026-01-02T00:00:00Z"),
                Folder = "Other/Folder",
                Tags = ["prod", "daily"],
                DependsOn = [new DependentJob { ActivityId = "upstream" }],
                Target = new JobTarget { ClusterUri = "https://other-cluster.invalid", Database = "OtherDb" }
            };

            var violations = ScheduleMutationPolicy.ValidateUpdate(current, proposed, hasStarted: true);

            Assert.Empty(violations);
        }

        private static JobDefinition Job(string activityId, string start, TimeSpan window) => new()
        {
            ActivityId = activityId,
            FunctionName = "PolicyFunction",
            OutputTable = "PolicyOutput",
            QueryWindowSize = window,
            DelayFromUtcNow = TimeSpan.Zero,
            MaxParallelism = 1,
            QueryTimeout = TimeSpan.FromMinutes(1),
            StartFrom = Utc(start),
            Target = new JobTarget { ClusterUri = "https://kolite-example.invalid", Database = "DemoDb" }
        };

        private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToUniversalTime();
    }
}
