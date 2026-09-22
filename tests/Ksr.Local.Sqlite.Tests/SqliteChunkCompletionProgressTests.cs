// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Core.Scheduling;
using Ksr.Local.Sqlite.Catalog;
using Ksr.Local.Sqlite.Connections;
using Ksr.Local.Sqlite.Schema;
using Ksr.Local.Sqlite.State;

namespace Ksr.Local.Sqlite.Tests
{
    public sealed class SqliteChunkCompletionProgressTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "chunk-progress-tests", Guid.NewGuid().ToString("N"));
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteChunkStateRepository chunks;

        public SqliteChunkCompletionProgressTests()
        {
            Directory.CreateDirectory(testDirectory);
            var factory = new KsrSqliteConnectionFactory(new KsrSqliteConnectionOptions(Path.Combine(testDirectory, "progress.db")) { BusyTimeoutMilliseconds = 10_000 });
            new KsrSqliteSchema(factory).EnsureSchema();
            catalog = new SqliteJobCatalogRepository(factory);
            chunks = new SqliteChunkStateRepository(factory);
        }

        public void Dispose()
        {
            TestCleanup.DeleteDirectoryWithRetry(testDirectory);
        }

        [Fact]
        public void Lists_completed_chunk_counts_for_all_materialized_windows_in_one_job()
        {
            var job = catalog.Create(Schedule("job.chunk.progress", chunks: 16));
            var first = new SliceRange(job.JobId, At(0), At(5));
            var second = new SliceRange(job.JobId, At(5), At(10));
            var firstChildren = chunks.EnsureWindow(first, 16, "test");
            var secondChildren = chunks.EnsureWindow(second, 16, "test");
            Complete(firstChildren[0], "first-0");
            Complete(firstChildren[1], "first-1");
            Complete(firstChildren[2], "first-2");
            Complete(secondChildren[0], "second-0");
            chunks.MarkQueued("second-1-queued", secondChildren[1].Execution, actor: "test");

            var progress = chunks.ListCompletionProgress(job.JobId);

            Assert.Collection(
                progress,
                row =>
                {
                    Assert.Equal(At(0), row.SliceStartUtc);
                    Assert.Equal(At(5), row.SliceEndUtc);
                    Assert.Equal(3, row.CompletedChunks);
                    Assert.Equal(16, row.TotalChunks);
                },
                row =>
                {
                    Assert.Equal(At(5), row.SliceStartUtc);
                    Assert.Equal(At(10), row.SliceEndUtc);
                    Assert.Equal(1, row.CompletedChunks);
                    Assert.Equal(16, row.TotalChunks);
                });
        }

        [Fact]
        public void Returns_no_progress_rows_when_no_chunk_windows_are_materialized()
        {
            var job = catalog.Create(Schedule("job.chunk.empty", chunks: 16));

            Assert.Empty(chunks.ListCompletionProgress(job.JobId));
        }

        [Fact]
        public void Progress_follows_current_child_state_through_repair_transitions()
        {
            var job = catalog.Create(Schedule("job.chunk.repair-progress", chunks: 2));
            var slice = new SliceRange(job.JobId, At(0), At(5));
            var children = chunks.EnsureWindow(slice, 2, "test");
            Complete(children[0], "successful");
            chunks.MarkQueued("failed-queued", children[1].Execution, actor: "test");
            var failedLease = chunks.AcquireLease("failed-lease", children[1].Execution, "failed-worker", TimeSpan.FromMinutes(5), At(20))!;
            Assert.True(chunks.DeadLetterLease("failed-dead", children[1].Execution, "failed-worker", failedLease.LeaseToken!, At(21), "permanent", "Permanent"));
            Assert.Equal(1, Assert.Single(chunks.ListCompletionProgress(job.JobId)).CompletedChunks);

            Assert.True(chunks.RequeueFailed("repair-queued", children[1].Execution, "test", "repair", At(22)));
            Assert.Equal(1, Assert.Single(chunks.ListCompletionProgress(job.JobId)).CompletedChunks);
            var repairLease = chunks.AcquireLease("repair-lease", children[1].Execution, "repair-worker", TimeSpan.FromMinutes(5), At(23))!;
            Assert.True(chunks.CompleteLease("repair-complete", children[1].Execution, "repair-worker", repairLease.LeaseToken!, At(24)));

            Assert.Equal(2, Assert.Single(chunks.ListCompletionProgress(job.JobId)).CompletedChunks);
        }

        private void Complete(DurableChunkState child, string operationPrefix)
        {
            chunks.MarkQueued($"{operationPrefix}-queued", child.Execution, actor: "test");
            var lease = chunks.AcquireLease($"{operationPrefix}-lease", child.Execution, operationPrefix, TimeSpan.FromMinutes(5), At(20))!;
            Assert.True(chunks.CompleteLease($"{operationPrefix}-complete", child.Execution, operationPrefix, lease.LeaseToken!, At(21)));
        }

        private static DateTimeOffset At(int minutes) =>
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        private static string JobId(string activityId)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(activityId));
            return new Guid(bytes).ToString("N");
        }

        private static string Schedule(string activityId, int chunks) => $$"""
        {
          "id": "{{JobId(activityId)}}",
          "activityId": "{{activityId}}",
          "functionName": "ChunkProgressFunction",
          "outputTable": "ChunkProgressOutput",
          "queryWindowSize": "00:05:00",
          "delayFromUtcNow": "00:00:00",
          "maxParallelism": 16,
          "queryTimeout": "00:01:00",
          "chunks": {{chunks}},
          "isPaused": false,
          "startFrom": "2026-01-01T00:00:00Z",
          "target": { "clusterUri": "https://ksr-example.invalid", "database": "DemoDb" }
        }
        """;
    }
}
