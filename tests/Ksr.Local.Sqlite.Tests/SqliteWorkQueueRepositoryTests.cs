// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Sqlite.Catalog;
using Ksr.Local.Sqlite.Connections;
using Ksr.Local.Sqlite.Schema;
using Ksr.Local.Sqlite.Queue;
using Ksr.Local.Sqlite.State;
using Microsoft.Data.Sqlite;

namespace Ksr.Local.Sqlite.Tests
{
    public sealed class SqliteWorkQueueRepositoryTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "queue-file-tests", Guid.NewGuid().ToString("N"));
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteSliceStateRepository state;
        private readonly SqliteWorkQueueRepository queue;

        public SqliteWorkQueueRepositoryTests()
        {
            Directory.CreateDirectory(testDirectory);
            var factory = new KsrSqliteConnectionFactory(new KsrSqliteConnectionOptions(Path.Combine(testDirectory, "queue.db")) { BusyTimeoutMilliseconds = 10_000 });
            new KsrSqliteSchema(factory).EnsureSchema();
            catalog = new SqliteJobCatalogRepository(factory);
            state = new SqliteSliceStateRepository(factory);
            catalog.Create(Schedule("job.queue"));
            state.Append("state-queued", JobId("job.queue"), At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
            state.Append("state-queued-2", JobId("job.queue"), At(5), At(10), DurableSliceStatus.Queued, expectedVersion: 0);
            queue = new SqliteWorkQueueRepository(factory);
        }

        public void Dispose()
        {
            TestCleanup.DeleteDirectoryWithRetry(testDirectory);
        }

        [Fact]
        public void Enqueue_is_idempotent_by_key()
        {
            var first = queue.Enqueue(JobId("job.queue"), At(0), At(5), "same-key", At(20));
            var second = queue.Enqueue(JobId("job.queue"), At(0), At(5), "same-key", At(20), priority: 99);

            Assert.Equal(first.QueueItemId, second.QueueItemId);
            Assert.Equal(first.Priority, second.Priority);
        }

        [Fact]
        public void ListPage_filters_and_uses_created_time_plus_queue_id_cursor()
        {
            var firstItem = queue.Enqueue(JobId("job.queue"), At(0), At(5), "page-first", At(20));
            Thread.Sleep(10);
            var secondItem = queue.Enqueue(JobId("job.queue"), At(5), At(10), "page-second", At(20));

            var first = queue.ListPage(
                JobId("job.queue"),
                queueName: "default",
                DurableWorkQueueState.Queued,
                cursorCreatedAtUtc: null,
                cursorId: null,
                take: 1);
            var next = queue.ListPage(
                JobId("job.queue"),
                queueName: "default",
                DurableWorkQueueState.Queued,
                cursorCreatedAtUtc: first[0].CreatedAtUtc,
                cursorId: first[0].QueueItemId,
                take: 1);

            Assert.Equal(secondItem.QueueItemId, Assert.Single(first).QueueItemId);
            Assert.Equal(firstItem.QueueItemId, Assert.Single(next).QueueItemId);
        }

        [Fact]
        public void ListPage_requires_a_positive_storage_limit()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => queue.ListPage(
                JobId("job.queue"),
                queueName: "default",
                DurableWorkQueueState.Queued,
                cursorCreatedAtUtc: null,
                cursorId: null,
                take: 0));
        }

        [Fact]
        public void Claim_orders_by_availability_then_priority_and_visibility_timeout()
        {
            queue.Enqueue(JobId("job.queue"), At(0), At(5), "low", At(10), priority: 0);
            queue.Enqueue(JobId("job.queue"), At(5), At(10), "high", At(10), priority: 5);

            var first = queue.Claim("default", "worker", TimeSpan.FromMinutes(5), At(10));
            var hidden = queue.Claim("default", "other", TimeSpan.FromMinutes(5), At(11));
            var reclaimed = queue.Claim("default", "other", TimeSpan.FromMinutes(5), At(16));

            Assert.Equal("high", first!.IdempotencyKey);
            Assert.Equal("low", hidden!.IdempotencyKey);
            Assert.Equal(first.QueueItemId, reclaimed!.QueueItemId);
            Assert.Equal(2, reclaimed.Attempts);
        }

        [Fact]
        public void Complete_abandon_and_deadletter_transition_leased_items_only()
        {
            var completed = queue.Enqueue(JobId("job.queue"), At(0), At(5), "complete", At(10));
            var abandoned = queue.Enqueue(JobId("job.queue"), At(5), At(10), "abandon", At(10));
            var claimedComplete = queue.Claim("default", "worker", TimeSpan.FromMinutes(5), At(10));
            Assert.True(queue.Complete(claimedComplete!.QueueItemId, "worker"));
            Assert.False(queue.Complete(completed.QueueItemId, "worker"));

            var claimedAbandon = queue.Claim("default", "worker", TimeSpan.FromMinutes(5), At(10));
            Assert.Equal(1, claimedAbandon!.Attempts);
            Assert.True(queue.Abandon(claimedAbandon.QueueItemId, "worker", At(12)));
            Assert.Equal(1, queue.Get(claimedAbandon.QueueItemId)!.Attempts);
            var reclaimed = queue.Claim("default", "worker", TimeSpan.FromMinutes(5), At(12));
            Assert.Equal(2, reclaimed!.Attempts);
            Assert.True(queue.DeadLetter(reclaimed.QueueItemId, "worker"));

            Assert.Equal(DurableWorkQueueState.Completed, queue.Get(claimedComplete.QueueItemId)!.State);
            Assert.Equal(DurableWorkQueueState.DeadLettered, queue.Get(reclaimed.QueueItemId)!.State);
        }

        [Fact]
        public void CountActiveByState_excludes_terminal_queue_history()
        {
            state.Append("state-queued-3", JobId("job.queue"), At(10), At(15), DurableSliceStatus.Queued, expectedVersion: 0);
            queue.Enqueue(JobId("job.queue"), At(0), At(5), "queued-count", At(20));
            queue.Enqueue(JobId("job.queue"), At(5), At(10), "leased-count", At(10));
            queue.Enqueue(JobId("job.queue"), At(10), At(15), "completed-count", At(10));

            var leased = queue.Claim("default", "worker", TimeSpan.FromMinutes(5), At(10));
            var completed = queue.Claim("default", "worker", TimeSpan.FromMinutes(5), At(10));
            Assert.True(queue.Complete(completed!.QueueItemId, "worker"));

            var counts = queue.CountActiveByState(JobId("job.queue"));

            Assert.Equal(1, counts.Queued);
            Assert.Equal(1, counts.Leased);
            Assert.Equal(DurableWorkQueueState.Leased, queue.Get(leased!.QueueItemId)!.State);
        }

        [Fact]
        public async Task Concurrent_claims_do_not_double_claim_one_item()
        {
            queue.Enqueue(JobId("job.queue"), At(0), At(5), "only", At(10));

            var claims = await Task.WhenAll(
                Task.Run(() => queue.Claim("default", "a", TimeSpan.FromMinutes(5), At(10))),
                Task.Run(() => queue.Claim("default", "b", TimeSpan.FromMinutes(5), At(10))));

            Assert.Single(claims, c => c is not null);
        }

        [Fact]
        public void Claim_skips_disabled_jobs_without_consuming_attempts_and_claims_after_resume()
        {
            var created = catalog.Create(Schedule("job.paused"));
            state.Append("paused-queued", JobId("job.paused"), At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
            var item = queue.Enqueue(JobId("job.paused"), At(0), At(5), "paused-work", At(10));
            var disabled = catalog.SetEnabled(JobId("job.paused"), enabled: false, expectedVersion: created.CatalogVersion);

            var pausedClaim = queue.Claim("default", "worker", TimeSpan.FromMinutes(5), At(10));

            Assert.Null(pausedClaim);
            var pausedItem = queue.Get(item.QueueItemId)!;
            Assert.Equal(DurableWorkQueueState.Queued, pausedItem.State);
            Assert.Equal(0, pausedItem.Attempts);
            Assert.Null(pausedItem.LockedBy);
            Assert.Null(pausedItem.LockedUntilUtc);

            catalog.SetEnabled(JobId("job.paused"), enabled: true, expectedVersion: disabled.CatalogVersion);
            var resumedClaim = queue.Claim("default", "worker", TimeSpan.FromMinutes(5), At(10));

            Assert.NotNull(resumedClaim);
            Assert.Equal(item.QueueItemId, resumedClaim!.QueueItemId);
            Assert.Equal(DurableWorkQueueState.Leased, resumedClaim.State);
            Assert.Equal(1, resumedClaim.Attempts);
            Assert.Equal("worker", resumedClaim.LockedBy);
        }

        [Fact]
        public void Count_claimable_uses_same_lifecycle_availability_and_lease_predicates_as_claim()
        {
            var disabled = catalog.Create(Schedule("job.disabled"));
            state.Append("disabled-queued", JobId("job.disabled"), At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
            queue.Enqueue(JobId("job.disabled"), At(0), At(5), "disabled-work", At(10));
            catalog.SetEnabled(JobId("job.disabled"), enabled: false, expectedVersion: disabled.CatalogVersion);

            queue.Enqueue(JobId("job.queue"), At(0), At(5), "ready-work", At(10));
            queue.Enqueue(JobId("job.queue"), At(5), At(10), "future-work", At(20));
            queue.Enqueue(JobId("job.queue"), At(5), At(10), "expired-work", At(10));
            var expired = queue.Claim("default", "stale-worker", TimeSpan.FromMinutes(1), At(10));
            Assert.NotNull(expired);

            Assert.Equal(2, queue.CountClaimable("default", At(12)));
            Assert.NotNull(queue.Claim("default", "worker-a", TimeSpan.FromMinutes(5), At(12)));
            Assert.NotNull(queue.Claim("default", "worker-b", TimeSpan.FromMinutes(5), At(12)));
            Assert.Null(queue.Claim("default", "worker-c", TimeSpan.FromMinutes(5), At(12)));
        }

        [Fact]
        public void Queued_only_claiming_skips_expired_leases()
        {
            queue.Enqueue(JobId("job.queue"), At(0), At(5), "expired-work", At(10));
            var expired = queue.Claim("default", "stale-worker", TimeSpan.FromMinutes(1), At(10));
            Assert.NotNull(expired);
            queue.Enqueue(JobId("job.queue"), At(5), At(10), "ready-work", At(10));

            Assert.Equal(2, queue.CountClaimable("default", At(12)));
            Assert.Equal(1, queue.CountQueuedClaimable("default", At(12)));
            var queuedOnly = queue.ClaimQueued("default", "fresh-worker", TimeSpan.FromMinutes(5), At(12));

            Assert.Equal("ready-work", queuedOnly!.IdempotencyKey);
            Assert.Equal("stale-worker", queue.Get(expired!.QueueItemId)!.LockedBy);
        }

        [Fact]
        public void Defer_without_attempt_releases_claim_without_consuming_attempt()
        {
            queue.Enqueue(JobId("job.queue"), At(0), At(5), "defer-paused", At(10));
            var claimed = queue.Claim("default", "worker", TimeSpan.FromMinutes(5), At(10));
            Assert.NotNull(claimed);
            Assert.Equal(1, claimed!.Attempts);

            Assert.True(queue.DeferWithoutAttempt(claimed.QueueItemId, "worker", At(10)));

            var deferred = queue.Get(claimed.QueueItemId)!;
            Assert.Equal(DurableWorkQueueState.Queued, deferred.State);
            Assert.Equal(0, deferred.Attempts);
            Assert.Null(deferred.LockedBy);
            Assert.Null(deferred.LockedUntilUtc);
        }

        [Fact]
        public void Extend_lease_updates_locked_until_for_current_owner_only()
        {
            queue.Enqueue(JobId("job.queue"), At(0), At(5), "extend-current-owner", At(10));
            var claimed = queue.Claim("default", "worker", TimeSpan.FromMinutes(1), At(10));
            Assert.NotNull(claimed);

            Assert.True(queue.ExtendLease(claimed!.QueueItemId, "worker", At(20), At(10)));
            Assert.False(queue.ExtendLease(claimed.QueueItemId, "other-worker", At(30), At(10)));

            var extended = queue.Get(claimed.QueueItemId)!;
            Assert.Equal(At(20), extended.LockedUntilUtc);
            Assert.Equal("worker", extended.LockedBy);
            Assert.Equal(1, extended.Attempts);
        }

        [Fact]
        public void Extend_lease_fails_after_current_lease_expires()
        {
            queue.Enqueue(JobId("job.queue"), At(0), At(5), "extend-expired", At(10));
            var claimed = queue.Claim("default", "worker", TimeSpan.FromMinutes(1), At(10));
            Assert.NotNull(claimed);

            Assert.False(queue.ExtendLease(claimed!.QueueItemId, "worker", At(20), At(12)));

            var expired = queue.Get(claimed.QueueItemId)!;
            Assert.Equal(At(11), expired.LockedUntilUtc);
            Assert.Equal("worker", expired.LockedBy);
        }

        [Fact]
        public void Claim_enforces_job_parallelism_only_when_requested()
        {
            queue.Enqueue(JobId("job.queue"), At(0), At(5), "first", At(10));
            queue.Enqueue(JobId("job.queue"), At(5), At(10), "second", At(10));

            // job.queue has maxParallelism=1: with enforcement, only one slice may be leased at a time.
            var first = queue.Claim("default", "w1", TimeSpan.FromMinutes(5), At(10), enforceJobParallelism: true);
            var blocked = queue.Claim("default", "w2", TimeSpan.FromMinutes(5), At(10), enforceJobParallelism: true);

            Assert.NotNull(first);
            Assert.Null(blocked);

            Assert.True(queue.Complete(first!.QueueItemId, "w1"));
            var afterComplete = queue.Claim("default", "w3", TimeSpan.FromMinutes(5), At(10), enforceJobParallelism: true);
            Assert.NotNull(afterComplete);
        }

        [Fact]
        public void Count_claimable_respects_job_parallelism_only_when_requested()
        {
            queue.Enqueue(JobId("job.queue"), At(0), At(5), "a", At(10));
            queue.Enqueue(JobId("job.queue"), At(5), At(10), "b", At(10));

            Assert.Equal(2, queue.CountClaimable("default", At(10)));
            Assert.Equal(1, queue.CountClaimable("default", At(10), enforceJobParallelism: true));

            var claimed = queue.Claim("default", "w1", TimeSpan.FromMinutes(5), At(10), enforceJobParallelism: true);
            Assert.NotNull(claimed);
            Assert.Equal(0, queue.CountClaimable("default", At(10), enforceJobParallelism: true));
        }

        [Fact]
        public void Claim_only_reclaims_expired_lease_after_grace_margin()
        {
            queue.Enqueue(JobId("job.queue"), At(0), At(5), "graced", At(10));
            var leased = queue.Claim("default", "stale", TimeSpan.FromMinutes(1), At(10));
            Assert.NotNull(leased);

            // Lease expires at At(11); 30s later is still inside a 1-minute grace, so it is not reclaimable.
            Assert.Null(queue.Claim("default", "fresh", TimeSpan.FromMinutes(5), At(11).AddSeconds(30), expiredLeaseGrace: TimeSpan.FromMinutes(1)));
            Assert.Equal(0, queue.CountClaimable("default", At(11).AddSeconds(30), expiredLeaseGrace: TimeSpan.FromMinutes(1)));

            // Beyond the grace it becomes reclaimable.
            Assert.Equal(1, queue.CountClaimable("default", At(12).AddSeconds(30), expiredLeaseGrace: TimeSpan.FromMinutes(1)));
            var reclaimed = queue.Claim("default", "fresh", TimeSpan.FromMinutes(5), At(12).AddSeconds(30), expiredLeaseGrace: TimeSpan.FromMinutes(1));
            Assert.NotNull(reclaimed);
            Assert.Equal(leased!.QueueItemId, reclaimed!.QueueItemId);
            Assert.Equal(2, reclaimed.Attempts);
        }

        [Fact]
        public void Requeue_expired_lease_resets_only_an_expired_leased_row()
        {
            queue.Enqueue(JobId("job.queue"), At(0), At(5), "requeue", At(10));
            var leased = queue.Claim("default", "stale", TimeSpan.FromMinutes(5), At(10));
            Assert.NotNull(leased);

            // Still held: a non-expired lease is never reset.
            Assert.False(queue.RequeueExpiredLease(JobId("job.queue"), At(0), At(5), At(12), At(12)));
            Assert.Equal(DurableWorkQueueState.Leased, queue.Get(leased!.QueueItemId)!.State);

            // After expiry the orphaned row is reset to Queued and made available again.
            Assert.True(queue.RequeueExpiredLease(JobId("job.queue"), At(0), At(5), At(20), At(20)));
            var requeued = queue.Get(leased.QueueItemId)!;
            Assert.Equal(DurableWorkQueueState.Queued, requeued.State);
            Assert.Null(requeued.LockedBy);
            Assert.Null(requeued.LockedUntilUtc);
            Assert.Equal(At(20), requeued.AvailableAtUtc);
        }

        private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        private static string JobId(string activityId)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(activityId));
            return new Guid(bytes).ToString("N");
        }

        private static string Schedule(string activityId) => $$"""
        {
          "id": "{{JobId(activityId)}}",
          "activityId": "{{activityId}}",
          "functionName": "QueueFunction",
          "outputTable": "QueueOutput",
          "queryWindowSize": "00:05:00",
          "delayFromUtcNow": "00:00:00",
          "maxParallelism": 1,
          "queryTimeout": "00:01:00",
          "isPaused": false,
          "startFrom": "2026-01-01T00:00:00Z",
          "target": { "clusterUri": "https://ksr-example.invalid", "database": "DemoDb" }
        }
        """;
    }
}
