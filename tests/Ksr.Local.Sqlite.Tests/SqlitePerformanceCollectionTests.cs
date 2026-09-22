// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Core.Performance;
using Ksr.Local.Sqlite.Infrastructure;
using static Ksr.Local.Sqlite.Tests.PerformanceTestStore;

namespace Ksr.Local.Sqlite.Tests
{
    public sealed class SqlitePerformanceCollectionTests : IDisposable
    {
        private readonly PerformanceTestStore store = new();

        public void Dispose() => store.Dispose();

        [Fact]
        public void Pending_batches_are_bounded_target_scoped_and_fresh_old_alternation_is_fair()
        {
            var fresh = store.CreateJob("fresh");
            var old = store.CreateJob("old", cluster: "https://old-target.invalid", database: "OldDb");
            for (var index = 0; index < 210; index++)
            {
                store.Capture($"fresh-{index}", fresh, At(index + 10), At(index + 11));
            }

            for (var index = 0; index < 5; index++)
            {
                store.Capture($"old-{index}", old, At(index + 1), At(index + 2));
            }

            var newest = store.Repository.GetPendingAttempts(At(300), 1000);
            Assert.Equal(200, newest.Count);
            Assert.Equal("fresh-209", newest[0].AttemptId);
            Assert.All(newest, attempt => Assert.Equal(Cluster, attempt.ClusterUri));
            var oldest = store.Repository.GetPendingAttempts(At(300), oldestFirst: true);
            Assert.Equal(5, oldest.Count);
            Assert.Equal("old-0", oldest[0].AttemptId);
            Assert.All(oldest, attempt => Assert.Equal("https://old-target.invalid", attempt.ClusterUri));

            store.Repository.ApplyStatistics(newest, [], At(300));
            var remainingFresh = store.Repository.GetPendingAttempts(At(300));
            Assert.Equal(10, remainingFresh.Count);
            Assert.DoesNotContain(remainingFresh, attempt => newest.Any(item => item.AttemptId == attempt.AttemptId));
            Assert.Equal(200, store.Repository.GetPendingAttempts(At(301)).Count);
        }

        [Fact]
        public void Missing_results_and_collection_failures_back_off_durably_without_changing_worker_facts()
        {
            var job = store.CreateJob();
            store.Capture("attempt", job, At(10), At(11));
            var first = store.Repository.GetPendingAttempts(At(20));
            store.Repository.ApplyStatistics(first, [], At(20));
            store.Repository.ApplyStatistics(first, [], At(20));

            Assert.Empty(store.Repository.GetPendingAttempts(At(20.5)));
            var second = Assert.Single(store.Repository.GetPendingAttempts(At(21)));
            Assert.Equal(1, second.LookupCount);
            store.Repository.RecordCollectionFailure([second], At(21), "Fake permission denied");
            Assert.Empty(store.Repository.GetPendingAttempts(At(22)));
            Assert.Equal(2, Assert.Single(store.Repository.GetPendingAttempts(At(23))).LookupCount);
            Assert.Equal("Fake permission denied", store.Repository.GetCollectionStatus().LastError);
            Assert.Equal("Succeeded", store.Scalar("SELECT status FROM performance_attempts;"));
            Assert.Equal("Succeeded", store.Scalar("SELECT status FROM slice_attempts;"));
            Assert.Equal(100d, Assert.Single(store.Repository.GetAggregates(At(0), At(30))).SuccessPercent);
        }

        [Fact]
        public void Empty_failure_batch_persists_global_error_without_mutating_attempts_or_coverage()
        {
            store.Repository.RecordCollectionFailure([], At(20), "Global pass failure before pending work was read.");
            Assert.Equal("Global pass failure before pending work was read.", store.Repository.GetCollectionStatus().LastError);
            Assert.Null(store.Repository.GetCollectionStatus().LastCollectionUtc);
            Assert.Equal(0, store.Count("SELECT COUNT(*) FROM performance_attempts;"));

            var job = store.CreateJob();
            store.Capture("measured", job, At(5), At(6));
            store.Capture("pending", job, At(10), At(11));
            var measured = store.Repository.GetPendingAttempts(At(20), take: 1, oldestFirst: true);
            store.Repository.ApplyStatistics(measured, [Statistics(Assert.Single(measured))], At(20));
            var pending = Assert.Single(store.Repository.GetPendingAttempts(At(20)));

            store.Repository.RecordCollectionFailure([], At(21), "Global pass failed again.");

            Assert.Equal("Global pass failed again.", store.Scalar("SELECT last_error FROM performance_collection_state;"));
            Assert.Equal(pending, Assert.Single(store.Repository.GetPendingAttempts(At(21))));
            Assert.Equal(1, store.Repository.GetCollectionStatus().AvailableSamples);
            Assert.Equal(2, Assert.Single(store.Repository.GetAggregates(At(0), At(30))).CompletedAttempts);
            store.Repository.RecordPassSuccess(At(22));
            Assert.Null(store.Repository.GetCollectionStatus().LastError);
            Assert.Equal(At(22), store.Repository.GetCollectionStatus().LastCollectionUtc);
            Assert.Equal(1, store.Repository.GetCollectionStatus().AvailableSamples);
        }

