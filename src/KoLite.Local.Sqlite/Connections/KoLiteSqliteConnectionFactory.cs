using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Connections
{
    public sealed class KoLiteSqliteConnectionFactory : IKoLiteSqliteConnectionFactory
    {
        private readonly KoLiteSqliteConnectionOptions options;

        public KoLiteSqliteConnectionFactory(KoLiteSqliteConnectionOptions options)
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
            // contention. It is established once at startup in KoLiteSqliteMigrator.Migrate instead.
            ExecuteNonQuery(connection, $"PRAGMA busy_timeout = {Math.Max(0, busyTimeoutMilliseconds)};");
            ExecuteNonQuery(connection, "PRAGMA foreign_keys = ON;");
            ExecuteNonQuery(connection, "PRAGMA synchronous = NORMAL;");
        }

        private static void ExecuteNonQuery(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}
