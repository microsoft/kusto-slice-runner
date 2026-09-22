// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Infrastructure
{
    // Small bounded retry for transient SQLite failures. On a single-writer WAL database under high
    // concurrency (many workers plus the scheduler reading the same slice-state rows), an individual
    // statement can occasionally fail with a transient result code — most notably SQLITE_ABORT
    // ("query aborted"), which busy_timeout does NOT cover. These are not logical errors: re-running
    // the same statement almost always succeeds immediately. Use this only for operations that are
    // safe to re-run (pure reads, or idempotent writes).
    internal static class SqliteTransientRetry
    {
        // Primary SQLite result codes treated as transient/retryable:
        // 4 SQLITE_ABORT, 5 SQLITE_BUSY, 6 SQLITE_LOCKED, 9 SQLITE_INTERRUPT, 10 SQLITE_IOERR.
        private static readonly int[] TransientPrimaryCodes = { 4, 5, 6, 9, 10 };

        internal static T Execute<T>(Func<T> operation, int maxAttempts = 3)
        {
            ArgumentNullException.ThrowIfNull(operation);
            if (maxAttempts < 1)
            {
                maxAttempts = 1;
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return operation();
                }
                catch (SqliteException ex) when (attempt < maxAttempts && IsTransient(ex))
                {
                    Thread.Sleep(BackoffFor(attempt));
                }
            }
        }

        internal static void Execute(Action operation, int maxAttempts = 3)
            => Execute(
                () =>
                {
                    operation();
                    return true;
                },
                maxAttempts);

        internal static bool IsTransient(SqliteException ex) => Array.IndexOf(TransientPrimaryCodes, ex.SqliteErrorCode) >= 0;

        private static TimeSpan BackoffFor(int attempt) => TimeSpan.FromMilliseconds(50 * attempt);
    }
}