        [Fact]
        public void Partial_family_coverage_survives_failures_and_last_pass_success_only_clears_the_pass_error()
        {
            var job = store.CreateJob();
            store.Capture("partial", job, At(10), At(11));
            var first = store.Repository.GetPendingAttempts(At(20));
            var command = Statistics(Assert.Single(first), cpu: 7, duration: null, memory: null, error: "Duration unavailable");
            store.Repository.ApplyStatistics(first, [command], At(20));
            var retry = store.Repository.GetPendingAttempts(At(21));
            store.Repository.RecordCollectionFailure(retry, At(21), "Fake network outage");
            store.Repository.RecordPassSuccess(At(22));

            Assert.Null(store.Scalar("SELECT last_error FROM performance_collection_state;"));
            Assert.Equal("Duration unavailable", store.Scalar("SELECT validation_error FROM performance_attempts;"));
            Assert.Equal(At(22), store.Repository.GetCollectionStatus().LastCollectionUtc);
            var partial = Assert.Single(store.Repository.GetAggregates(At(0), At(30)));
            Assert.Equal(new PerformancePercentiles(1, 7, 7, 7), partial.CpuSeconds);
            Assert.Equal(PerformancePercentiles.Empty, partial.DurationSeconds);
            Assert.Equal(1, store.Repository.GetCollectionStatus().AvailableSamples);

            var last = store.Repository.GetPendingAttempts(At(23));
            store.Repository.ApplyStatistics(last, [command with { DurationSeconds = 9, MemoryPeakBytes = 1_073_741_824, ValidationError = null }], At(23));
            Assert.Equal("Collected", store.Scalar("SELECT collection_status FROM performance_attempts;"));
            Assert.Null(store.Scalar("SELECT validation_error FROM performance_attempts;"));
            Assert.Null(store.Repository.GetCollectionStatus().LastError);
        }

        [Fact]
        public void New_exact_identity_does_not_require_fuzzy_local_time_matching()
        {
            var job = store.CreateJob();
            store.Capture("exact", job, At(10), At(11));
            var pending = store.Repository.GetPendingAttempts(At(20));
            var command = Statistics(Assert.Single(pending), 5, 6, 7) with { StartedAtUtc = At(9), CompletedAtUtc = At(12) };

            store.Repository.ApplyStatistics(pending, [command], At(20));

            Assert.Equal(5d, Assert.Single(store.Repository.GetAggregates(At(0), At(30))).CpuSeconds.P50);
            Assert.Equal(0, store.Repository.GetCollectionStatus().PendingSamples);
        }

        [Fact]
        public void Legacy_identity_requires_a_unique_command_inside_the_complete_attempt_interval()
        {
            var job = store.CreateJob();
            store.Legacy("first", job, At(10), At(20));
            store.Legacy("second", job, At(30), At(40));
            store.Reconcile();
            var pending = store.Repository.GetPendingAttempts(At(60));
            Assert.Equal(2, pending.Count);
            var first = pending.Single(attempt => attempt.AttemptId == "first");
            var command = Statistics(first, 3, 4, 5) with { StartedAtUtc = At(11), CompletedAtUtc = At(19) };

            store.Repository.ApplyStatistics(pending, [command], At(60));

            Assert.Equal("Collected", store.Scalar("SELECT collection_status FROM performance_attempts WHERE attempt_id='first';"));
            Assert.Equal("Pending", store.Scalar("SELECT collection_status FROM performance_attempts WHERE attempt_id='second';"));
            var total = Assert.Single(store.Repository.GetAggregates(At(0), At(70)));
            Assert.Equal(2, total.CompletedAttempts);
            Assert.Equal(1, total.CpuSeconds.SampleCount);
        }

