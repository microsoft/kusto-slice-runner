using System.Globalization;
using KoLite.Local.Core.Schedules;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Migrations;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Tests
{
    // Exercises migration v6 (the activityId -> GUID re-key) against a synthetic schema-v5
    // database, and optionally rehearses it against a copy of a real database when
    // KOLITE_REHEARSAL_DB points at one.
    public sealed class KoLiteSqliteMigratorRekeyTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "rekey-migration-tests", Guid.NewGuid().ToString("N"));

        public KoLiteSqliteMigratorRekeyTests()
        {
            Directory.CreateDirectory(testDirectory);
        }

        [Fact]
        public void Rekeys_existing_activity_id_data_to_guid_in_place()
        {
            var dbPath = Path.Combine(testDirectory, "synthetic-v5.db");
            var factory = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(dbPath) { BusyTimeoutMilliseconds = 10_000 });
            var migrator = new KoLiteSqliteMigrator(factory);

            // 1. Build a schema-v5 database (pre-re-key) and seed activityId-keyed rows.
            using (var connection = factory.OpenConnection())
            {
                migrator.Migrate(connection, throughVersion: 5);
                Assert.Equal("5", ReadScalar(connection, "SELECT value FROM app_metadata WHERE key='schema_version';"));

                Exec(connection, """
                    INSERT INTO job_definitions (job_id, display_name, query_ref, schedule_json, parameters_json, is_enabled, catalog_version)
                    VALUES ('job.alpha', 'job.alpha', 'AlphaFn', $alpha, '{}', 1, 1),
                           ('job.beta', 'job.beta', 'BetaFn', $beta, '{}', 1, 1);
                    """,
                    ("$alpha", ScheduleJson("job.alpha")),
                    ("$beta", ScheduleJson("job.beta", dependsOnActivityId: "job.alpha")));

                Exec(connection, """
                    INSERT INTO current_slice_state (job_id, slice_start_utc, slice_end_utc, state, attempt)
                    VALUES ('job.alpha', '2026-01-01T00:00:00.0000000Z', '2026-01-01T01:00:00.0000000Z', 'Completed', 0);
                    """);

                Exec(connection, """
                    INSERT INTO work_queue (queue_item_id, job_id, slice_start_utc, slice_end_utc, state, available_at_utc, idempotency_key, payload_json)
                    VALUES ('q1', 'job.alpha', '2026-01-01T00:00:00.0000000Z', '2026-01-01T01:00:00.0000000Z', 'Completed', '2026-01-01T00:00:00.0000000Z',
                            'normal|job.alpha|2026-01-01T00:00:00.0000000Z|2026-01-01T01:00:00.0000000Z',
                            '{"jobId":"job.alpha","sliceKey":"job.alpha|2026-01-01T00:00:00.0000000Z|2026-01-01T01:00:00.0000000Z"}');
                    """);

                Exec(connection, """
                    INSERT INTO system_audit (audit_id, action, subject_type, subject_id, payload_json)
                    VALUES ('a1', 'Created', 'Job', 'job.alpha', '{}');
                    """);
            }

            // 2. Apply the full migrator (adds v6).
            migrator.Migrate();

            // 3. Verify the re-key.
            using (var connection = factory.OpenConnection())
            {
                Assert.Equal("6", ReadScalar(connection, "SELECT value FROM app_metadata WHERE key='schema_version';"));

                var alphaId = ReadScalar(connection, "SELECT job_id FROM job_definitions WHERE activity_id='job.alpha';");
                var betaId = ReadScalar(connection, "SELECT job_id FROM job_definitions WHERE activity_id='job.beta';");
                Assert.True(Guid.TryParse(alphaId, out _), $"alpha job_id should be a GUID, got '{alphaId}'.");
                Assert.True(Guid.TryParse(betaId, out _), $"beta job_id should be a GUID, got '{betaId}'.");
                Assert.NotEqual(alphaId, betaId);

                // No activityId-keyed rows survive anywhere.
                Assert.Equal("0", ReadScalar(connection, "SELECT COUNT(*) FROM job_definitions WHERE job_id IN ('job.alpha','job.beta');"));
                Assert.Equal("0", ReadScalar(connection, "SELECT COUNT(*) FROM job_definitions WHERE activity_id IS NULL OR activity_id='';"));

                // FK columns re-keyed to the GUID.
                Assert.Equal(alphaId, ReadScalar(connection, "SELECT job_id FROM current_slice_state LIMIT 1;"));
                Assert.Equal(alphaId, ReadScalar(connection, "SELECT job_id FROM work_queue LIMIT 1;"));
                Assert.Equal(alphaId, ReadScalar(connection, "SELECT subject_id FROM system_audit WHERE audit_id='a1';"));

                // Embedded strings re-keyed (anchored), and old token gone.
                var idempotencyKey = ReadScalar(connection, "SELECT idempotency_key FROM work_queue WHERE queue_item_id='q1';");
                Assert.Equal($"normal|{alphaId}|2026-01-01T00:00:00.0000000Z|2026-01-01T01:00:00.0000000Z", idempotencyKey);
                var payload = ReadScalar(connection, "SELECT payload_json FROM work_queue WHERE queue_item_id='q1';");
                Assert.Contains($"\"jobId\":\"{alphaId}\"", payload);
                Assert.Contains($"\"sliceKey\":\"{alphaId}|", payload);
                Assert.DoesNotContain("job.alpha", payload);

                // dependsOn rewritten from activityId to the upstream GUID, and parses with Id set.
                var betaJson = ReadScalar(connection, "SELECT schedule_json FROM job_definitions WHERE activity_id='job.beta';");
                var parsed = ScheduleParser.Parse(betaJson);
                Assert.True(parsed.IsValid, "Migrated beta schedule JSON should parse.");
                Assert.Equal(betaId, parsed.Definition!.Id);
                var edge = Assert.Single(parsed.Definition.DependsOn);
                Assert.Equal(alphaId, edge.Id);

                // No orphan foreign keys after the re-key.
                Assert.Empty(ForeignKeyViolations(connection));
            }
        }

        [Fact]
        public void Rehearses_rekey_on_a_copy_of_a_real_database_when_requested()
        {
            var source = Environment.GetEnvironmentVariable("KOLITE_REHEARSAL_DB");
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            {
                return; // Opt-in rehearsal; no-op in normal/CI runs.
            }

            var copyPath = Path.Combine(testDirectory, "rehearsal-copy.db");
            File.Copy(source, copyPath, overwrite: true);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(source + suffix))
                {
                    File.Copy(source + suffix, copyPath + suffix, overwrite: true);
                }
            }

            var factory = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(copyPath) { BusyTimeoutMilliseconds = 30_000 });
            int jobCountBefore;
            using (var connection = factory.OpenConnection())
            {
                jobCountBefore = int.Parse(ReadScalar(connection, "SELECT COUNT(*) FROM job_definitions;"), CultureInfo.InvariantCulture);
            }

            new KoLiteSqliteMigrator(factory).Migrate();

            using (var connection = factory.OpenConnection())
            {
                Assert.Equal("6", ReadScalar(connection, "SELECT value FROM app_metadata WHERE key='schema_version';"));
                Assert.Equal(jobCountBefore.ToString(CultureInfo.InvariantCulture), ReadScalar(connection, "SELECT COUNT(*) FROM job_definitions;"));
                Assert.Equal("0", ReadScalar(connection, "SELECT COUNT(*) FROM job_definitions WHERE activity_id IS NULL OR activity_id='';"));
                Assert.Equal("0", ReadScalar(connection, "SELECT COUNT(*) FROM job_definitions WHERE job_id NOT GLOB '[0-9a-f]*' OR length(job_id) <> 32;"));
                Assert.Empty(ForeignKeyViolations(connection));
            }
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }

        private static string ScheduleJson(string activityId, string? dependsOnActivityId = null) => $$"""
            {
              "activityId": "{{activityId}}",
              "functionName": "Fn",
              "outputTable": "Output",
              "queryWindowSize": "01:00:00",
              "delayFromUtcNow": "00:00:00",
              "maxParallelism": 1,
              "queryTimeout": "00:05:00",
              "isPaused": false,
              "startFrom": "2026-01-01T00:00:00Z",
              "dependsOn": {{(dependsOnActivityId is null ? "[]" : $"[{{ \"activityId\": \"{dependsOnActivityId}\" }}]")}},
              "target": { "clusterUri": "https://kolite-example.invalid", "database": "DemoDb" }
            }
            """;

        private static void Exec(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            command.ExecuteNonQuery();
        }

        private static string ReadScalar(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static IReadOnlyList<string> ForeignKeyViolations(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_key_check;";
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                rows.Add($"{reader.GetValue(0)}|{reader.GetValue(1)}|{reader.GetValue(2)}|{reader.GetValue(3)}");
            }

            return rows;
        }
    }
}
