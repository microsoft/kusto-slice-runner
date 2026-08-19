namespace KoLite.LocalApp.Retention
{
    // The latest outcome of the retention background service, surfaced by /api/v1/system/status. Captured
    // even on failure so operators can see when retention last ran and whether it errored.
    public sealed record RetentionSnapshot(
        bool Enabled,
        DateTimeOffset? LastRunUtc,
        int LogsDeleted,
        int AttemptsDeleted,
        int ScheduledSlicesDeleted,
        int IngestionThrottlesDeleted,
        int QueueRowsDeleted,
        int TotalDeleted,
        string? LastError)
    {
        public static RetentionSnapshot Initial(bool enabled) =>
            new(enabled, LastRunUtc: null, 0, 0, 0, 0, 0, 0, LastError: null);
    }

    public sealed class RetentionRuntimeState
    {
        private readonly object gate = new();
        private RetentionSnapshot snapshot;

        public RetentionRuntimeState(RetentionSnapshot initial)
        {
            snapshot = initial;
        }

        public RetentionSnapshot GetSnapshot()
        {
            lock (gate)
            {
                return snapshot;
            }
        }

        public void Update(RetentionSnapshot next)
        {
            lock (gate)
            {
                snapshot = next;
            }
        }
    }
}
