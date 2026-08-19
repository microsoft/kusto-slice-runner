using System.Text.Json;
using System.Text.Json.Nodes;
using KoLite.Local.Core.Schedules;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Catalog
{
    public sealed record JobCatalogRecord(string JobId, string ActivityId, string DisplayName, string? Description, string? QueryRef, string ScheduleJson, string ParametersJson, bool IsEnabled, long CatalogVersion, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc)
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
            var jobId = definition.Id ?? Guid.NewGuid().ToString("N");
            if (Exists(connection, transaction, jobId)) throw new InvalidOperationException($"Job '{jobId}' already exists.");
            EnsureActivityIdAvailable(connection, transaction, definition.ActivityId, excludingJobId: null);
            var deps = ResolveDependencies(connection, transaction, definition, batch: null);
            var storageJson = CatalogScheduleJson.WriteStorageJson(canonical, jobId, deps);
            var now = DateTimeOffset.UtcNow;
            InsertJob(connection, transaction, jobId, definition, storageJson, catalogVersion: 1, now);
            InsertEvent(connection, transaction, eventId ?? Guid.NewGuid().ToString("N"), jobId, 1, "Created", storageJson, actor);
            transaction.Commit();
            return Get(jobId)!;
        }

        public JobCatalogRecord Update(string jobId, string scheduleJson, long expectedVersion, string? actor = null, string? eventId = null)
        {
            var (definition, canonical) = Validate(scheduleJson);
            if (definition.Id is { } suppliedId && !StringComparer.Ordinal.Equals(suppliedId, jobId)) throw new InvalidOperationException($"Updated schedule id '{suppliedId}' must match the target job id '{jobId}'.");
            using var connection = connectionFactory.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var current = Get(connection, transaction, jobId) ?? throw new InvalidOperationException($"Job '{jobId}' does not exist.");
            if (current.CatalogVersion != expectedVersion) throw new CatalogVersionConflictException(jobId, expectedVersion, current.CatalogVersion);
            var proposed = definition with { Id = jobId };
            EnsureMutationAllowed(current, proposed, HasStarted(connection, transaction, jobId));
            if (!StringComparer.Ordinal.Equals(current.ActivityId, proposed.ActivityId)) EnsureActivityIdAvailable(connection, transaction, proposed.ActivityId, excludingJobId: jobId);
            var deps = ResolveDependencies(connection, transaction, proposed, batch: null);
            var storageJson = CatalogScheduleJson.WriteStorageJson(canonical, jobId, deps);
            var newVersion = current.CatalogVersion + 1;
            UpdateJob(connection, transaction, jobId, proposed, storageJson, newVersion, expectedVersion, DateTimeOffset.UtcNow);
            InsertEvent(connection, transaction, eventId ?? Guid.NewGuid().ToString("N"), jobId, newVersion, "Updated", storageJson, actor);
            transaction.Commit();
            return Get(jobId)!;
        }

        public JobCatalogRecord SetEnabled(string jobId, bool enabled, long expectedVersion, string? actor = null, string? eventId = null)
        {
            using var connection = connectionFactory.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var current = Get(connection, transaction, jobId) ?? throw new InvalidOperationException($"Job '{jobId}' does not exist.");
            if (current.CatalogVersion != expectedVersion) throw new CatalogVersionConflictException(jobId, expectedVersion, current.CatalogVersion);
            var schedule = JsonNode.Parse(current.ScheduleJson)?.AsObject()
                ?? throw new InvalidOperationException($"Stored schedule for '{jobId}' is invalid.");
            schedule["isPaused"] = !enabled;
            var updatedScheduleJson = schedule.ToJsonString(SqliteStorage.JsonOptions);
            var newVersion = current.CatalogVersion + 1;
            using (var update = SqliteStorage.Command(connection, transaction, "UPDATE job_definitions SET is_enabled = $enabled, schedule_json = $schedule, catalog_version = $version, updated_at_utc = $updated_at WHERE job_id = $job_id AND catalog_version = $expected;"))
            {
                update.Add("$enabled", enabled ? 1 : 0);
                update.Add("$schedule", updatedScheduleJson);
                update.Add("$version", newVersion);
                update.Add("$updated_at", SqliteStorage.Utc(DateTimeOffset.UtcNow));
                update.Add("$job_id", jobId);
                update.Add("$expected", expectedVersion);
                if (update.ExecuteNonQuery() != 1) throw new CatalogVersionConflictException(jobId, expectedVersion, actualVersion: null);
            }

            InsertEvent(connection, transaction, eventId ?? Guid.NewGuid().ToString("N"), jobId, newVersion, enabled ? "Enabled" : "Disabled", updatedScheduleJson, actor);
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

            // Resolve every item to a target job id (match existing by id, else by activityId, else
            // create — preserving a supplied id so export/import round-trips). Resolving up front
            // gives a stable batch map for dependency resolution and final-state validation.
            var plans = new List<ImportPlan>();
            foreach (var item in validatedItems)
            {
                var existing = item.Definition.Id is { } id
                    ? Get(connection, transaction, id)
                    : GetByActivityId(connection, transaction, item.Definition.ActivityId);
                var targetId = existing?.JobId ?? item.Definition.Id ?? Guid.NewGuid().ToString("N");
                plans.Add(new ImportPlan(item.Index, item.Definition, item.CanonicalJson, existing, targetId));
            }

            var duplicateTarget = plans.GroupBy(p => p.TargetId, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
            if (duplicateTarget is not null)
            {
                throw new InvalidOperationException($"Multiple import items resolve to the same job id '{duplicateTarget.Key}'.");
            }

            EnsureNoFinalActivityIdCollisions(connection, transaction, plans);

            var batch = plans.ToDictionary(p => p.Definition.ActivityId, p => p.TargetId, StringComparer.Ordinal);

            var mutationErrors = new List<ScheduleValidationError>();
            foreach (var plan in plans.Where(p => p.Existing is not null))
            {
                mutationErrors.AddRange(MutationErrors(plan.Existing!, plan.Definition with { Id = plan.TargetId }, HasStarted(connection, transaction, plan.TargetId), $"[{plan.Index}]."));
            }

            if (mutationErrors.Count > 0)
            {
                throw new InvalidOperationException("Schedule JSON changes read-only started-job fields: " + FormatErrors(mutationErrors));
            }

            // Park updated jobs' activity_id at unique temp values so rename swaps within the batch
            // do not trip the immediate (non-deferrable) unique index mid-transaction.
            foreach (var plan in plans.Where(p => p.Existing is not null))
            {
                ParkActivityId(connection, transaction, plan.TargetId);
            }

            var itemResults = new List<JobCatalogImportItemResult>();
            var created = 0;
            var updated = 0;
            foreach (var plan in plans)
            {
                var proposed = plan.Definition with { Id = plan.TargetId };
                var deps = ResolveDependencies(connection, transaction, proposed, batch);
                var storageJson = CatalogScheduleJson.WriteStorageJson(plan.CanonicalJson, plan.TargetId, deps);
                if (plan.Existing is null)
                {
                    InsertJob(connection, transaction, plan.TargetId, proposed, storageJson, catalogVersion: 1, DateTimeOffset.UtcNow);
                    InsertEvent(connection, transaction, Guid.NewGuid().ToString("N"), plan.TargetId, 1, "Created", storageJson, actor);
                    itemResults.Add(new JobCatalogImportItemResult(plan.TargetId, "Created", 1));
                    created++;
                    continue;
                }

                var newVersion = plan.Existing.CatalogVersion + 1;
                UpdateJob(connection, transaction, plan.TargetId, proposed, storageJson, newVersion, plan.Existing.CatalogVersion, DateTimeOffset.UtcNow);
                InsertEvent(connection, transaction, Guid.NewGuid().ToString("N"), plan.TargetId, newVersion, "Updated", storageJson, actor);
                itemResults.Add(new JobCatalogImportItemResult(plan.TargetId, "Updated", newVersion));
                updated++;
            }

            transaction.Commit();
            return new JobCatalogImportResult(created, updated, itemResults);
        }

        private sealed record ImportPlan(int Index, JobDefinition Definition, string CanonicalJson, JobCatalogRecord? Existing, string TargetId);

        public JobCatalogRecord? Get(string jobId)
        {
            using var connection = connectionFactory.OpenConnection();
            return Get(connection, null, jobId);
        }

        public JobCatalogRecord? GetByActivityId(string activityId)
        {
            using var connection = connectionFactory.OpenConnection();
            return GetByActivityId(connection, null, activityId);
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

        // Every OTHER job whose stored definition lists jobId as an upstream dependency. Dependency
        // edges are stored canonically as upstream GUID ids, so the match is by DependentJob.Id.
        // Records whose stored schedule JSON cannot be parsed are skipped defensively rather than
        // failing the whole scan.
        public IReadOnlyList<(string JobId, string ActivityId)> FindDependents(string jobId)
        {
            var dependents = new List<(string JobId, string ActivityId)>();
            foreach (var record in List())
            {
                if (StringComparer.Ordinal.Equals(record.JobId, jobId))
                {
                    continue;
                }

                JobDefinition definition;
                try
                {
                    definition = record.Definition;
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                if (definition.DependsOn.Any(dependency => StringComparer.Ordinal.Equals(dependency.Id, jobId)))
                {
                    dependents.Add((record.JobId, record.ActivityId));
                }
            }

            return dependents;
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

        public string Export(string jobId)
        {
            var record = Get(jobId) ?? throw new InvalidOperationException($"Job '{jobId}' does not exist.");
            return CatalogScheduleJson.WriteExportJson(record.ScheduleJson, ActivityLabels());
        }

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

        private string SerializeAsImportArray(IEnumerable<JobCatalogRecord> records)
        {
            var labels = ActivityLabels();
            var array = new JsonArray();
            foreach (var record in records
                .OrderBy(record => record.ActivityId, StringComparer.Ordinal)
                .ThenBy(record => record.JobId, StringComparer.Ordinal))
            {
                array.Add(CatalogScheduleJson.BuildExportNode(record.ScheduleJson, labels));
            }

            return array.ToJsonString(SqliteStorage.IndentedJsonOptions);
        }

        private IReadOnlyDictionary<string, string> ActivityLabels() =>
            List().ToDictionary(record => record.JobId, record => record.ActivityId, StringComparer.Ordinal);

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

        private static JobCatalogRecord? GetByActivityId(SqliteConnection connection, SqliteTransaction? transaction, string activityId)
        {
            using var command = SqliteStorage.Command(connection, transaction, "SELECT * FROM job_definitions WHERE activity_id = $activity_id LIMIT 1;");
            command.Add("$activity_id", activityId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadJob(reader) : null;
        }

        private static string? LookupIdByActivityId(SqliteConnection connection, SqliteTransaction? transaction, string activityId)
        {
            using var command = SqliteStorage.Command(connection, transaction, "SELECT job_id FROM job_definitions WHERE activity_id = $activity_id LIMIT 1;");
            command.Add("$activity_id", activityId);
            return command.ExecuteScalar() as string;
        }

        private static void EnsureActivityIdAvailable(SqliteConnection connection, SqliteTransaction? transaction, string activityId, string? excludingJobId)
        {
            var existingId = LookupIdByActivityId(connection, transaction, activityId);
            if (existingId is not null && !StringComparer.Ordinal.Equals(existingId, excludingJobId))
            {
                throw new InvalidOperationException($"activityId '{activityId}' is already used by another job.");
            }
        }

        private static IReadOnlyList<StoredDependency> ResolveDependencies(SqliteConnection connection, SqliteTransaction? transaction, JobDefinition definition, IReadOnlyDictionary<string, string>? batch)
        {
            var deps = new List<StoredDependency>();
            foreach (var dependency in definition.DependsOn)
            {
                string? upstreamId;
                if (dependency.Id is { } id)
                {
                    upstreamId = id;
                    if (dependency.ActivityId is { } activityId)
                    {
                        var resolved = ResolveUpstreamActivityId(connection, transaction, batch, activityId);
                        if (resolved is not null && !StringComparer.Ordinal.Equals(resolved, id))
                        {
                            throw new InvalidOperationException($"Dependency id '{id}' and activityId '{activityId}' refer to different jobs.");
                        }
                    }
                }
                else
                {
                    var activityId = dependency.ActivityId!;
                    upstreamId = ResolveUpstreamActivityId(connection, transaction, batch, activityId)
                        ?? throw new InvalidOperationException($"Unknown upstream dependency activityId '{activityId}'. Create it first or reference it by id.");
                }

                deps.Add(new StoredDependency(upstreamId, null));
            }

            return deps;
        }

        private static string? ResolveUpstreamActivityId(SqliteConnection connection, SqliteTransaction? transaction, IReadOnlyDictionary<string, string>? batch, string activityId) =>
            batch is not null && batch.TryGetValue(activityId, out var batched) ? batched : LookupIdByActivityId(connection, transaction, activityId);

        private static void ParkActivityId(SqliteConnection connection, SqliteTransaction transaction, string jobId)
        {
            using var command = SqliteStorage.Command(connection, transaction, "UPDATE job_definitions SET activity_id = $temp WHERE job_id = $job_id;");
            command.Add("$temp", "\u0001import-temp:" + jobId);
            command.Add("$job_id", jobId);
            command.ExecuteNonQuery();
        }

        private static void EnsureNoFinalActivityIdCollisions(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<ImportPlan> plans)
        {
            var finalByJobId = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var command = SqliteStorage.Command(connection, transaction, "SELECT job_id, activity_id FROM job_definitions;"))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    finalByJobId[reader.GetString(0)] = reader.IsDBNull(1) ? reader.GetString(0) : reader.GetString(1);
                }
            }

            foreach (var plan in plans)
            {
                finalByJobId[plan.TargetId] = plan.Definition.ActivityId;
            }

            var duplicate = finalByJobId.GroupBy(kv => kv.Value, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
            {
                throw new InvalidOperationException($"activityId '{duplicate.Key}' would be used by more than one job after import.");
            }
        }

        private static JobCatalogRecord ReadJob(SqliteDataReader reader)
        {
            var jobId = reader.GetString(reader.GetOrdinal("job_id"));
            var activityIdOrdinal = reader.GetOrdinal("activity_id");
            var activityId = reader.IsDBNull(activityIdOrdinal) ? jobId : reader.GetString(activityIdOrdinal);
            return new(
                jobId,
                activityId,
                reader.GetString(reader.GetOrdinal("display_name")),
                reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description")),
                reader.IsDBNull(reader.GetOrdinal("query_ref")) ? null : reader.GetString(reader.GetOrdinal("query_ref")),
                reader.GetString(reader.GetOrdinal("schedule_json")),
                reader.GetString(reader.GetOrdinal("parameters_json")),
                reader.GetInt32(reader.GetOrdinal("is_enabled")) == 1,
                reader.GetInt64(reader.GetOrdinal("catalog_version")),
                SqliteStorage.ReadUtc(reader, "created_at_utc"),
                SqliteStorage.ReadUtc(reader, "updated_at_utc"));
        }

        private static void InsertJob(SqliteConnection connection, SqliteTransaction transaction, string jobId, JobDefinition definition, string storageJson, long catalogVersion, DateTimeOffset now)
        {
            using var insert = SqliteStorage.Command(connection, transaction, """
                INSERT INTO job_definitions (job_id, activity_id, display_name, description, query_ref, schedule_json, parameters_json, is_enabled, catalog_version, created_at_utc, updated_at_utc)
                VALUES ($job_id, $activity_id, $display_name, $description, $query_ref, $schedule_json, $parameters_json, $is_enabled, $catalog_version, $now, $now);
                """);
            insert.Add("$job_id", jobId);
            insert.Add("$activity_id", definition.ActivityId);
            insert.Add("$display_name", definition.ActivityId);
            insert.Add("$description", definition.Description);
            insert.Add("$query_ref", definition.FunctionName);
            insert.Add("$schedule_json", storageJson);
            insert.Add("$parameters_json", ParametersJson(definition));
            insert.Add("$is_enabled", definition.IsPaused ? 0 : 1);
            insert.Add("$catalog_version", catalogVersion);
            insert.Add("$now", SqliteStorage.Utc(now));
            insert.ExecuteNonQuery();
        }

        private static void UpdateJob(SqliteConnection connection, SqliteTransaction transaction, string jobId, JobDefinition definition, string storageJson, long newVersion, long expectedVersion, DateTimeOffset updatedAt)
        {
            using var update = SqliteStorage.Command(connection, transaction, """
                UPDATE job_definitions
                SET activity_id = $activity_id, display_name = $display_name, description = $description, query_ref = $query_ref, schedule_json = $schedule_json, parameters_json = $parameters_json,
                    is_enabled = $is_enabled, catalog_version = $catalog_version, updated_at_utc = $updated_at
                WHERE job_id = $job_id AND catalog_version = $expected_version;
                """);
            update.Add("$activity_id", definition.ActivityId);
            update.Add("$display_name", definition.ActivityId);
            update.Add("$description", definition.Description);
            update.Add("$query_ref", definition.FunctionName);
            update.Add("$schedule_json", storageJson);
            update.Add("$parameters_json", ParametersJson(definition));
            update.Add("$is_enabled", definition.IsPaused ? 0 : 1);
            update.Add("$catalog_version", newVersion);
            update.Add("$updated_at", SqliteStorage.Utc(updatedAt));
            update.Add("$job_id", jobId);
            update.Add("$expected_version", expectedVersion);
            if (update.ExecuteNonQuery() != 1) throw new CatalogVersionConflictException(jobId, expectedVersion, actualVersion: null);
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
