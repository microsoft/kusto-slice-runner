// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Ksr.Local.Sqlite.Connections
{
    public sealed class KsrSqliteConnectionFactory : IKsrSqliteConnectionFactory
    {
        private readonly KsrSqliteConnectionOptions options;

        public KsrSqliteConnectionFactory(KsrSqliteConnectionOptions options)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public SqliteConnection OpenConnection()
        {
            var directory = Path.GetDirectoryName(options.DatabasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Private cache (the default) is deliberate. Shared cache routes every in-process
            // connection through one cache whose table-level locks surface contention as
            // SQLITE_LOCKED, which busy_timeout does NOT retry. That turned an ordinary overlap (a
            // hard-delete write while the dashboard/scheduler/worker held a read) into a permanent
            // wedge: the hard-delete page hung forever and the job was never purged. With a private
            // cache + WAL, readers never block the single writer and writer-vs-writer contention is
            // SQLITE_BUSY, which busy_timeout retries cleanly.
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = options.DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
            };

            var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            ApplyPragmas(connection, options.BusyTimeoutMilliseconds);
            return connection;
        }

        private static void ApplyPragmas(SqliteConnection connection, int busyTimeoutMilliseconds)
        {
            // Connection-scoped pragmas only. journal_mode = WAL is intentionally NOT set here: WAL is
            // a persistent database-header property, so setting it on every open (including read-only
            // dashboard/health paths) takes a write lock each time and needlessly widens write
            // contention. It is established once at startup in KsrSqliteSchema.EnsureSchema instead.
            //
            // A pooled connection can be handed back still inside a transaction if a prior owner left
            // one open (an untracked BEGIN, or a commit/rollback that failed under write contention).
            // `PRAGMA synchronous` (the "safety level") cannot be changed inside a transaction and
            // `PRAGMA foreign_keys` is silently ignored inside one, so reset to autocommit first.
            EnsureAutocommit(connection);
            ExecuteNonQuery(connection, $"PRAGMA busy_timeout = {Math.Max(0, busyTimeoutMilliseconds)};");
            ExecuteNonQuery(connection, "PRAGMA foreign_keys = ON;");
            ExecuteNonQuery(connection, "PRAGMA synchronous = NORMAL;");
        }

        private static void EnsureAutocommit(SqliteConnection connection)
        {
            // sqlite3_get_autocommit returns 0 only while a transaction is active. Checking it keeps
            // the common (clean) path free of an extra round-trip or a thrown-and-caught exception,
            // and rolling back a leftover transaction is safe: the pool hands this connection to a
            // single owner, and any transaction still open on it belongs to an operation that already
            // failed.
            if (connection.Handle is { } handle && raw.sqlite3_get_autocommit(handle) == 0)
            {
                ExecuteNonQuery(connection, "ROLLBACK;");
            }
        }

        private static void ExecuteNonQuery(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}
