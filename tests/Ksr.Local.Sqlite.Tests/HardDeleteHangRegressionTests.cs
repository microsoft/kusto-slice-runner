// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Core.Scheduling;
using Ksr.Local.Sqlite.Catalog;
using Ksr.Local.Sqlite.Connections;
using Ksr.Local.Sqlite.Lifecycle;
using Ksr.Local.Sqlite.Schema;
using Ksr.Local.Sqlite.State;
using Microsoft.Data.Sqlite;

namespace Ksr.Local.Sqlite.Tests
{
    // Regression guard for the production bug where the "Hard delete" page hung forever and the job
    // was never purged while the rest of the app stayed up. The hard delete runs a multi-table write
    // while the scheduler/worker/dashboard touch the same database on other connections. Under the
    // shared-cache + per-open-WAL connection handling these tests wedge (a concurrent reader's
    // table-level read lock blocks the purge with SQLITE_LOCKED, which busy_timeout does not retry);
    // with a private cache + WAL MVCC the reader never blocks the single writer and the purge
    // completes. Each test is bounded so a regression fails fast instead of hanging the suite.
    public sealed class HardDeleteHangRegressionTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "hard-delete-hang-tests", Guid.NewGuid().ToString("N"));
        private readonly KsrSqliteConnectionFactory factory;

        public HardDeleteHangRegressionTests()
        {
            Directory.CreateDirectory(testDirectory);
            factory = new KsrSqliteConnectionFactory(new KsrSqliteConnectionOptions(Path.Combine(testDirectory, "hang.db")) { BusyTimeoutMilliseconds = 10_000 });
            new KsrSqliteSchema(factory).EnsureSchema();
        }

        public void Dispose()
        {
            TestCleanup.DeleteDirectoryWithRetry(testDirectory);
        }

        // A concurrent reader holds a table read lock for the whole window. Shared cache makes that
        // read lock block the purge's write to the same table (the hang); a private cache + WAL lets
        // the single writer proceed alongside the reader, so the purge completes within the bound.
        [Fact]
        public async Task Hard_delete_completes_while_a_concurrent_reader_holds_a_table_read_lock()
        {
            var catalog = new SqliteJobCatalogRepository(factory);
            var lifecycle = new SqliteJobLifecycleService(factory, catalog);

            catalog.Create(Schedule("live", "00:30:00"));
            var victim = catalog.Create(Schedule("victim", "00:30:00"));
            catalog.SetEnabled(victim.JobId, enabled: false, expectedVersion: victim.CatalogVersion);

            // Hold an open cursor on job_definitions (a table the purge must delete) on a separate
            // connection so its read lock is held for the entire wait window below.
            using var readerConnection = factory.OpenConnection();
            using var readerCommand = readerConnection.CreateCommand();
            readerCommand.CommandText = "SELECT job_id FROM job_definitions;";
            using var heldReader = readerCommand.ExecuteReader();
            Assert.True(heldReader.Read());

            var purge = Task.Run(() => lifecycle.HardDelete(victim.JobId, $"DELETE {victim.DisplayName}", actor: "test", reason: "hang-regression"));

            // The reader lock is held throughout. Under the shared-cache bug the purge cannot make
            // progress and never completes within the bound; with the fix it finishes in milliseconds.
            var finished = await Task.WhenAny(purge, Task.Delay(TimeSpan.FromSeconds(10)));

            try
            {
                Assert.True(
                    ReferenceEquals(finished, purge) && purge.IsCompletedSuccessfully,
                    "Hard delete did not complete while a concurrent reader held a table read lock (shared-cache wedge regression).");
                Assert.Null(catalog.Get(victim.JobId));
            }
            finally
            {
                // Release the lock and let any still-running purge settle so the connection pool drains.
                heldReader.Dispose();
                readerCommand.Dispose();
                readerConnection.Dispose();
                await Task.WhenAny(purge, Task.Delay(TimeSpan.FromSeconds(15)));
            }
        }

        // Production-shaped load: worker-style slice writes and dashboard reads run on many connections
        // while a hard delete purges a different job. Everything must complete within the bound and no
        // concurrent writer may fail. The shared-cache handling wedges or starves writers here; the
        // private-cache + WAL handling lets the writers and the single purge interleave cleanly.
        [Fact]
        public async Task Hard_delete_under_concurrent_writes_and_reads_completes_without_wedging()
        {
            var catalog = new SqliteJobCatalogRepository(factory);
            var state = new SqliteSliceStateRepository(factory);
            var lifecycle = new SqliteJobLifecycleService(factory, catalog);

            catalog.Create(Schedule("live", "00:30:00"));
            var victim = catalog.Create(Schedule("victim", "00:30:00"));
            catalog.SetEnabled(victim.JobId, enabled: false, expectedVersion: victim.CatalogVersion);

            var liveJobId = JobId("live");
            const int writerCount = 6;
            const int iterations = 40;
            using var startGate = new ManualResetEventSlim(false);
            var faults = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

            var tasks = Enumerable.Range(0, writerCount).Select(w => Task.Run(() =>
            {
                startGate.Wait();
                try
                {
                    for (var i = 0; i < iterations; i++)
                    {
                        // Each (writer, iteration) writes a distinct slice, so writes never conflict on
                        // version yet still contend for the single SQLite writer, and reads interleave.
                        var start = At(1 + (w * iterations) + i);
                        state.Append($"op-{w}-{i}", liveJobId, start, start.AddMinutes(30), DurableSliceStatus.Queued, expectedVersion: 0);
                        _ = catalog.List();
                    }
                }
                catch (Exception ex)
                {
                    faults.Enqueue(ex);
                }
            })).ToList();

            tasks.Add(Task.Run(() =>
            {
                startGate.Wait();
                try
                {
                    lifecycle.HardDelete(victim.JobId, $"DELETE {victim.DisplayName}", actor: "test", reason: "hang-regression-load");
                }
                catch (Exception ex)
                {
                    faults.Enqueue(ex);
                }
            }));

            startGate.Set();
            var all = Task.WhenAll(tasks);
            var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(30)));

            Assert.True(ReferenceEquals(finished, all), "Concurrent reads/writes during a hard delete did not complete within 30s (connection-handling wedge regression).");
            await all;
            Assert.True(faults.IsEmpty, "Concurrent operations during a hard delete faulted: " + string.Join(" | ", faults.Select(f => f.GetType().Name + ": " + f.Message)));
            Assert.Null(catalog.Get(victim.JobId));
        }

        private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        private static string JobId(string activityId)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(activityId));
            return new Guid(bytes).ToString("N");
        }

        private static string Schedule(string activityId, string window) => $$"""
        {
          "id": "{{JobId(activityId)}}",
          "activityId": "{{activityId}}",
          "functionName": "StateFunction",
          "outputTable": "StateOutput",
          "queryWindowSize": "{{window}}",
          "delayFromUtcNow": "00:00:00",
          "maxParallelism": 1,
          "queryTimeout": "00:01:00",
          "isPaused": false,
          "startFrom": "2026-01-01T00:00:00Z",
          "dependsOn": [],
          "target": { "clusterUri": "https://ksr-example.invalid", "database": "DemoDb" }
        }
        """;
    }
}
