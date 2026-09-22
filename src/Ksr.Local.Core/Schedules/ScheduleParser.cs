// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Ksr.Local.Core.Schedules
{
    public static partial class ScheduleParser
    {
        private static readonly HashSet<string> AllowedTopLevel = new(StringComparer.Ordinal)
        {
            "id", "activityId", "description", "functionName", "outputTable", "queryWindowSize", "delayFromUtcNow",
            "maxParallelism", "queryTimeout", "chunks", "isPaused", "startFrom", "endOn", "folder",
            "tags", "dependsOn", "jobSettings", "target", "healthPolicy"
        };

        private static readonly HashSet<string> AllowedTargetFields = new(StringComparer.Ordinal) { "clusterUri", "database" };
        private static readonly HashSet<string> AllowedDependencyFields = new(StringComparer.Ordinal) { "id", "activityId" };

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

                var tags = ParseTags(doc.RootElement, activityId, errors);
                var id = ParseId(doc.RootElement, activityId, errors);
                var healthPolicy = ParseHealthPolicy(doc.RootElement, activityId, errors);
                var dependencies = ParseDependencies(doc.RootElement, dto, id, activityId, errors);
                var startFrom = ParseUtcIso8601(dto.StartFrom, "startFrom", activityId, errors);
                var endOn = ParseUtcIso8601(dto.EndOn, "endOn", activityId, errors, required: false);
                ValidateDto(dto, activityId, errors);

                if (endOn is { } e && startFrom is { } s && e <= s)
                {
                    errors.Add(new ScheduleValidationError(activityId, "endOn", "endOn must be strictly greater than startFrom."));
                }

                return errors.Count == 0
                    ? ScheduleValidationResult.Success(Map(dto, id, tags, dependencies, startFrom!.Value, endOn, healthPolicy))
                    : ScheduleValidationResult.Failed(errors);
            }
        }

        private static void CollectUnknownFields(JsonElement root, string? activityId, List<ScheduleValidationError> errors)
        {
            foreach (var prop in root.EnumerateObject())
            {
                if (!AllowedTopLevel.Contains(prop.Name))
                {
                    errors.Add(new ScheduleValidationError(activityId, prop.Name, $"Field '{prop.Name}' is not part of the supported Kusto Slice Runner schedule contract."));
                }
            }

            if (root.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in target.EnumerateObject())
                {
                    if (!AllowedTargetFields.Contains(prop.Name))
                    {
                        errors.Add(new ScheduleValidationError(activityId, $"target.{prop.Name}", $"Field 'target.{prop.Name}' is not part of the supported Kusto Slice Runner schedule target contract."));
                    }
                }
            }
        }

        private static IReadOnlyList<string> ParseTags(JsonElement root, string? activityId, List<ScheduleValidationError> errors)
        {
            var result = new List<string>();
            if (!root.TryGetProperty("tags", out var tags))
            {
                return result;
            }

            if (tags.ValueKind != JsonValueKind.Array)
            {
                errors.Add(new ScheduleValidationError(activityId, "tags", "tags must be an array of strings."));
                return result;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var entry in tags.EnumerateArray())
            {
                var path = $"tags[{index}]";
                if (entry.ValueKind != JsonValueKind.String)
                {
                    errors.Add(new ScheduleValidationError(activityId, path, $"{path} must be a string."));
                    index++;
                    continue;
                }

                if (!ScheduleTags.TryNormalize(entry.GetString(), out var normalized))
                {
                    errors.Add(new ScheduleValidationError(activityId, path, $"{path} must be a non-empty string."));
                }
                else if (seen.Add(normalized))
                {
                    result.Add(normalized);
                }

                index++;
            }

            return result;
        }

        private static string? ParseId(JsonElement root, string? activityId, List<ScheduleValidationError> errors)
        {
            if (!root.TryGetProperty("id", out var id) || id.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
            {
                errors.Add(new ScheduleValidationError(activityId, "id", "id must be a non-empty GUID string when present."));
                return null;
            }

            if (!TryNormalizeGuid(id.GetString(), out var normalized))
            {
                errors.Add(new ScheduleValidationError(activityId, "id", $"id must be a valid GUID; got '{id.GetString()}'."));
                return null;
            }

            return normalized;
        }

        private static JobHealthPolicy ParseHealthPolicy(JsonElement root, string? activityId, List<ScheduleValidationError> errors)
        {
            if (!root.TryGetProperty("healthPolicy", out var policy) || policy.ValueKind == JsonValueKind.Null)
            {
                return JobHealthPolicy.Complete;
            }

            if (policy.ValueKind != JsonValueKind.String)
            {
                errors.Add(new ScheduleValidationError(activityId, "healthPolicy", "healthPolicy must be a string ('complete' or 'recent')."));
                return JobHealthPolicy.Complete;
            }

            return policy.GetString() switch
            {
                var v when string.Equals(v, "complete", StringComparison.OrdinalIgnoreCase) => JobHealthPolicy.Complete,
                var v when string.Equals(v, "recent", StringComparison.OrdinalIgnoreCase) => JobHealthPolicy.Recent,
                var v => Reject(v)
            };

            JobHealthPolicy Reject(string? value)
            {
                errors.Add(new ScheduleValidationError(activityId, "healthPolicy", $"healthPolicy must be 'complete' or 'recent'; got '{value}'."));
                return JobHealthPolicy.Complete;
            }
        }

        private static bool TryNormalizeGuid(string? raw, out string normalized)
        {
            if (!string.IsNullOrWhiteSpace(raw) && Guid.TryParse(raw, out var guid))
            {
                normalized = guid.ToString("N");
                return true;
            }

            normalized = string.Empty;
            return false;
        }

        private static List<DependentJob> ParseDependencies(JsonElement root, ScheduleJsonDto dto, string? selfId, string? activityId, List<ScheduleValidationError> errors)
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
                        errors.Add(new ScheduleValidationError(activityId, $"{path}.{prop.Name}", $"Field '{path}.{prop.Name}' is not part of the supported Kusto Slice Runner dependency contract."));
                    }
                }

                var depRawId = entry.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                var depActivityId = entry.TryGetProperty("activityId", out var actEl) && actEl.ValueKind == JsonValueKind.String ? actEl.GetString() : null;
                var hasId = !string.IsNullOrWhiteSpace(depRawId);
                var hasActivityId = !string.IsNullOrWhiteSpace(depActivityId);

                if (!hasId && !hasActivityId)
                {
                    errors.Add(new ScheduleValidationError(activityId, path, $"{path} must specify an upstream 'id' or 'activityId'."));
                    index++;
                    continue;
                }

                string? depId = null;
                if (hasId)
                {
                    if (!TryNormalizeGuid(depRawId, out var normalizedDepId))
                    {
                        errors.Add(new ScheduleValidationError(activityId, $"{path}.id", $"{path}.id must be a valid GUID; got '{depRawId}'."));
                        index++;
                        continue;
                    }

                    depId = normalizedDepId;
                }

                if (depId is not null && selfId is not null && string.Equals(depId, selfId, StringComparison.Ordinal))
                {
                    errors.Add(new ScheduleValidationError(activityId, path, $"{path} declares self-dependency on id '{selfId}'."));
                    index++;
                    continue;
                }

                if (hasActivityId && string.Equals(depActivityId, dto.ActivityId, StringComparison.Ordinal))
                {
                    errors.Add(new ScheduleValidationError(activityId, path, $"{path} declares self-dependency on '{dto.ActivityId}'."));
                    index++;
                    continue;
                }

                result.Add(new DependentJob { Id = depId, ActivityId = hasActivityId ? depActivityId : null });
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
            if (dto.Chunks is { } chunks && (chunks < JobChunks.MinCount || chunks > JobChunks.MaxCount))
            {
                errors.Add(new ScheduleValidationError(activityId, "chunks", $"chunks must be between {JobChunks.MinCount} and {JobChunks.MaxCount}."));
            }
            if (dto.Description is { Length: > JobDescription.MaxLength })
            {
                errors.Add(new ScheduleValidationError(activityId, "description", $"description must not exceed {JobDescription.MaxLength.ToString(CultureInfo.InvariantCulture)} characters."));
            }

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

        private static JobDefinition Map(ScheduleJsonDto dto, string? id, IReadOnlyList<string> tags, IReadOnlyList<DependentJob> dependencies, DateTimeOffset startFrom, DateTimeOffset? endOn, JobHealthPolicy healthPolicy) => new()
        {
            Id = id,
            ActivityId = dto.ActivityId!,
            Description = dto.Description,
            FunctionName = dto.FunctionName!,
            OutputTable = dto.OutputTable!,
            QueryWindowSize = dto.QueryWindowSize!.Value,
            DelayFromUtcNow = dto.DelayFromUtcNow!.Value,
            MaxParallelism = dto.MaxParallelism!.Value,
            QueryTimeout = dto.QueryTimeout!.Value,
            Chunks = dto.Chunks,
            StartFrom = startFrom,
            EndOn = endOn,
            IsPaused = dto.IsPaused ?? false,
            Folder = dto.Folder,
            Tags = tags,
            DependsOn = dependencies,
            JobSettings = dto.JobSettings,
            HealthPolicy = healthPolicy,
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
            public string? Description { get; set; }
            public string? FunctionName { get; set; }
            public string? OutputTable { get; set; }
            public TimeSpan? QueryWindowSize { get; set; }
            public TimeSpan? DelayFromUtcNow { get; set; }
            public int? MaxParallelism { get; set; }
            public TimeSpan? QueryTimeout { get; set; }
            public int? Chunks { get; set; }
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
