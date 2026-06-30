namespace KoLite.Local.Sqlite.Connections
{
    public sealed record KoLiteSqliteConnectionOptions
    {
        public KoLiteSqliteConnectionOptions(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
            {
                throw new ArgumentException("Database path is required.", nameof(databasePath));
            }

            DatabasePath = Path.GetFullPath(databasePath);
        }

        public string DatabasePath { get; }

        // Generous default so a writer waits out a longer single-writer operation (for example a
        // hard-delete batch, or any pass that briefly holds the WAL writer) instead of failing with
        // SQLITE_BUSY. WAL allows one writer at a time; this is how long another writer's BEGIN
        // IMMEDIATE retries before giving up. Override via KoLite:Sqlite:BusyTimeoutMilliseconds.
        public int BusyTimeoutMilliseconds { get; init; } = 15_000;
    }
}
