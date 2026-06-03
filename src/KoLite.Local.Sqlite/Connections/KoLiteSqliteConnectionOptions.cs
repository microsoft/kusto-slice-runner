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

        public int BusyTimeoutMilliseconds { get; init; } = 5_000;
    }
}