        [Fact]
        public void Overlapping_legacy_attempts_are_ambiguous_even_when_the_other_attempt_is_outside_the_batch()
        {
            var job = store.CreateJob();
            store.Legacy("first", job, At(10), At(20));
            store.Legacy("second", job, At(15), At(25), "FailedRetryable");
            store.Reconcile();
            var pending = store.Repository.GetPendingAttempts(At(60), take: 1);
            var command = Statistics(Assert.Single(pending)) with { StartedAtUtc = At(16), CompletedAtUtc = At(19) };

            store.Repository.ApplyStatistics(pending, [command], At(60));

            Assert.Equal("Ambiguous", store.Scalar("SELECT collection_status FROM performance_attempts WHERE attempt_id='first';"));
            Assert.Equal(0, store.Repository.GetCollectionStatus().AvailableSamples);
            Assert.Equal(50d, Assert.Single(store.Repository.GetAggregates(At(0), At(70))).SuccessPercent);
        }

        [Theory]
        [InlineData("FailedRetryable")]
        [InlineData("LeaseLost")]
        public void Overlapping_legacy_attempt_with_unresolved_target_remains_a_possible_competitor(string competingStatus)
        {
            var job = store.CreateJob();
            store.Legacy("success", job, At(10), At(20));
            store.Legacy("unresolved-target", job, At(15), At(25), competingStatus);
            var changed = store.Catalog.Update(job.JobId,
                Schedule(job.ActivityId, cluster: "https://changed-target.invalid", database: "ChangedDb"), job.CatalogVersion);
            store.Execute("UPDATE job_definition_events SET recorded_at_utc=$at WHERE job_id=$job AND catalog_version=$version;",
                ("$at", SqliteStorage.Utc(At(22))), ("$job", job.JobId), ("$version", changed.CatalogVersion));
            store.Reconcile();
            Assert.Null(store.Scalar("SELECT cluster_uri FROM performance_attempts WHERE attempt_id='unresolved-target';"));
            Assert.Null(store.Scalar("SELECT database_name FROM performance_attempts WHERE attempt_id='unresolved-target';"));
            var pending = store.Repository.GetPendingAttempts(At(60), take: 1);
            Assert.Equal("success", Assert.Single(pending).AttemptId);
            var command = Statistics(pending[0], cpu: 99) with { StartedAtUtc = At(16), CompletedAtUtc = At(19) };

            store.Repository.ApplyStatistics(pending, [command], At(60));

            Assert.Equal("Ambiguous", store.Scalar("SELECT collection_status FROM performance_attempts WHERE attempt_id='success';"));
            Assert.Equal(0, store.Repository.GetCollectionStatus().AvailableSamples);
            var total = Assert.Single(store.Repository.GetAggregates(At(0), At(70)));
            Assert.Equal(2, total.CompletedAttempts);
            Assert.Equal(50d, total.SuccessPercent);
            Assert.Equal(0, total.CpuSeconds.SampleCount);
        }

        [Fact]
        public void A_competing_request_with_a_proven_different_target_does_not_block_legacy_correlation()
        {
            var job = store.CreateJob("legacy");
            store.Legacy("legacy-success", job, At(10), At(20));
            store.Reconcile();
            var first = Assert.Single(store.Repository.GetPendingAttempts(At(60)));
            var other = store.CreateJob("other", cluster: "https://other-target.invalid", database: "OtherDb");
            store.Capture("other-target", other, At(15), At(25), "FailedRetryable", clientRequestId: first.ClientRequestId);
            var command = Statistics(first, cpu: 7) with { StartedAtUtc = At(16), CompletedAtUtc = At(19) };

            store.Repository.ApplyStatistics([first], [command], At(60));

            Assert.Equal("Collected", store.Scalar("SELECT collection_status FROM performance_attempts WHERE attempt_id='legacy-success';"));
            Assert.Equal(7d, store.Scalar("SELECT cpu_seconds FROM performance_attempts WHERE attempt_id='legacy-success';"));
        }

