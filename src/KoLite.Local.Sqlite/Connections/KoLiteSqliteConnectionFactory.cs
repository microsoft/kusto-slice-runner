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

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = options.DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
            };

            var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            ApplyPragmas(connection, options.BusyTimeoutMilliseconds);
            return connection;
        }

        private static void ApplyPragmas(SqliteConnection connection, int busyTimeoutMilliseconds)
        {
            ExecuteNonQuery(connection, $"PRAGMA busy_timeout = {Math.Max(0, busyTimeoutMilliseconds)};");
            ExecuteNonQuery(connection, "PRAGMA foreign_keys = ON;");
            ExecuteNonQuery(connection, "PRAGMA journal_mode = WAL;");
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
