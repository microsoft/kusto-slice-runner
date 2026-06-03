using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace KoLite.Local.Core.Schedules
{
    public static partial class ScheduleParser
    {
        private static readonly HashSet<string> AllowedTopLevel = new(StringComparer.Ordinal)
        {
            "activityId", "functionName", "outputTable", "queryWindowSize", "delayFromUtcNow",
            "maxParallelism", "queryTimeout", "isPaused", "startFrom", "endOn", "folder",
            "dependsOn", "jobSettings", "target"
        };

        private static readonly HashSet<string> AllowedTargetFields = new(StringComparer.Ordinal) { "clusterUri", "database" };
        private static readonly HashSet<string> AllowedDependencyFields = new(StringComparer.Ordinal) { "activityId" };

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict,
        };

        private static readonly string[] UtcFormats =
        [
            "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.f", "yyyy-MM-ddTHH:mm:ss.ff",
            "yyyy-MM-ddTHH:mm:ss.fff", "yyyy-MM-ddTHH:mm:ss.ffff", "yyyy-MM-ddTHH:mm:ss.fffff",
            "yyyy-MM-ddTHH:mm:ss.ffffff", "yyyy-MM-ddTHH:mm:ss.fffffff"
        ];

        public static ScheduleValidationResult Parse(string json)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                return ScheduleValidationResult.Failed(new ScheduleValidationError(null, "<json>", $"Schedule document is not valid JSON: {ex.Message}"));
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return ScheduleValidationResult.Failed(new ScheduleValidationError(null, "<root>", "Schedule document must be a JSON object."));
                }

                var activityId = TryExtractActivityId(doc.RootElement);
                var errors = new List<ScheduleValidationError>();
                CollectUnknownFields(doc.RootElement, activityId, errors);

                ScheduleJsonDto? dto = null;
                try
                {
                    dto = doc.RootElement.Deserialize<ScheduleJsonDto>(JsonOptions);
                }
                catch (JsonException ex)
                {
                    var field = ExtractPath(ex) ?? "<json>";
                    errors.Add(new ScheduleValidationError(activityId, field, $"Could not deserialize field '{field}': {ex.Message}"));
                    return ScheduleValidationResult.Failed(errors);
                }

                if (dto is null)
                {
                    errors.Add(new ScheduleValidationError(activityId, "<root>", "Schedule document deserialized to null."));
                    return ScheduleValidationResult.Failed(errors);
                }

                var dependencies = ParseDependencies(doc.RootElement, dto, activityId, errors);
                var startFrom = ParseUtcIso8601(dto.StartFrom, "startFrom", activityId, errors);
                var endOn = ParseUtcIso8601(dto.EndOn, "endOn", activityId, errors, required: false);
                ValidateDto(dto, activityId, errors);

                if (endOn is { } e && startFrom is { } s && e <= s)
                {
                    errors.Add(new ScheduleValidationError(activityId, "endOn", "endOn must be strictly greater than startFrom."));
                }

                return errors.Count == 0
                    ? ScheduleValidationResult.Success(Map(dto, dependencies, startFrom!.Value, endOn))
                    : ScheduleValidationResult.Failed(errors);
            }
        }

        private static void CollectUnknownFields(JsonElement root, string? activityId, List<ScheduleValidationError> errors)
        {
            foreach (var prop in root.EnumerateObject())
            {
                if (!AllowedTopLevel.Contains(prop.Name))
                {
                    errors.Add(new ScheduleValidationError(activityId, prop.Name, $"Field '{prop.Name}' is not part of the supported KO Lite schedule contract."));
                }
            }

            if (root.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in target.EnumerateObject())
                {
                    if (!AllowedTargetFields.Contains(prop.Name))
                    {
                        errors.Add(new ScheduleValidationError(activityId, $"target.{prop.Name}", $"Field 'target.{prop.Name}' is not part of the supported KO Lite schedule target contract."));
                    }
                }
            }
        }

        private static List<DependentJob> ParseDependencies(JsonElement root, ScheduleJsonDto dto, string? activityId, List<ScheduleValidationError> errors)
        {
            var result = new List<DependentJob>();
            if (!root.TryGetProperty("dependsOn", out var deps) || deps.ValueKind == JsonValueKind.Null)
            {
                return result;
            }

            if (deps.ValueKind != JsonValueKind.Array)
            {
                errors.Add(new ScheduleValidationError(activityId, "dependsOn", "dependsOn must be an array of dependency objects."));
                return result;
            }

            var index = 0;
            foreach (var entry in deps.EnumerateArray())
            {
                var path = $"dependsOn[{index}]";
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    errors.Add(new ScheduleValidationError(activityId, path, $"{path} must be a JSON object."));
                    index++;
                    continue;
                }

                foreach (var prop in entry.EnumerateObject())
                {
                    if (!AllowedDependencyFields.Contains(prop.Name))
                    {
                        errors.Add(new ScheduleValidationError(activityId, $"{path}.{prop.Name}", $"Field '{path}.{prop.Name}' is not part of the supported KO Lite dependency contract."));
                    }
                }

                var depActivityId = entry.TryGetProperty("activityId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
                if (string.IsNullOrWhiteSpace(depActivityId))
                {
                    errors.Add(new ScheduleValidationError(activityId, $"{path}.activityId", $"{path}.activityId is required and must be a non-empty string."));
                }
                else if (string.Equals(depActivityId, dto.ActivityId, StringComparison.Ordinal))
                {
                    errors.Add(new ScheduleValidationError(activityId, path, $"{path} declares self-dependency on '{dto.ActivityId}'."));
                }
                else
                {
                    result.Add(new DependentJob { ActivityId = depActivityId });
                }

                index++;
            }

            return result;
        }

        private static DateTimeOffset? ParseUtcIso8601(string? raw, string fieldName, string? activityId, List<ScheduleValidationError> errors, bool required = true)
        {
            if (raw is null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                errors.Add(new ScheduleValidationError(activityId, fieldName, $"{fieldName} must be a non-empty ISO-8601 string."));
                return null;
            }

            var match = UtcTimestampRegex().Match(raw);
            if (!match.Success)
            {
                errors.Add(new ScheduleValidationError(activityId, fieldName, $"{fieldName} must be ISO-8601 of the form yyyy-MM-ddTHH:mm:ss[.fffffff][Z|+00:00]; got '{raw}'."));
                return null;
            }

            var offset = match.Groups["off"].Success ? match.Groups["off"].Value : null;
            if (offset is not null && offset != "Z" && offset != "+00:00" && offset != "-00:00")
            {
                errors.Add(new ScheduleValidationError(activityId, fieldName, $"{fieldName} must be UTC (no offset, 'Z', or +00:00); got '{raw}'."));
                return null;
            }

            if (!DateTime.TryParseExact(match.Groups["dt"].Value, UtcFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                errors.Add(new ScheduleValidationError(activityId, fieldName, $"{fieldName} is not a valid date/time value: '{raw}'."));
                return null;
            }

            _ = required;
            return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero);
        }

        private static void ValidateDto(ScheduleJsonDto dto, string? activityId, List<ScheduleValidationError> errors)
        {
            static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
            if (Blank(dto.ActivityId)) errors.Add(new ScheduleValidationError(activityId, "activityId", "activityId is required and must be a non-empty string."));
            if (Blank(dto.FunctionName)) errors.Add(new ScheduleValidationError(activityId, "functionName", "functionName is required and must be a non-empty string."));
            if (Blank(dto.OutputTable)) errors.Add(new ScheduleValidationError(activityId, "outputTable", "outputTable is required and must be a non-empty string."));
            if (dto.QueryWindowSize is null) errors.Add(new ScheduleValidationError(activityId, "queryWindowSize", "queryWindowSize is required."));
            if (dto.DelayFromUtcNow is null) errors.Add(new ScheduleValidationError(activityId, "delayFromUtcNow", "delayFromUtcNow is required."));
            if (dto.MaxParallelism is null) errors.Add(new ScheduleValidationError(activityId, "maxParallelism", "maxParallelism is required."));
            if (dto.QueryTimeout is null) errors.Add(new ScheduleValidationError(activityId, "queryTimeout", "queryTimeout is required."));
            if (dto.StartFrom is null) errors.Add(new ScheduleValidationError(activityId, "startFrom", "startFrom is required."));
            if (dto.Target is null) errors.Add(new ScheduleValidationError(activityId, "target", "target is required."));
            if (dto.QueryWindowSize is { } qws && qws <= TimeSpan.Zero) errors.Add(new ScheduleValidationError(activityId, "queryWindowSize", "queryWindowSize must be strictly greater than zero."));
            if (dto.DelayFromUtcNow is { } d && d < TimeSpan.Zero) errors.Add(new ScheduleValidationError(activityId, "delayFromUtcNow", "delayFromUtcNow must be greater than or equal to zero."));
            if (dto.MaxParallelism is { } mp && mp < 1) errors.Add(new ScheduleValidationError(activityId, "maxParallelism", "maxParallelism must be at least 1."));
            if (dto.QueryTimeout is { } qt && qt <= TimeSpan.Zero) errors.Add(new ScheduleValidationError(activityId, "queryTimeout", "queryTimeout must be strictly greater than zero."));
            if (dto.Target is not { } target) return;
            if (Blank(target.ClusterUri))
            {
                errors.Add(new ScheduleValidationError(activityId, "target.clusterUri", "target.clusterUri is required and must be a non-empty string."));
            }
            else if (!Uri.TryCreate(target.ClusterUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                errors.Add(new ScheduleValidationError(activityId, "target.clusterUri", "target.clusterUri must be an absolute https URI."));
            }

            if (Blank(target.Database)) errors.Add(new ScheduleValidationError(activityId, "target.database", "target.database is required and must be a non-empty string."));
        }

        private static JobDefinition Map(ScheduleJsonDto dto, IReadOnlyList<DependentJob> dependencies, DateTimeOffset startFrom, DateTimeOffset? endOn) => new()
        {
            ActivityId = dto.ActivityId!,
            FunctionName = dto.FunctionName!,
            OutputTable = dto.OutputTable!,
            QueryWindowSize = dto.QueryWindowSize!.Value,
            DelayFromUtcNow = dto.DelayFromUtcNow!.Value,
            MaxParallelism = dto.MaxParallelism!.Value,
            QueryTimeout = dto.QueryTimeout!.Value,
            StartFrom = startFrom,
            EndOn = endOn,
            IsPaused = dto.IsPaused ?? false,
            Folder = dto.Folder,
            DependsOn = dependencies,
            JobSettings = dto.JobSettings,
            Target = new JobTarget { ClusterUri = dto.Target!.ClusterUri!, Database = dto.Target.Database! }
        };

        private static string? TryExtractActivityId(JsonElement root) =>
            root.TryGetProperty("activityId", out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()
                : null;

        private static string? ExtractPath(JsonException ex) => string.IsNullOrEmpty(ex.Path) ? null : ex.Path!.TrimStart('$', '.');

        [GeneratedRegex(@"^(?<dt>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)(?<off>Z|[+-]\d{2}:\d{2})?$", RegexOptions.CultureInvariant)]
        private static partial Regex UtcTimestampRegex();

        private sealed class ScheduleJsonDto
        {
            public string? ActivityId { get; set; }
            public string? FunctionName { get; set; }
            public string? OutputTable { get; set; }
            public TimeSpan? QueryWindowSize { get; set; }
            public TimeSpan? DelayFromUtcNow { get; set; }
            public int? MaxParallelism { get; set; }
            public TimeSpan? QueryTimeout { get; set; }
            public bool? IsPaused { get; set; }
            public string? StartFrom { get; set; }
            public string? EndOn { get; set; }
            public string? Folder { get; set; }
            public JsonElement? JobSettings { get; set; }
            public ScheduleTargetJsonDto? Target { get; set; }
        }

        private sealed class ScheduleTargetJsonDto
        {
            public string? ClusterUri { get; set; }
            public string? Database { get; set; }
        }
    }
}
