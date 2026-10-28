// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Sqlite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Ksr.Local.Sqlite.Repair
{
    internal static class SqliteRepairHistory
    {
        internal static void AttachUnchunked(SqliteConnection connection, SqliteTransaction transaction, string batchId, string sliceId, string queueItemId)
        {
            using var command = SqliteStorage.Command(connection, transaction, """
                INSERT INTO repair_slices (
                    repair_slice_id,repair_batch_id,job_id,slice_start_utc,slice_end_utc,
                    status,enqueued_queue_item_id,created_at_utc,updated_at_utc)
                SELECT $slice,$batch,job_id,slice_start_utc,slice_end_utc,
                       CASE state WHEN 'Completed' THEN 'Completed' WHEN 'DeadLettered' THEN 'Failed' ELSE 'Queued' END,
                       queue_item_id,created_at_utc,updated_at_utc
                FROM work_queue WHERE queue_item_id=$queue AND chunk_id IS NULL
                ON CONFLICT(repair_slice_id) DO UPDATE SET
                    status=CASE WHEN repair_slices.status IN ('Completed','Failed') THEN repair_slices.status ELSE excluded.status END,
                    enqueued_queue_item_id=excluded.enqueued_queue_item_id,
                    updated_at_utc=CASE WHEN repair_slices.status IN ('Completed','Failed') THEN repair_slices.updated_at_utc ELSE excluded.updated_at_utc END;
                """);
            command.Add("$slice", sliceId);
            command.Add("$batch", batchId);
            command.Add("$queue", queueItemId);
            command.ExecuteNonQuery();
        }

        internal static void RecordTerminal(SqliteConnection connection, SqliteTransaction transaction, string queueItemId)
        {
            using (var command = SqliteStorage.Command(connection, transaction, """
                UPDATE repair_slices
                SET status=CASE (SELECT state FROM work_queue WHERE queue_item_id=$id)
                               WHEN 'Completed' THEN 'Completed' ELSE 'Failed' END,
                    updated_at_utc=(SELECT updated_at_utc FROM work_queue WHERE queue_item_id=$id)
                WHERE enqueued_queue_item_id=$id AND status IN ('Planned','Queued')
                  AND EXISTS (
                      SELECT 1 FROM work_queue work
                      WHERE work.queue_item_id=$id AND work.chunk_id IS NULL
                        AND work.state IN ('Completed','DeadLettered')
                        AND work.job_id=repair_slices.job_id
                        AND work.slice_start_utc=repair_slices.slice_start_utc
                        AND work.slice_end_utc=repair_slices.slice_end_utc)
                  AND NOT EXISTS (
                      SELECT 1 FROM repair_chunk_executions child
                      WHERE child.repair_batch_id=repair_slices.repair_batch_id
                        AND child.job_id=repair_slices.job_id
                        AND child.slice_start_utc=repair_slices.slice_start_utc
                        AND child.slice_end_utc=repair_slices.slice_end_utc);

                UPDATE repair_chunk_executions
                SET status=CASE (SELECT state FROM work_queue WHERE queue_item_id=$id)
                               WHEN 'Completed' THEN 'Completed' ELSE 'Failed' END,
                    updated_at_utc=(SELECT updated_at_utc FROM work_queue WHERE queue_item_id=$id)
                WHERE enqueued_queue_item_id=$id AND status IN ('Planned','Queued')
                  AND EXISTS (SELECT 1 FROM work_queue WHERE queue_item_id=$id AND state IN ('Completed','DeadLettered'));

                UPDATE repair_slices
                SET status=CASE
                        WHEN status='Failed' OR EXISTS (
                            SELECT 1 FROM repair_chunk_executions child
                            WHERE child.repair_batch_id=repair_slices.repair_batch_id
                              AND child.job_id=repair_slices.job_id
                              AND child.slice_start_utc=repair_slices.slice_start_utc
                              AND child.slice_end_utc=repair_slices.slice_end_utc
                              AND child.status='Failed') THEN 'Failed'
                        WHEN NOT EXISTS (
                            SELECT 1 FROM repair_chunk_executions child
                            WHERE child.repair_batch_id=repair_slices.repair_batch_id
                              AND child.job_id=repair_slices.job_id
                              AND child.slice_start_utc=repair_slices.slice_start_utc
                              AND child.slice_end_utc=repair_slices.slice_end_utc
                              AND child.status NOT IN ('Completed','Failed')) THEN 'Completed'
                        ELSE 'Queued' END,
                    updated_at_utc=(
                        SELECT MAX(child.updated_at_utc) FROM repair_chunk_executions child
                        WHERE child.repair_batch_id=repair_slices.repair_batch_id
                          AND child.job_id=repair_slices.job_id
                          AND child.slice_start_utc=repair_slices.slice_start_utc
                          AND child.slice_end_utc=repair_slices.slice_end_utc)
                WHERE status IN ('Planned','Queued','Failed')
                  AND repair_batch_id IN (SELECT repair_batch_id FROM repair_chunk_executions WHERE enqueued_queue_item_id=$id)
                  AND EXISTS (
                    SELECT 1 FROM repair_chunk_executions child
                    WHERE child.enqueued_queue_item_id=$id
                      AND child.repair_batch_id=repair_slices.repair_batch_id
                      AND child.job_id=repair_slices.job_id
                      AND child.slice_start_utc=repair_slices.slice_start_utc
                      AND child.slice_end_utc=repair_slices.slice_end_utc);
                """))
            {
                command.Add("$id", queueItemId);
                command.ExecuteNonQuery();
            }

            var batches = new List<string>();
            using (var command = SqliteStorage.Command(connection, transaction, """
                SELECT repair_batch_id FROM repair_slices WHERE enqueued_queue_item_id=$id
                UNION
                SELECT repair_batch_id FROM repair_chunk_executions WHERE enqueued_queue_item_id=$id;
                """))
            {
                command.Add("$id", queueItemId);
                using var reader = command.ExecuteReader();
                while (reader.Read()) batches.Add(reader.GetString(0));
            }
            foreach (var batch in batches) RefreshBatch(connection, transaction, batch);
        }

        internal static void RefreshBatch(SqliteConnection connection, SqliteTransaction transaction, string batchId)
        {
            // Planned is the enqueue barrier: a worker may finish before the planner publishes all slices.
            using var command = SqliteStorage.Command(connection, transaction, """
                UPDATE repair_batches
                SET status=CASE
                        WHEN status='Failed' OR EXISTS (
                            SELECT 1 FROM repair_slices slice
                            WHERE slice.repair_batch_id=$batch AND slice.status='Failed') THEN 'Failed'
                        WHEN NOT EXISTS (
                            SELECT 1 FROM repair_slices slice
                            WHERE slice.repair_batch_id=$batch AND slice.status IN ('Planned','Queued','Blocked'))
                         AND NOT EXISTS (
                            SELECT 1 FROM repair_chunk_executions child
                            WHERE child.repair_batch_id=$batch AND child.status NOT IN ('Completed','Failed')) THEN 'Completed'
                        ELSE 'Queued' END,
                    completed_at_utc=CASE
                        WHEN NOT EXISTS (
                            SELECT 1 FROM repair_slices slice
                            WHERE slice.repair_batch_id=$batch AND slice.status IN ('Planned','Queued','Blocked'))
                         AND NOT EXISTS (
                            SELECT 1 FROM repair_chunk_executions child
                            WHERE child.repair_batch_id=$batch AND child.status NOT IN ('Completed','Failed'))
                        THEN COALESCE(completed_at_utc, (
                            SELECT MAX(slice.updated_at_utc) FROM repair_slices slice WHERE slice.repair_batch_id=$batch))
                        ELSE completed_at_utc END
                WHERE repair_batch_id=$batch AND status IN ('Queued','Running','Failed')
                  AND EXISTS (SELECT 1 FROM repair_slices slice WHERE slice.repair_batch_id=$batch);
                """);
            command.Add("$batch", batchId);
            command.ExecuteNonQuery();
        }

        internal static void ReconcileUnchunked(SqliteConnection connection)
        {
            string? cursor = null;
            while (true)
            {
                using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
                var rows = new List<(string SliceId, string QueueId)>();
                using (var command = SqliteStorage.Command(connection, transaction, """
                    SELECT slice.repair_slice_id,work.queue_item_id
                    FROM repair_slices slice
                    JOIN repair_batches batch ON batch.repair_batch_id=slice.repair_batch_id
                    JOIN work_queue work ON work.queue_item_id=slice.enqueued_queue_item_id
                    WHERE slice.status='Queued' AND batch.status IN ('Queued','Running','Failed')
                      AND work.chunk_id IS NULL AND work.state IN ('Completed','DeadLettered')
                      AND work.job_id=slice.job_id
                      AND work.slice_start_utc=slice.slice_start_utc AND work.slice_end_utc=slice.slice_end_utc
                      AND ($cursor IS NULL OR slice.repair_slice_id > $cursor)
                      AND NOT EXISTS (
                          SELECT 1 FROM repair_chunk_executions child
                          WHERE child.repair_batch_id=slice.repair_batch_id AND child.job_id=slice.job_id
                            AND child.slice_start_utc=slice.slice_start_utc AND child.slice_end_utc=slice.slice_end_utc)
                    ORDER BY slice.repair_slice_id LIMIT 200;
                    """))
                {
                    command.Add("$cursor", cursor);
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
                }
                foreach (var row in rows) RecordTerminal(connection, transaction, row.QueueId);
                transaction.Commit();
                if (rows.Count == 0) return;
                cursor = rows[^1].SliceId;
            }
        }
    }
}
