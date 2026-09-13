using System.Text.Json;
using KoLite.Local.Core.Scheduling;
using KoLite.Local.Sqlite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Performance
{
    public sealed partial class SqlitePerformanceRepository
    {
        private static AttemptFact ResolveLegacyFact(SqliteConnection connection, SqliteTransaction transaction, AttemptFact fact)
        {
            var errors = new List<string?> { fact.CaptureError };
            string? clientRequestId = null;
            if (fact.SliceEndUtc <= fact.SliceStartUtc
                || fact.ChunkId.HasValue != fact.TotalChunks.HasValue
                || (fact.TotalChunks is { } count && (count is < 1 or > 32 || fact.ChunkId < 0 || fact.ChunkId >= count)))
            {
                errors.Add("Historical execution identity is incomplete or invalid.");
            }
            else
            {
                var execution = new SliceExecutionUnit(
                    new SliceRange(fact.JobId, fact.SliceStartUtc, fact.SliceEndUtc), fact.ChunkId, fact.TotalChunks);
                clientRequestId = $"KoLite.Local.Output;output|{execution.ExecutionKey}";
            }

            if (fact.StartedAtUtc is null || fact.StartedAtUtc > fact.CompletedAtUtc)
            {
                errors.Add("The historical attempt has no trustworthy start/completion interval.");
            }

            var history = ReadTargetEvidence(connection, transaction, fact);
            if (history.Error is not null)
            {
                errors.Add(history.Error);
            }

            return fact with
            {
                ClusterUri = history.ClusterUri,
                Database = history.Database,
                CatalogVersion = history.CatalogVersion,
                ClientRequestId = clientRequestId,
                CaptureError = CombineErrors(errors.ToArray())
            };
        }

        private static TargetEvidence ReadTargetEvidence(SqliteConnection connection, SqliteTransaction transaction, AttemptFact fact)
        {
            const int maxEvents = 128;
            using var command = SqliteStorage.Command(connection, transaction, """
                SELECT catalog_version, recorded_at_utc, payload_json
                FROM job_definition_events
                WHERE job_id=$job AND recorded_at_utc < $end
                ORDER BY recorded_at_utc DESC, catalog_version DESC
                LIMIT $take;
                """);
            command.Add("$job", fact.JobId);
            command.Add("$end", SqliteStorage.Utc(fact.CompletedAtUtc!.Value.AddSeconds(1)));
            command.Add("$take", maxEvents + 1);
            var events = new List<TargetEvent>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var recorded = TryUtc(reader.GetString(1));
                    if (recorded is not null && recorded <= fact.CompletedAtUtc)
                    {
                        events.Add(new TargetEvent(reader.GetInt64(0), recorded.Value, reader.GetString(2)));
                    }
                }
            }

            if (events.Count == 0)
            {
                return TargetEvidence.Unavailable("No retained catalog definition establishes the historical target.");
            }

            var atStart = fact.StartedAtUtc is { } start
                ? events.Where(item => item.RecordedAtUtc <= start).Select(item => (DateTimeOffset?)item.RecordedAtUtc).Max()
                : null;
            if (fact.StartedAtUtc is not null && atStart is null)
            {
                return TargetEvidence.Unavailable(events.Count > maxEvents
                    ? "Too many catalog changes overlap this attempt to establish a unique historical target."
                    : "No retained catalog definition establishes the target at the attempt start.");
            }

            if (fact.StartedAtUtc is null && events.Count > maxEvents)
            {
                return TargetEvidence.Unavailable("The missing attempt start prevents bounded historical target resolution.");
            }

            var relevant = atStart.HasValue ? events.Where(item => item.RecordedAtUtc >= atStart).ToArray() : events.ToArray();
            if (relevant.Length > maxEvents)
            {
                return TargetEvidence.Unavailable("Too many catalog changes overlap this attempt to establish a unique historical target.");
            }

            var targets = relevant.Select(ReadTarget).ToArray();
            if (targets.Any(target => target.Error is not null))
            {
                return TargetEvidence.Unavailable("A retained catalog definition has missing or invalid historical target evidence.");
            }

            if (targets.Select(target => (target.ClusterUri, target.Database)).Distinct().Count() != 1)
            {
                return TargetEvidence.Unavailable("Catalog targets changed or conflict during the historical attempt interval; correlation is ambiguous.");
            }

            var version = atStart.HasValue
                ? relevant.Where(item => item.RecordedAtUtc == atStart).Max(item => item.Version)
                : (long?)null;
            return targets[0] with { CatalogVersion = version };
        }

        private static TargetEvidence ReadTarget(TargetEvent history)
        {
            try
            {
                using var payload = JsonDocument.Parse(history.Json);
                if (payload.RootElement.ValueKind != JsonValueKind.Object
                    || !payload.RootElement.TryGetProperty("scheduleJson", out var schedule))
                {
                    return TargetEvidence.Unavailable("Missing canonical schedule JSON.");
                }

                using var definition = schedule.ValueKind == JsonValueKind.String
                    ? JsonDocument.Parse(schedule.GetString()!)
                    : JsonDocument.Parse(schedule.GetRawText());
                if (definition.RootElement.ValueKind != JsonValueKind.Object
                    || !definition.RootElement.TryGetProperty("target", out var target) || target.ValueKind != JsonValueKind.Object)
                {
                    return TargetEvidence.Unavailable("Missing target.");
                }

                var cluster = NormalizeCluster(JsonText(target, "clusterUri"));
                var database = JsonText(target, "database");
                return cluster is not null && !string.IsNullOrWhiteSpace(database)
                    ? new TargetEvidence(cluster, database, history.Version, null)
                    : TargetEvidence.Unavailable("Invalid target.");
            }
            catch (JsonException)
            {
                return TargetEvidence.Unavailable("Invalid canonical schedule JSON.");
            }
        }

        private sealed record TargetEvent(long Version, DateTimeOffset RecordedAtUtc, string Json);
        private sealed record TargetEvidence(string? ClusterUri, string? Database, long? CatalogVersion, string? Error)
        {
            public static TargetEvidence Unavailable(string message) => new(null, null, null, message);
        }
    }
}