        [Fact]
        public void Legacy_competitor_lookup_is_indexed_even_when_target_evidence_is_missing()
        {
            using var connection = store.Factory.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                EXPLAIN QUERY PLAN
                SELECT 1 FROM performance_attempts
                WHERE client_request_id='request' AND attempt_id<>'attempt'
                  AND (cluster_uri='https://target.invalid' OR cluster_uri IS NULL)
                  AND (database_name='Db' OR database_name IS NULL)
                  AND (started_at_utc IS NULL OR started_at_utc <= '2026-09-01T00:16:00.0000000Z')
                  AND (completed_at_utc IS NULL OR completed_at_utc >= '2026-09-01T00:19:00.0000000Z')
                LIMIT 1;
                """;
            using var reader = command.ExecuteReader();
            var plan = new List<string>();
            while (reader.Read()) plan.Add(reader.GetString(3));

            Assert.Contains(plan, detail => detail.Contains("ix_performance_attempts_client", StringComparison.Ordinal));
            Assert.DoesNotContain(plan, detail => detail.Contains("SCAN performance_attempts", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Multiple_roots_or_conflicting_duplicate_server_records_do_not_produce_a_sample(bool sameRoot)
        {
            var job = store.CreateJob();
            store.Capture("ambiguous", job, At(10), At(11));
            var pending = store.Repository.GetPendingAttempts(At(20));
            var first = Statistics(Assert.Single(pending));
            var second = first with { CpuSeconds = 5, ServerActivityId = sameRoot ? first.ServerActivityId : Guid.NewGuid() };

            store.Repository.ApplyStatistics(pending, [first, second], At(20));

            Assert.Equal("Ambiguous", store.Scalar("SELECT collection_status FROM performance_attempts;"));
            Assert.Equal(0, store.Repository.GetCollectionStatus().AvailableSamples);
            Assert.Empty(store.Repository.GetPendingAttempts(At(30)));
        }

        [Fact]
        public void Identical_duplicate_server_records_and_reapplied_batches_are_idempotent()
        {
            var job = store.CreateJob();
            store.Capture("unique", job, At(10), At(11));
            var pending = store.Repository.GetPendingAttempts(At(20));
            var command = Statistics(Assert.Single(pending));

            store.Repository.ApplyStatistics(pending, [command, command], At(20));
            store.Repository.ApplyStatistics(pending, [command], At(21));

            Assert.Equal(1, store.Count("SELECT lookup_count FROM performance_attempts;"));
            Assert.Equal(1, store.Repository.GetCollectionStatus().AvailableSamples);
            Assert.Equal(1, Assert.Single(store.Repository.GetAggregates(At(0), At(30))).CpuSeconds.SampleCount);
        }

        [Fact]
        public void A_unique_server_identity_cannot_be_assigned_to_a_second_attempt_but_is_scoped_to_target()
        {
            var firstJob = store.CreateJob("first");
            var otherJob = store.CreateJob("other", cluster: "https://other-target.invalid");
            store.Capture("first", firstJob, At(10), At(11));
            store.Capture("second", firstJob, At(12), At(13));
            store.Capture("other", otherJob, At(14), At(15));
            var root = Guid.NewGuid();
            var firstBatch = store.Repository.GetPendingAttempts(At(20), take: 1, oldestFirst: true);
            store.Repository.ApplyStatistics(firstBatch, [Statistics(Assert.Single(firstBatch), cpu: 3, server: root)], At(20));
            var secondBatch = store.Repository.GetPendingAttempts(At(20), take: 1, oldestFirst: true);
            store.Repository.ApplyStatistics(secondBatch, [Statistics(Assert.Single(secondBatch), cpu: 99, server: root)], At(20));
            var otherBatch = store.Repository.GetPendingAttempts(At(20), take: 1);
            store.Repository.ApplyStatistics(otherBatch, [Statistics(Assert.Single(otherBatch), cpu: 7, server: root)], At(20));

            Assert.Equal("Ambiguous", store.Scalar("SELECT collection_status FROM performance_attempts WHERE attempt_id='second';"));
            Assert.Equal(3d, store.Scalar("SELECT cpu_seconds FROM performance_attempts WHERE attempt_id='first';"));
            Assert.Equal(2, store.Repository.GetCollectionStatus().AvailableSamples);
            Assert.Equal(2, store.Count("SELECT COUNT(*) FROM performance_attempts WHERE server_activity_id=$server;", ("$server", root.ToString("N"))));
        }

        [Fact]
        public void Duplicate_exact_client_identity_is_ambiguous_before_either_attempt_is_enriched()
        {
            var job = store.CreateJob();
            store.Capture("first", job, At(10), At(11), clientRequestId: "Ksr.Local.Output;attempt|same");
            store.Capture("second", job, At(20), At(21), clientRequestId: "Ksr.Local.Output;attempt|same");
            var pending = store.Repository.GetPendingAttempts(At(30), take: 1);

            store.Repository.ApplyStatistics(pending, [Statistics(Assert.Single(pending))], At(30));

            Assert.Equal("Ambiguous", store.Scalar("SELECT collection_status FROM performance_attempts WHERE attempt_id='second';"));
            Assert.Equal(0, store.Repository.GetCollectionStatus().AvailableSamples);
        }

        [Fact]
        public void A_changed_value_for_the_same_previously_partial_server_record_is_not_overwritten()
        {
            var job = store.CreateJob();
            store.Capture("partial", job, At(10), At(11));
            var first = store.Repository.GetPendingAttempts(At(20));
            var command = Statistics(Assert.Single(first), cpu: 1, duration: null, memory: null);
            store.Repository.ApplyStatistics(first, [command], At(20));
            var second = store.Repository.GetPendingAttempts(At(21));
            store.Repository.ApplyStatistics(second, [command with { CpuSeconds = 2, DurationSeconds = 3 }], At(21));

            Assert.Equal("Ambiguous", store.Scalar("SELECT collection_status FROM performance_attempts;"));
            Assert.Equal(0, store.Repository.GetCollectionStatus().AvailableSamples);
        }

        [Fact]
        public void Server_completed_state_and_local_success_are_both_required_for_resource_samples()
        {
            var job = store.CreateJob();
            store.Capture("local-success", job, At(10), At(11));
            store.Capture("local-failure", job, At(10), At(11), "LeaseLost");
            var pending = store.Repository.GetPendingAttempts(At(20));
            Assert.Equal("local-success", Assert.Single(pending).AttemptId);
            var failed = Statistics(pending[0], state: "Failed");
            store.Repository.ApplyStatistics(pending, [failed], At(20));
            var fabricated = pending[0] with { AttemptId = "local-failure", ClientRequestId = "Ksr.Local.Output;attempt|local-failure" };
            store.Repository.ApplyStatistics([fabricated], [Statistics(fabricated)], At(21));

            Assert.Equal(0, store.Repository.GetCollectionStatus().AvailableSamples);
            Assert.Equal(2, Assert.Single(store.Repository.GetAggregates(At(0), At(30))).CompletedAttempts);
            Assert.Equal(1, store.Repository.GetCollectionStatus().PendingSamples);
        }

        [Fact]
        public void Expired_lookup_history_retains_the_completed_local_fact_but_leaves_the_remote_population()
        {
            var job = store.CreateJob();
            store.Capture("aged", job, At(10).AddDays(-31), At(11));

            Assert.Empty(store.Repository.GetPendingAttempts(At(20)));
            Assert.Equal("Expired", store.Scalar("SELECT collection_status FROM performance_attempts;"));
            Assert.Equal(1, Assert.Single(store.Repository.GetAggregates(At(0), At(30))).CompletedAttempts);
            Assert.Equal(0, store.Repository.GetCollectionStatus().PendingSamples);
        }

        [Fact]
        public void Mixed_target_results_and_stale_attempt_contexts_are_never_applied()
        {
            var firstJob = store.CreateJob("first");
            var otherJob = store.CreateJob("other", database: "OtherDb");
            store.Capture("first", firstJob, At(10), At(11));
            store.Capture("other", otherJob, At(12), At(13));
            var first = Assert.Single(store.Repository.GetPendingAttempts(At(20), oldestFirst: true));
            var other = Assert.Single(store.Repository.GetPendingAttempts(At(20)));
            Assert.Throws<ArgumentException>(() => store.Repository.ApplyStatistics([first, other], [Statistics(first)], At(20)));
            store.Repository.ApplyStatistics([first with { ClientRequestId = "stale" }], [Statistics(first)], At(20));
            Assert.Equal(0, store.Repository.GetCollectionStatus().AvailableSamples);
            Assert.Equal(0, store.Count("SELECT SUM(lookup_count) FROM performance_attempts;"));
        }
    }
}
