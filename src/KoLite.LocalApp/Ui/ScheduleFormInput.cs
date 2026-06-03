using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using KoLite.Local.Core.Schedules;

namespace KoLite.LocalApp.Ui
{
    public sealed class ScheduleFormInput
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

        public string ActivityId { get; set; } = string.Empty;
        public string FunctionName { get; set; } = string.Empty;
        public string OutputTable { get; set; } = string.Empty;
        public string QueryWindowSize { get; set; } = "01:00:00";
        public string DelayFromUtcNow { get; set; } = "00:10:00";
        public int MaxParallelism { get; set; } = 1;
        public string QueryTimeout { get; set; } = "00:05:00";
        public bool IsPaused { get; set; }
        public string StartFrom { get; set; } = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        public string? EndOn { get; set; }
        public string? Folder { get; set; }
        public string? Tags { get; set; }
        public string ClusterUri { get; set; } = "https://example.kusto.windows.net";
        public string Database { get; set; } = "Samples";
        public string? DependsOn { get; set; }
        public string JobSettingsJson { get; set; } = "{}";

        public static ScheduleFormInput Default() => FromJson(SampleScheduleFactory.CreateJson());

        public static ScheduleFormInput FromJson(string json)
        {
            var result = ScheduleParser.Parse(json);
            if (!result.IsValid || result.Definition is null)
            {
                return DefaultWithoutParsing();
            }

            return FromDefinition(result.Definition);
        }

        public static ScheduleFormInput FromDefinition(JobDefinition definition)
        {
            return new ScheduleFormInput
            {
                ActivityId = definition.ActivityId,
                FunctionName = definition.FunctionName,
                OutputTable = definition.OutputTable,
                QueryWindowSize = definition.QueryWindowSize.ToString("c", CultureInfo.InvariantCulture),
                DelayFromUtcNow = definition.DelayFromUtcNow.ToString("c", CultureInfo.InvariantCulture),
                MaxParallelism = definition.MaxParallelism,
                QueryTimeout = definition.QueryTimeout.ToString("c", CultureInfo.InvariantCulture),
                IsPaused = definition.IsPaused,
                StartFrom = AppFormatting.Iso(definition.StartFrom),
                EndOn = definition.EndOn is null ? null : AppFormatting.Iso(definition.EndOn.Value),
                Folder = definition.Folder,
                Tags = string.Join(Environment.NewLine, definition.Tags),
                ClusterUri = definition.Target.ClusterUri,
                Database = definition.Target.Database,
                DependsOn = string.Join(Environment.NewLine, definition.DependsOn.Select(d => d.ActivityId)),
                JobSettingsJson = definition.JobSettings is null ? "{}" : JsonSerializer.Serialize(definition.JobSettings.Value, JsonOptions)
            };
        }

        public string ToScheduleJson()
        {
            var root = new JsonObject
            {
                ["activityId"] = ActivityId?.Trim() ?? string.Empty,
                ["functionName"] = FunctionName?.Trim() ?? string.Empty,
                ["outputTable"] = OutputTable?.Trim() ?? string.Empty,
                ["queryWindowSize"] = QueryWindowSize?.Trim() ?? string.Empty,
                ["delayFromUtcNow"] = DelayFromUtcNow?.Trim() ?? string.Empty,
                ["maxParallelism"] = MaxParallelism,
                ["queryTimeout"] = QueryTimeout?.Trim() ?? string.Empty,
                ["isPaused"] = IsPaused,
                ["startFrom"] = StartFrom?.Trim() ?? string.Empty,
                ["target"] = new JsonObject
                {
                    ["clusterUri"] = ClusterUri?.Trim() ?? string.Empty,
                    ["database"] = Database?.Trim() ?? string.Empty
                }
            };

            if (!string.IsNullOrWhiteSpace(EndOn))
            {
                root["endOn"] = EndOn.Trim();
            }

            if (!string.IsNullOrWhiteSpace(Folder))
            {
                root["folder"] = Folder.Trim();
            }

            var tags = SplitTags().ToArray();
            if (tags.Length > 0)
            {
                var array = new JsonArray();
                foreach (var tag in tags)
                {
                    array.Add(tag);
                }

                root["tags"] = array;
            }

            var dependencies = SplitDependencyIds().Select(id => new JsonObject { ["activityId"] = id }).ToArray();
            if (dependencies.Length > 0)
            {
                var array = new JsonArray();
                foreach (var dependency in dependencies)
                {
                    array.Add(dependency);
                }

                root["dependsOn"] = array;
            }

            root["jobSettings"] = JsonNode.Parse(string.IsNullOrWhiteSpace(JobSettingsJson) ? "{}" : JobSettingsJson) ?? new JsonObject();
            return root.ToJsonString(JsonOptions);
        }

        private IEnumerable<string> SplitDependencyIds() =>
            (DependsOn ?? string.Empty)
                .Split(['\r', '\n', ',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal);

        private IEnumerable<string> SplitTags() =>
            ScheduleTags.NormalizeDistinct(
                (Tags ?? string.Empty)
                    .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries));

        private static ScheduleFormInput DefaultWithoutParsing() => new()
        {
            ActivityId = "sample.hourly.aggregate",
            FunctionName = "SampleFunction",
            OutputTable = "SampleOutput",
            QueryWindowSize = "01:00:00",
            DelayFromUtcNow = "00:10:00",
            MaxParallelism = 2,
            QueryTimeout = "00:05:00",
            IsPaused = false,
            StartFrom = DateTimeOffset.UtcNow.AddHours(-6).ToString("yyyy-MM-ddTHH:00:00Z", CultureInfo.InvariantCulture),
            ClusterUri = "https://example.kusto.windows.net",
            Database = "Samples",
            JobSettingsJson = "{}"
        };
    }
}
