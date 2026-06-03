using System.Text.Json;
using KoLite.Local.Sqlite.Catalog;

namespace KoLite.LocalApp.Ui
{
    public sealed record CatalogHistoryDisplayRow(
        long CatalogVersion,
        string EventType,
        DateTimeOffset RecordedAtUtc,
        string? Actor,
        IReadOnlyList<JsonDiffRow> DiffRows,
        bool IsInitialDefinition)
    {
        public bool HasScheduleChanges => DiffRows.Count > 0;
    }

    public sealed record JsonDiffRow(string Path, string ChangeType, string? PreviousValue, string? CurrentValue);

    internal static class CatalogHistoryDiffBuilder
    {
        public static IReadOnlyList<CatalogHistoryDisplayRow> Build(IEnumerable<JobDefinitionEventRecord> events)
        {
            var snapshots = events
                .OrderBy(e => e.CatalogVersion)
                .ThenBy(e => e.RecordedAtUtc)
                .Select(ReadSnapshot)
                .ToList();

            var rows = new List<CatalogHistoryDisplayRow>(snapshots.Count);
            CatalogHistorySnapshot? previous = null;
            foreach (var snapshot in snapshots)
            {
                var isInitialDefinition = previous is null;
                var diffRows = previous is null
                    ? []
                    : DiffScheduleJson(previous.ScheduleJson, snapshot.ScheduleJson);
                rows.Add(new CatalogHistoryDisplayRow(
                    snapshot.CatalogVersion,
                    snapshot.EventType,
                    snapshot.RecordedAtUtc,
                    snapshot.Actor,
                    diffRows,
                    isInitialDefinition));
                previous = snapshot;
            }

            return rows
                .OrderByDescending(row => row.CatalogVersion)
                .ThenByDescending(row => row.RecordedAtUtc)
                .ToList();
        }

        private static CatalogHistorySnapshot ReadSnapshot(JobDefinitionEventRecord record)
        {
            using var payload = JsonDocument.Parse(record.PayloadJson);
            var actor = payload.RootElement.TryGetProperty("actor", out var actorElement) && actorElement.ValueKind == JsonValueKind.String
                ? actorElement.GetString()
                : null;
            var scheduleJson = payload.RootElement.TryGetProperty("scheduleJson", out var scheduleJsonElement) && scheduleJsonElement.ValueKind == JsonValueKind.String
                ? scheduleJsonElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(scheduleJson))
            {
                throw new InvalidOperationException($"Catalog history event '{record.EventId}' for job '{record.JobId}' does not contain scheduleJson.");
            }

            return new CatalogHistorySnapshot(record.CatalogVersion, record.EventType, record.RecordedAtUtc, actor, scheduleJson);
        }

        private static IReadOnlyList<JsonDiffRow> DiffScheduleJson(string previousJson, string currentJson)
        {
            using var previousDocument = JsonDocument.Parse(previousJson);
            using var currentDocument = JsonDocument.Parse(currentJson);
            var rows = new List<JsonDiffRow>();
            DiffElement("$", previousDocument.RootElement, currentDocument.RootElement, rows);
            return rows;
        }

        private static void DiffElement(string path, JsonElement previous, JsonElement current, List<JsonDiffRow> rows)
        {
            if (previous.ValueKind != current.ValueKind)
            {
                rows.Add(new JsonDiffRow(path, "Changed", previous.GetRawText(), current.GetRawText()));
                return;
            }

            switch (previous.ValueKind)
            {
                case JsonValueKind.Object:
                    DiffObject(path, previous, current, rows);
                    break;
                case JsonValueKind.Array:
                    DiffArray(path, previous, current, rows);
                    break;
                default:
                    if (!StringComparer.Ordinal.Equals(previous.GetRawText(), current.GetRawText()))
                    {
                        rows.Add(new JsonDiffRow(path, "Changed", previous.GetRawText(), current.GetRawText()));
                    }

                    break;
            }
        }

        private static void DiffObject(string path, JsonElement previous, JsonElement current, List<JsonDiffRow> rows)
        {
            var previousProperties = previous.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            var currentProperties = current.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            var propertyNames = previousProperties.Keys
                .Concat(currentProperties.Keys)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal);

            foreach (var propertyName in propertyNames)
            {
                var propertyPath = AppendPropertyPath(path, propertyName);
                var hasPrevious = previousProperties.TryGetValue(propertyName, out var previousValue);
                var hasCurrent = currentProperties.TryGetValue(propertyName, out var currentValue);
                if (!hasPrevious)
                {
                    rows.Add(new JsonDiffRow(propertyPath, "Added", null, currentValue.GetRawText()));
                    continue;
                }

                if (!hasCurrent)
                {
                    rows.Add(new JsonDiffRow(propertyPath, "Removed", previousValue.GetRawText(), null));
                    continue;
                }

                DiffElement(propertyPath, previousValue, currentValue, rows);
            }
        }

        private static void DiffArray(string path, JsonElement previous, JsonElement current, List<JsonDiffRow> rows)
        {
            var previousItems = previous.EnumerateArray().ToList();
            var currentItems = current.EnumerateArray().ToList();
            var itemCount = Math.Max(previousItems.Count, currentItems.Count);
            for (var index = 0; index < itemCount; index++)
            {
                var itemPath = $"{path}[{index}]";
                if (index >= previousItems.Count)
                {
                    rows.Add(new JsonDiffRow(itemPath, "Added", null, currentItems[index].GetRawText()));
                    continue;
                }

                if (index >= currentItems.Count)
                {
                    rows.Add(new JsonDiffRow(itemPath, "Removed", previousItems[index].GetRawText(), null));
                    continue;
                }

                DiffElement(itemPath, previousItems[index], currentItems[index], rows);
            }
        }

        private static string AppendPropertyPath(string path, string propertyName) => path == "$" ? $"$.{propertyName}" : $"{path}.{propertyName}";

        private sealed record CatalogHistorySnapshot(long CatalogVersion, string EventType, DateTimeOffset RecordedAtUtc, string? Actor, string ScheduleJson);
    }
}
