// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;

namespace KoLite.Local.Core.Schedules
{
    public static class ScheduleImportParser
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
        };

        public static ScheduleImportValidationResult Parse(string json)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                return ScheduleImportValidationResult.Failed(new ScheduleValidationError(null, "<json>", $"Schedule import document is not valid JSON: {ex.Message}"));
            }

            using (doc)
            {
                return doc.RootElement.ValueKind switch
                {
                    JsonValueKind.Object => ParseSingle(doc.RootElement),
                    JsonValueKind.Array => ParseArray(doc.RootElement),
                    _ => ScheduleImportValidationResult.Failed(new ScheduleValidationError(null, "<root>", "Schedule import document must be a JSON object or an array of JSON objects."))
                };
            }
        }

        private static ScheduleImportValidationResult ParseSingle(JsonElement root)
        {
            var scheduleJson = Serialize(root);
            var parsed = ScheduleParser.Parse(scheduleJson);
            return parsed.IsValid && parsed.Definition is not null
                ? ScheduleImportValidationResult.Success([new ScheduleImportItem(0, scheduleJson, parsed.Definition)])
                : ScheduleImportValidationResult.Failed(parsed.Errors);
        }

        private static ScheduleImportValidationResult ParseArray(JsonElement root)
        {
            var items = new List<ScheduleImportItem>();
            var errors = new List<ScheduleValidationError>();
            var seenActivityIds = new Dictionary<string, int>(StringComparer.Ordinal);
            var seenIds = new Dictionary<string, int>(StringComparer.Ordinal);
            var index = 0;

            foreach (var element in root.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    errors.Add(new ScheduleValidationError(null, $"[{index}]", $"Import item [{index}] must be a JSON object."));
                    index++;
                    continue;
                }

                var scheduleJson = Serialize(element);
                var parsed = ScheduleParser.Parse(scheduleJson);
                if (!parsed.IsValid || parsed.Definition is null)
                {
                    errors.AddRange(parsed.Errors.Select(error => Prefix(index, error)));
                    index++;
                    continue;
                }

                if (parsed.Definition.Id is { } id && seenIds.TryGetValue(id, out var firstIdIndex))
                {
                    errors.Add(new ScheduleValidationError(
                        parsed.Definition.ActivityId,
                        $"[{index}].id",
                        $"Duplicate id '{id}' in import payload; first seen at item [{firstIdIndex}]."));
                    index++;
                    continue;
                }

                if (seenActivityIds.TryGetValue(parsed.Definition.ActivityId, out var firstIndex))
                {
                    errors.Add(new ScheduleValidationError(
                        parsed.Definition.ActivityId,
                        $"[{index}].activityId",
                        $"Duplicate activityId '{parsed.Definition.ActivityId}' in import payload; first seen at item [{firstIndex}]."));
                    index++;
                    continue;
                }

                if (parsed.Definition.Id is { } definedId)
                {
                    seenIds.Add(definedId, index);
                }

                seenActivityIds.Add(parsed.Definition.ActivityId, index);
                items.Add(new ScheduleImportItem(index, scheduleJson, parsed.Definition));
                index++;
            }

            if (index == 0)
            {
                errors.Add(new ScheduleValidationError(null, "<root>", "Schedule import array must contain at least one schedule object."));
            }

            return errors.Count == 0
                ? ScheduleImportValidationResult.Success(items)
                : ScheduleImportValidationResult.Failed(errors);
        }

        private static ScheduleValidationError Prefix(int index, ScheduleValidationError error) =>
            error with { Field = $"[{index}].{error.Field}" };

        private static string Serialize(JsonElement element) => JsonSerializer.Serialize(element, JsonOptions);
    }

    public sealed record ScheduleImportItem(int Index, string ScheduleJson, JobDefinition Definition);

    public sealed class ScheduleImportValidationResult
    {
        private ScheduleImportValidationResult(IReadOnlyList<ScheduleImportItem> items, IReadOnlyList<ScheduleValidationError> errors)
        {
            Items = items;
            Errors = errors;
        }

        public IReadOnlyList<ScheduleImportItem> Items { get; }
        public IReadOnlyList<ScheduleValidationError> Errors { get; }
        public bool IsValid => Items.Count > 0 && Errors.Count == 0;

        public static ScheduleImportValidationResult Success(IReadOnlyList<ScheduleImportItem> items) => new(items, Array.Empty<ScheduleValidationError>());
        public static ScheduleImportValidationResult Failed(params ScheduleValidationError[] errors) => new(Array.Empty<ScheduleImportItem>(), errors);
        public static ScheduleImportValidationResult Failed(IEnumerable<ScheduleValidationError> errors) => new(Array.Empty<ScheduleImportItem>(), errors.ToArray());
    }
}
