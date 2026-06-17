using System.Text.Json;
using System.Text.Json.Nodes;
using KoLite.Local.Core.Schedules;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Catalog
{
    public sealed record JobCatalogRecord(string JobId, string DisplayName, string? Description, string? QueryRef, string ScheduleJson, string ParametersJson, bool IsEnabled, long CatalogVersion, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc)
    {
        public JobDefinition Definition => ScheduleParser.Parse(ScheduleJson).Definition ?? throw new InvalidOperationException($"Stored schedule for '{JobId}' is invalid.");
    }

    public sealed record JobDefinitionEventRecord(string EventId, string JobId, long CatalogVersion, string EventType, string PayloadJson, DateTimeOffset RecordedAtUtc);

    public sealed record JobCatalogImportItemResult(string JobId, string Action, long CatalogVersion);

    public sealed record JobCatalogImportResult(int Created, int Updated, IReadOnlyList<JobCatalogImportItemResult> Items)
    {
        public int Total => Created + Updated;
    }

    public sealed class SqliteJobCatalogRepository
    {
        private readonly IKoLiteSqliteConnectionFactory connectionFactory;

        public SqliteJobCatalogRepository(IKoLiteSqliteConnectionFactory connectionFactory) => this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

        public JobCatalogRecord Create(string scheduleJson, string? actor = null, string? eventId = null)
        {
            var (definition, canonical) = Validate(scheduleJson);
            using var connection = connectionFactory.OpenConnection();
            using var transaction = connection.BeginTransaction();
            if (Exists(connection, transaction, definition.ActivityId)) throw new InvalidOperationException($"Job '{definition.ActivityId}' already exists.");
            var now = DateTimeOffset.UtcNow;
            InsertJob(connection, transaction, definition, canonical, catalogVersion: 1, now);
            InsertEvent(connection, transaction, eventId ?? Guid.NewGuid().ToString("N"), definition.ActivityId, 1, "Created", canonical, actor);
            transaction.Commit();
            return Get(definition.ActivityId)!;
        }

        public JobCatalogRecord Update(string jobId, string scheduleJson, long expectedVersion, string? actor = null, string? eventId = null)
        {
            var (definition, canonical) = Validate(scheduleJson);
            if (!StringComparer.Ordinal.Equals(jobId, definition.ActivityId)) throw new InvalidOperationException("Updated schedule activityId must match the target job id.");
            using var connection = connectionFactory.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var current = Get(connection, transaction, jobId) ?? throw new InvalidOperationException($"Job '{jobId}' does not exist.");
            if (current.CatalogVersion != expectedVersion) throw new InvalidOperationException($"Catalog version conflict for '{jobId}'. Expected {expectedVersion}, found {current.CatalogVersion}.");
            EnsureMutationAllowed(current, definition, HasStarted(connection, transaction, jobId));
            var newVersion = current.CatalogVersion + 1;
            UpdateJob(connection, transaction, jobId, definition, canonical, newVersion, expectedVersion, DateTimeOffset.UtcNow);
            InsertEvent(connection, transaction, eventId ?? Guid.NewGuid().ToString("N"), jobId, newVersion, "Updated", canonical, actor);
            transaction.Commit();
            return Get(jobId)!;
        }

        public JobCatalogRecord SetEnabled(string jobId, bool enabled, long expectedVersion, string? actor = null, string? eventId = null)
        {
            using var connection = connectionFactory.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var current = Get(connection, transaction, jobId) ?? throw new InvalidOperationException($"Job '{jobId}' does not exist.");
            if (current.CatalogVersion != expectedVersion) throw new InvalidOperationException($"Catalog version conflict for '{jobId}'. Expected {expectedVersion}, found {current.CatalogVersion}.");
            var newVersion = current.CatalogVersion + 1;
            using (var update = SqliteStorage.Command(connection, transaction, "UPDATE job_definitions SET is_enabled = $enabled, catalog_version = $version, updated_at_utc = $updated_at WHERE job_id = $job_id AND catalog_version = $expected;"))
            {
                update.Add("$enabled", enabled ? 1 : 0);
                update.Add("$version", newVersion);
                update.Add("$updated_at", SqliteStorage.Utc(DateTimeOffset.UtcNow));
                update.Add("$job_id", jobId);
                update.Add("$expected", expectedVersion);
                if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException($"Catalog version conflict for '{jobId}'.");
            }

            InsertEvent(connection, transaction, eventId ?? Guid.NewGuid().ToString("N"), jobId, newVersion, enabled ? "Enabled" : "Disabled", current.ScheduleJson, actor);
            transaction.Commit();
            return Get(jobId)!;
        }

        public JobCatalogImportResult Import(string importJson, string? actor = null)
        {
            var parsed = ScheduleImportParser.Parse(importJson);
            if (!parsed.IsValid)
            {
                throw new InvalidOperationException("Schedule JSON is invalid: " + FormatErrors(parsed.Errors));
            }

            var validatedItems = parsed.Items.Select(item =>
            {
                var (definition, canonical) = Validate(item.ScheduleJson);
                return (item.Index, Definition: definition, CanonicalJson: canonical);
            }).ToArray();

            using var connection = connectionFactory.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var itemResults = new List<JobCatalogImportItemResult>();
            var created = 0;
            var updated = 0;
            var currentItems = new List<(int Index, JobDefinition Definition, string CanonicalJson, JobCatalogRecord? Current)>();
            var mutationErrors = new List<ScheduleValidationError>();

            foreach (var item in validatedItems)
            {
                var current = Get(connection, transaction, item.Definition.ActivityId);
                currentItems.Add((item.Index, item.Definition, item.CanonicalJson, current));
                if (current is not null)
                {
                    mutationErrors.AddRange(MutationErrors(current, item.Definition, HasStarted(connection, transaction, current.JobId), $"[{item.Index}]."));
                }
            }

            if (mutationErrors.Count > 0)
            {
                throw new InvalidOperationException("Schedule JSON changes read-only started-job fields: " + FormatErrors(mutationErrors));
            }

            foreach (var item in currentItems)
            {
                var current = item.Current;
                if (current is null)
                {
                    InsertJob(connection, transaction, item.Definition, item.CanonicalJson, catalogVersion: 1, DateTimeOffset.UtcNow);
                    InsertEvent(connection, transaction, Guid.NewGuid().ToString("N"), item.Definition.ActivityId, 1, "Created", item.CanonicalJson, actor);
                    itemResults.Add(new JobCatalogImportItemResult(item.Definition.ActivityId, "Created", 1));
                    created++;
                    continue;
                }

                var newVersion = current.CatalogVersion + 1;
                UpdateJob(connection, transaction, current.JobId, item.Definition, item.CanonicalJson, newVersion, current.CatalogVersion, DateTimeOffset.UtcNow);
                InsertEvent(connection, transaction, Guid.NewGuid().ToString("N"), current.JobId, newVersion, "Updated", item.CanonicalJson, actor);
                itemResults.Add(new JobCatalogImportItemResult(current.JobId, "Updated", newVersion));
                updated++;
            }

            transaction.Commit();
            return new JobCatalogImportResult(created, updated, itemResults);
        }

        public JobCatalogRecord? Get(string jobId)
        {
            using var connection = connectionFactory.OpenConnection();
            return Get(connection, null, jobId);
        }

        public IReadOnlyList<JobCatalogRecord> List(bool enabledOnly = false)
        {
            using var connection = connectionFactory.OpenConnection();
            using var command = SqliteStorage.Command(connection, null, enabledOnly
                ? "SELECT * FROM job_definitions WHERE is_enabled = 1 ORDER BY job_id;"
                : "SELECT * FROM job_definitions ORDER BY job_id;");
            using var reader = command.ExecuteReader();
            var records = new List<JobCatalogRecord>();
            while (reader.Read()) records.Add(ReadJob(reader));
            return records;
        }

        public IReadOnlyList<JobDefinitionEventRecord> History(string jobId)
        {
            using var connection = connectionFactory.OpenConnection();
            using var command = SqliteStorage.Command(connection, null, "SELECT * FROM job_definition_events WHERE job_id = $job_id ORDER BY catalog_version, recorded_at_utc;");
            command.Add("$job_id", jobId);
            using var reader = command.ExecuteReader();
            var records = new List<JobDefinitionEventRecord>();
            while (reader.Read())
            {
                records.Add(new JobDefinitionEventRecord(
                    reader.GetString(reader.GetOrdinal("event_id")),
                    reader.GetString(reader.GetOrdinal("job_id")),
                    reader.GetInt64(reader.GetOrdinal("catalog_version")),
                    reader.GetString(reader.GetOrdinal("event_type")),
                    reader.GetString(reader.GetOrdinal("payload_json")),
                    SqliteStorage.ReadUtc(reader, "recorded_at_utc")));
            }

            return records;
        }

        public string Export(string jobId) => Get(jobId)?.ScheduleJson ?? throw new InvalidOperationException($"Job '{jobId}' does not exist.");

        public string ExportAll(IReadOnlySet<string>? excludedJobIds = null)
        {
            var records = List();
            var included = excludedJobIds is null || excludedJobIds.Count == 0
                ? records
                : records.Where(record => !excludedJobIds.Contains(record.JobId));
            return SerializeAsImportArray(included);
        }

        public string ExportSelected(IReadOnlyCollection<string> jobIds)
        {
            var selected = jobIds is null || jobIds.Count == 0
                ? new HashSet<string>(StringComparer.Ordinal)
                : jobIds.ToHashSet(StringComparer.Ordinal);
            var included = selected.Count == 0
                ? Enumerable.Empty<JobCatalogRecord>()
                : List().Where(record => selected.Contains(record.JobId));
            return SerializeAsImportArray(included);
        }

        private static string SerializeAsImportArray(IEnumerable<JobCatalogRecord> records) =>
            "[" + string.Join(",", records
                .OrderBy(record => record.JobId, StringComparer.Ordinal)
                .Select(record => record.ScheduleJson)) + "]";

        public bool HasStarted(string jobId)
        {
            using var connection = connectionFactory.OpenConnection();
            return HasStarted(connection, null, jobId);
        }

        private static (JobDefinition Definition, string CanonicalJson) Validate(string scheduleJson)
        {
            string canonical;
            try
            {
                canonical = SqliteStorage.CanonicalJson(scheduleJson);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Schedule JSON is invalid: $: " + ex.Message, ex);
            }

            var parsed = ScheduleParser.Parse(canonical);
            if (!parsed.IsValid || parsed.Definition is null)
            {
                throw new InvalidOperationException("Schedule JSON is invalid: " + FormatErrors(parsed.Errors));
            }

            canonical = NormalizeTagsInCanonicalJson(canonical, parsed.Definition.Tags);
            return (parsed.Definition, canonical);
        }

        private static string NormalizeTagsInCanonicalJson(string canonicalJson, IReadOnlyList<string> tags)
        {
            var root = JsonNode.Parse(canonicalJson)?.AsObject() ?? throw new InvalidOperationException("Schedule JSON is invalid: root must be an object.");
            if (!root.ContainsKey("tags"))
            {
                return canonicalJson;
            }

            if (tags.Count == 0)
            {
                root.Remove("tags");
            }
            else
            {
                var array = new JsonArray();
                foreach (var tag in tags)
                {
                    array.Add(tag);
                }

                root["tags"] = array;
            }

            return root.ToJsonString(SqliteStorage.JsonOptions);
        }

        private static string FormatErrors(IEnumerable<ScheduleValidationError> errors) => string.Join("; ", errors.Select(e => $"{e.Field}: {e.Message}"));

        private static void EnsureMutationAllowed(JobCatalogRecord current, JobDefinition proposed, bool hasStarted)
        {
            var errors = MutationErrors(current, proposed, hasStarted, fieldPrefix: string.Empty);
            if (errors.Count > 0)
            {
                throw new InvalidOperationException("Schedule JSON changes read-only started-job fields: " + FormatErrors(errors));
            }
        }

        private static IReadOnlyList<ScheduleValidationError> MutationErrors(JobCatalogRecord current, JobDefinition proposed, bool hasStarted, string fieldPrefix)
        {
            var violations = ScheduleMutationPolicy.ValidateUpdate(current.Definition, proposed, hasStarted);
            return violations
                .Select(v => new ScheduleValidationError(current.JobId, fieldPrefix + v.Field, v.Message))
                .ToArray();
        }

        private static bool HasStarted(SqliteConnection connection, SqliteTransaction? transaction, string jobId)
        {
            using var command = SqliteStorage.Command(connection, transaction, """
                SELECT CASE WHEN
                    EXISTS (SELECT 1 FROM current_slice_state WHERE job_id = $job_id LIMIT 1)
                    OR EXISTS (SELECT 1 FROM slice_state_events WHERE job_id = $job_id LIMIT 1)
                    OR EXISTS (SELECT 1 FROM work_queue WHERE job_id = $job_id LIMIT 1)
                    OR EXISTS (SELECT 1 FROM scheduled_slices WHERE job_id = $job_id LIMIT 1)
                    OR EXISTS (SELECT 1 FROM slice_attempts WHERE job_id = $job_id LIMIT 1)
                THEN 1 ELSE 0 END;
                """);
            command.Add("$job_id", jobId);
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
        }

        private static bool Exists(SqliteConnection connection, SqliteTransaction transaction, string jobId)
        {
            using var command = SqliteStorage.Command(connection, transaction, "SELECT 1 FROM job_definitions WHERE job_id = $job_id LIMIT 1;");
            command.Add("$job_id", jobId);
            return command.ExecuteScalar() is not null;
        }

        private static JobCatalogRecord? Get(SqliteConnection connection, SqliteTransaction? transaction, string jobId)
        {
            using var command = SqliteStorage.Command(connection, transaction, "SELECT * FROM job_definitions WHERE job_id = $job_id LIMIT 1;");
            command.Add("$job_id", jobId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadJob(reader) : null;
        }

        private static JobCatalogRecord ReadJob(SqliteDataReader reader) => new(
            reader.GetString(reader.GetOrdinal("job_id")),
            reader.GetString(reader.GetOrdinal("display_name")),
            reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description")),
            reader.IsDBNull(reader.GetOrdinal("query_ref")) ? null : reader.GetString(reader.GetOrdinal("query_ref")),
            reader.GetString(reader.GetOrdinal("schedule_json")),
            reader.GetString(reader.GetOrdinal("parameters_json")),
            reader.GetInt32(reader.GetOrdinal("is_enabled")) == 1,
            reader.GetInt64(reader.GetOrdinal("catalog_version")),
            SqliteStorage.ReadUtc(reader, "created_at_utc"),
            SqliteStorage.ReadUtc(reader, "updated_at_utc"));

        private static void InsertJob(SqliteConnection connection, SqliteTransaction transaction, JobDefinition definition, string canonicalJson, long catalogVersion, DateTimeOffset now)
        {
            using var insert = SqliteStorage.Command(connection, transaction, """
                INSERT INTO job_definitions (job_id, display_name, description, query_ref, schedule_json, parameters_json, is_enabled, catalog_version, created_at_utc, updated_at_utc)
                VALUES ($job_id, $display_name, NULL, $query_ref, $schedule_json, $parameters_json, $is_enabled, $catalog_version, $now, $now);
                """);
            insert.Add("$job_id", definition.ActivityId);
            insert.Add("$display_name", definition.ActivityId);
            insert.Add("$query_ref", definition.FunctionName);
            insert.Add("$schedule_json", canonicalJson);
            insert.Add("$parameters_json", ParametersJson(definition));
            insert.Add("$is_enabled", definition.IsPaused ? 0 : 1);
            insert.Add("$catalog_version", catalogVersion);
            insert.Add("$now", SqliteStorage.Utc(now));
            insert.ExecuteNonQuery();
        }

        private static void UpdateJob(SqliteConnection connection, SqliteTransaction transaction, string jobId, JobDefinition definition, string canonicalJson, long newVersion, long expectedVersion, DateTimeOffset updatedAt)
        {
            using var update = SqliteStorage.Command(connection, transaction, """
                UPDATE job_definitions
                SET display_name = $display_name, query_ref = $query_ref, schedule_json = $schedule_json, parameters_json = $parameters_json,
                    is_enabled = $is_enabled, catalog_version = $catalog_version, updated_at_utc = $updated_at
                WHERE job_id = $job_id AND catalog_version = $expected_version;
                """);
            update.Add("$display_name", definition.ActivityId);
            update.Add("$query_ref", definition.FunctionName);
            update.Add("$schedule_json", canonicalJson);
            update.Add("$parameters_json", ParametersJson(definition));
            update.Add("$is_enabled", definition.IsPaused ? 0 : 1);
            update.Add("$catalog_version", newVersion);
            update.Add("$updated_at", SqliteStorage.Utc(updatedAt));
            update.Add("$job_id", jobId);
            update.Add("$expected_version", expectedVersion);
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException($"Catalog version conflict for '{jobId}'.");
        }

        private static string ParametersJson(JobDefinition definition) =>
            definition.JobSettings is null ? "{}" : JsonSerializer.Serialize(definition.JobSettings.Value, SqliteStorage.JsonOptions);

        private static void InsertEvent(SqliteConnection connection, SqliteTransaction transaction, string eventId, string jobId, long version, string eventType, string scheduleJson, string? actor)
        {
            using var command = SqliteStorage.Command(connection, transaction, """
                INSERT INTO job_definition_events (event_id, job_id, catalog_version, event_type, payload_json)
                VALUES ($event_id, $job_id, $version, $event_type, $payload_json);
                """);
            command.Add("$event_id", eventId);
            command.Add("$job_id", jobId);
            command.Add("$version", version);
            command.Add("$event_type", eventType);
            command.Add("$payload_json", JsonSerializer.Serialize(new { actor, scheduleJson }, SqliteStorage.JsonOptions));
            command.ExecuteNonQuery();
        }
    }
}
