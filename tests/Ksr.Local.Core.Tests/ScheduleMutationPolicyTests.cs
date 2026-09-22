// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using Ksr.Local.Core.Schedules;

namespace Ksr.Local.Core.Tests
{
    public sealed class ScheduleMutationPolicyTests
    {
        [Fact]
        public void Started_job_allows_rename_but_rejects_window_start_and_chunk_changes()
        {
            var current = Job("job.policy", "2026-01-01T00:00:00Z", TimeSpan.FromMinutes(5));
            var proposed = current with
            {
                ActivityId = "job.policy.renamed",
                QueryWindowSize = TimeSpan.FromMinutes(10),
                StartFrom = Utc("2026-01-01T00:05:00Z"),
                Chunks = 4
            };

            var violations = ScheduleMutationPolicy.ValidateUpdate(current, proposed, hasStarted: true);

            Assert.Equal(["queryWindowSize", "startFrom", "chunks"], violations.Select(v => v.Field).ToArray());
            Assert.All(violations, v => Assert.Contains("cannot change", v.Message, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Unstarted_job_allows_window_start_and_rename_changes()
        {
            var current = Job("job.policy", "2026-01-01T00:00:00Z", TimeSpan.FromMinutes(5));
            var changed = current with
            {
                QueryWindowSize = TimeSpan.FromMinutes(10),
                StartFrom = Utc("2026-01-01T00:05:00Z"),
                ActivityId = "job.policy.renamed",
                Chunks = 4
            };

            Assert.Empty(ScheduleMutationPolicy.ValidateUpdate(current, changed, hasStarted: false));
        }

        [Fact]
        public void Id_change_is_rejected_even_when_unstarted()
        {
            var current = Job("job.policy", "2026-01-01T00:00:00Z", TimeSpan.FromMinutes(5));
            var reIded = current with { Id = "22222222222222222222222222222222" };

            var violations = ScheduleMutationPolicy.ValidateUpdate(current, reIded, hasStarted: false);

            Assert.Equal(["id"], violations.Select(v => v.Field).ToArray());
            Assert.All(violations, v => Assert.Contains("cannot change", v.Message, StringComparison.OrdinalIgnoreCase));
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

        [Fact]
        public void Started_job_rejects_removing_chunks()
        {
            var current = Job("job.policy", "2026-01-01T00:00:00Z", TimeSpan.FromMinutes(5)) with { Chunks = 2 };

            var violations = ScheduleMutationPolicy.ValidateUpdate(current, current with { Chunks = null }, hasStarted: true);

            Assert.Equal(["chunks"], violations.Select(v => v.Field).ToArray());
        }

        private static JobDefinition Job(string activityId, string start, TimeSpan window) => new()
        {
            Id = "11111111111111111111111111111111",
            ActivityId = activityId,
            FunctionName = "PolicyFunction",
            OutputTable = "PolicyOutput",
            QueryWindowSize = window,
            DelayFromUtcNow = TimeSpan.Zero,
            MaxParallelism = 1,
            QueryTimeout = TimeSpan.FromMinutes(1),
            StartFrom = Utc(start),
            Target = new JobTarget { ClusterUri = "https://ksr-example.invalid", Database = "DemoDb" }
        };

        private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToUniversalTime();
    }
}
