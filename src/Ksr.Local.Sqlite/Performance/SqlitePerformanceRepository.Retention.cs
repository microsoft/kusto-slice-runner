// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Sqlite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Ksr.Local.Sqlite.Performance
{
    public sealed partial class SqlitePerformanceRepository
    {
        internal static int CleanupOldAttempts(SqliteConnection connection, DateTimeOffset cutoffUtc, int batchSize, DateTimeOffset nowUtc)
        {
            using (var floor = SqliteStorage.Command(connection, null, """
                UPDATE performance_collection_state
                SET retained_from_utc=MAX($cutoff, COALESCE(retained_from_utc, $cutoff))
                WHERE singleton=1;
                """))
            {
                floor.Add("$cutoff", SqliteStorage.Utc(cutoffUtc));
                floor.ExecuteNonQuery();
            }

            var total = 0;
            for (var batch = 0; batch < 1_000_000; batch++)
            {
                using var transaction = connection.BeginTransaction();
                using var command = SqliteStorage.Command(connection, transaction, """
                    DELETE FROM performance_attempts WHERE attempt_id IN (
                        SELECT p.attempt_id FROM performance_attempts p
                        WHERE p.completed_at_utc < $cutoff
                           OR (p.completed_at_utc IS NULL AND COALESCE(p.started_at_utc,p.recorded_at_utc) < $cutoff
                               AND NOT EXISTS (
                                   SELECT 1 FROM work_queue q
                                   WHERE q.job_id=p.job_id AND q.slice_start_utc=p.slice_start_utc AND q.slice_end_utc=p.slice_end_utc
                                     AND q.chunk_id IS p.chunk_id AND q.state='Leased' AND q.locked_until_utc > $now)
                               AND NOT EXISTS (
                                   SELECT 1 FROM current_slice_state s
                                   WHERE p.chunk_id IS NULL AND s.job_id=p.job_id
                                     AND s.slice_start_utc=p.slice_start_utc AND s.slice_end_utc=p.slice_end_utc
                                     AND s.state='Running' AND s.lease_expires_at_utc > $now)
                               AND NOT EXISTS (
                                   SELECT 1 FROM current_slice_chunk_state s
                                   WHERE s.job_id=p.job_id AND s.slice_start_utc=p.slice_start_utc AND s.slice_end_utc=p.slice_end_utc
                                     AND s.chunk_id=p.chunk_id AND s.state='Running' AND s.lease_expires_at_utc > $now))
                        LIMIT $take
                    );
                    """);
                command.Add("$cutoff", SqliteStorage.Utc(cutoffUtc));
                command.Add("$now", SqliteStorage.Utc(nowUtc));
                command.Add("$take", batchSize);
                var deleted = command.ExecuteNonQuery();
                transaction.Commit();
                total += deleted;
                if (deleted < batchSize)
                {
                    break;
                }
            }

            return total;
        }
    }
}
