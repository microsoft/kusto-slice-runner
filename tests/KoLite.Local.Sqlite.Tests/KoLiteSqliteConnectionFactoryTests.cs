// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.Local.Sqlite.Connections;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Tests
{
    public sealed class KoLiteSqliteConnectionFactoryTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "connection-factory-tests", Guid.NewGuid().ToString("N"));
        private readonly KoLiteSqliteConnectionFactory factory;

        public KoLiteSqliteConnectionFactoryTests()
        {
            Directory.CreateDirectory(testDirectory);
            factory = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(Path.Combine(testDirectory, "conn.db")) { BusyTimeoutMilliseconds = 10_000 });
        }

        public void Dispose()
        {
            TestCleanup.DeleteDirectoryWithRetry(testDirectory);
        }

        // A prior owner can hand a pooled connection back still inside a transaction (for example an
        // untracked BEGIN, or a commit/rollback that failed under write contention). ApplyPragmas then
        // runs `PRAGMA synchronous` on reuse, which SQLite rejects inside a transaction ("Safety level
        // may not be changed inside a transaction"). OpenConnection must recover the connection instead
        // of throwing.
        [Fact]
        public void OpenConnection_recovers_a_pooled_connection_left_in_a_transaction()
        {
            // Poison the pool: start a transaction the pool will not roll back on return (a raw BEGIN is
            // not tracked by SqliteTransaction, so Dispose leaves it open on the pooled connection).
            var poisoned = factory.OpenConnection();
            using (var begin = poisoned.CreateCommand())
            {
                begin.CommandText = "BEGIN;";
                begin.ExecuteNonQuery();
            }

            poisoned.Dispose();

            // Reuse must succeed and hand back a usable, autocommit connection.
            using var reused = factory.OpenConnection();
            using var probe = reused.CreateCommand();
            probe.CommandText = "SELECT 1;";
            Assert.Equal(1L, Convert.ToInt64(probe.ExecuteScalar()));
        }
    }
}
