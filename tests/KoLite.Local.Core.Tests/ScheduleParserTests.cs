// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.Local.Core.Schedules;

namespace KoLite.Local.Core.Tests
{
    public sealed class ScheduleParserTests
    {
        [Fact]
        public void Parser_rejects_unknown_fields_and_invalid_target_cluster_uri()
        {
            var json = """
            {
              "activityId": "demo.bad",
              "functionName": "Demo",
              "outputTable": "Output",
              "queryWindowSize": "00:05:00",
              "delayFromUtcNow": "00:00:00",
              "maxParallelism": 1,
              "queryTimeout": "00:01:00",
              "isPaused": false,
              "startFrom": "2024-06-01T00:00:00Z",
              "unsupported": true,
              "target": {
                "clusterUri": "http://kolite-example.invalid",
                "database": "DemoDb",
                "extra": "nope"
              }
            }
            """;

            var result = ScheduleParser.Parse(json);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Field == "unsupported");
            Assert.Contains(result.Errors, e => e.Field == "target.extra");
            Assert.Contains(result.Errors, e => e.Field == "target.clusterUri");
        }

        [Theory]
        [MemberData(nameof(ValidSamples))]
        public void Parser_accepts_supported_schedule_samples(string json, string activityId, int dependencyCount)
        {
            var result = ScheduleParser.Parse(json);

            Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors.Select(e => $"{e.Field}: {e.Message}")));
            Assert.NotNull(result.Definition);
            Assert.Equal(activityId, result.Definition.ActivityId);
            Assert.Equal("https://kolite-example.invalid", result.Definition.Target.ClusterUri);
            Assert.Equal(dependencyCount, result.Definition.DependsOn.Count);
        }

        [Fact]
        public void Parser_rejects_non_utc_startFrom()
        {
            var result = ScheduleParser.Parse(MinimalSample.Replace("2024-06-01T00:00:00+00:00", "2024-06-01T00:00:00-07:00", StringComparison.Ordinal));

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Field == "startFrom");
        }

        [Fact]
        public void Import_parser_accepts_single_object_and_array_payloads()
        {
            var single = ScheduleImportParser.Parse(MinimalSample);
            var array = ScheduleImportParser.Parse("[" + MinimalSample + "," + BoundedSample + "]");

            Assert.True(single.IsValid, string.Join(Environment.NewLine, single.Errors.Select(e => $"{e.Field}: {e.Message}")));
            Assert.Single(single.Items);
            Assert.Equal("demo.minimal", single.Items[0].Definition.ActivityId);

            Assert.True(array.IsValid, string.Join(Environment.NewLine, array.Errors.Select(e => $"{e.Field}: {e.Message}")));
            Assert.Equal(["demo.minimal", "demo.bounded"], array.Items.Select(i => i.Definition.ActivityId).ToArray());
            Assert.Equal([0, 1], array.Items.Select(i => i.Index).ToArray());
        }

        [Fact]
        public void Import_parser_rejects_empty_arrays_non_object_items_and_duplicate_activity_ids()
        {
            var empty = ScheduleImportParser.Parse("[]");
            var nonObjectItem = ScheduleImportParser.Parse("[" + MinimalSample + ", 42]");
            var duplicate = ScheduleImportParser.Parse("[" + MinimalSample + "," + MinimalSample + "]");

            Assert.False(empty.IsValid);
            Assert.Contains(empty.Errors, e => e.Field == "<root>");

            Assert.False(nonObjectItem.IsValid);
            Assert.Contains(nonObjectItem.Errors, e => e.Field == "[1]");

            Assert.False(duplicate.IsValid);
            Assert.Contains(duplicate.Errors, e => e.Field == "[1].activityId");
        }

        [Fact]
        public void Import_parser_reports_array_item_validation_errors_with_item_index()
        {
            var invalid = MinimalSample.Replace("\"outputTable\": \"DemoMinimalOutput\",", "\"unknownField\": true,", StringComparison.Ordinal);

            var result = ScheduleImportParser.Parse("[" + invalid + "]");

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Field == "[0].unknownField");
            Assert.Contains(result.Errors, e => e.Field == "[0].outputTable");
        }

        [Fact]
        public void Parser_treats_omitted_and_empty_tags_as_no_tags()
        {
            var omitted = ScheduleParser.Parse(MinimalSample);
            var empty = ScheduleParser.Parse(WithTopLevel(MinimalSample, "\"tags\": []"));

            Assert.True(omitted.IsValid, string.Join(Environment.NewLine, omitted.Errors.Select(e => $"{e.Field}: {e.Message}")));
            Assert.True(empty.IsValid, string.Join(Environment.NewLine, empty.Errors.Select(e => $"{e.Field}: {e.Message}")));
            Assert.Empty(omitted.Definition!.Tags);
            Assert.Empty(empty.Definition!.Tags);
        }

        [Fact]
        public void Parser_accepts_and_preserves_optional_markdown_description()
        {
            const string description = "# Purpose\n\nRuns **daily**.\n\n    keep indentation";
            var json = WithTopLevel(
                MinimalSample,
                "\"description\": " + System.Text.Json.JsonSerializer.Serialize(description));

            var result = ScheduleParser.Parse(json);

            Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors.Select(e => $"{e.Field}: {e.Message}")));
            Assert.Equal(description, result.Definition!.Description);
            Assert.Null(ScheduleParser.Parse(MinimalSample).Definition!.Description);
            Assert.Null(ScheduleParser.Parse(WithTopLevel(MinimalSample, "\"description\": null")).Definition!.Description);
        }

        [Fact]
        public void Parser_accepts_description_at_the_length_limit()
        {
            var description = new string('a', JobDescription.MaxLength);
            var json = WithTopLevel(
                MinimalSample,
                "\"description\": " + System.Text.Json.JsonSerializer.Serialize(description));

            var result = ScheduleParser.Parse(json);

            Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors.Select(e => $"{e.Field}: {e.Message}")));
            Assert.Equal(JobDescription.MaxLength, result.Definition!.Description!.Length);
        }

        [Fact]
        public void Parser_rejects_non_string_and_over_limit_descriptions()
        {
            var nonString = ScheduleParser.Parse(WithTopLevel(MinimalSample, "\"description\": 42"));
            var tooLong = ScheduleParser.Parse(WithTopLevel(
                MinimalSample,
                "\"description\": " + System.Text.Json.JsonSerializer.Serialize(new string('a', JobDescription.MaxLength + 1))));

            Assert.False(nonString.IsValid);
            Assert.Contains(nonString.Errors, e => e.Field == "description");
            Assert.False(tooLong.IsValid);
            Assert.Contains(tooLong.Errors, e => e.Field == "description" && e.Message.Contains("65536", StringComparison.Ordinal));
        }

        [Fact]
        public void Parser_normalizes_and_deduplicates_tags()
        {
            var result = ScheduleParser.Parse(WithTopLevel(MinimalSample, "\"tags\": [\" Prod \", \"daily\", \"PROD\", \"security\"]"));

            Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors.Select(e => $"{e.Field}: {e.Message}")));
            Assert.Equal(["prod", "daily", "security"], result.Definition!.Tags);
        }

        [Theory]
        [InlineData("\"prod\"", "tags")]
        [InlineData("[\"prod\", 42]", "tags[1]")]
        [InlineData("[\"prod\", \"   \"]", "tags[1]")]
        public void Parser_rejects_invalid_tags(string tagsJson, string expectedField)
        {
            var result = ScheduleParser.Parse(WithTopLevel(MinimalSample, $"\"tags\": {tagsJson}"));

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Field == expectedField);
        }

        [Fact]
        public void Parser_accepts_and_normalizes_top_level_guid_id()
        {
            var raw = "6F9619FF-8B86-D011-B42D-00CF4FC964FF";

            var result = ScheduleParser.Parse(WithTopLevel(MinimalSample, $"\"id\": \"{raw}\""));

            Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors.Select(e => $"{e.Field}: {e.Message}")));
            Assert.Equal(Guid.Parse(raw).ToString("N"), result.Definition!.Id);
        }

        [Theory]
        [InlineData("not-a-guid")]
        [InlineData("")]
        public void Parser_rejects_invalid_top_level_id(string idValue)
        {
            var result = ScheduleParser.Parse(WithTopLevel(MinimalSample, $"\"id\": \"{idValue}\""));

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Field == "id");
        }

        [Fact]
        public void Parser_accepts_dependsOn_referenced_by_guid_id()
        {
            var upstream = Guid.NewGuid().ToString("N");

            var result = ScheduleParser.Parse(WithTopLevel(MinimalSample, $"\"dependsOn\": [ {{ \"id\": \"{upstream}\" }} ]"));

            Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors.Select(e => $"{e.Field}: {e.Message}")));
            var dependency = Assert.Single(result.Definition!.DependsOn);
            Assert.Equal(upstream, dependency.Id);
            Assert.Null(dependency.ActivityId);
        }

        [Fact]
        public void Parser_accepts_dependsOn_with_both_id_and_activity_id()
        {
            var upstream = Guid.NewGuid().ToString("N");

            var result = ScheduleParser.Parse(WithTopLevel(MinimalSample, $"\"dependsOn\": [ {{ \"id\": \"{upstream}\", \"activityId\": \"demo.upstream\" }} ]"));

            Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors.Select(e => $"{e.Field}: {e.Message}")));
            var dependency = Assert.Single(result.Definition!.DependsOn);
            Assert.Equal(upstream, dependency.Id);
            Assert.Equal("demo.upstream", dependency.ActivityId);
        }

        [Fact]
        public void Parser_rejects_dependsOn_with_invalid_guid_id()
        {
            var result = ScheduleParser.Parse(WithTopLevel(MinimalSample, "\"dependsOn\": [ { \"id\": \"nope\" } ]"));

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Field == "dependsOn[0].id");
        }

        [Fact]
        public void Parser_rejects_dependsOn_entry_without_id_or_activity_id()
        {
            var result = ScheduleParser.Parse(WithTopLevel(MinimalSample, "\"dependsOn\": [ { } ]"));

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Field == "dependsOn[0]");
        }

        [Fact]
        public void Parser_rejects_unknown_dependsOn_fields()
        {
            var result = ScheduleParser.Parse(WithTopLevel(MinimalSample, "\"dependsOn\": [ { \"activityId\": \"demo.upstream\", \"bogus\": true } ]"));

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Field == "dependsOn[0].bogus");
        }

        [Fact]
        public void Parser_rejects_self_dependency_by_id()
        {
            var self = Guid.NewGuid().ToString("N");
            var json = WithTopLevel(WithTopLevel(MinimalSample, $"\"id\": \"{self}\""), $"\"dependsOn\": [ {{ \"id\": \"{self}\" }} ]");

            var result = ScheduleParser.Parse(json);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Field == "dependsOn[0]" && e.Message.Contains("self-dependency", StringComparison.Ordinal));
        }

        [Fact]
        public void Parser_rejects_self_dependency_by_activity_id()
        {
            var result = ScheduleParser.Parse(WithTopLevel(MinimalSample, "\"dependsOn\": [ { \"activityId\": \"demo.minimal\" } ]"));

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Field == "dependsOn[0]" && e.Message.Contains("self-dependency", StringComparison.Ordinal));
        }

        public static TheoryData<string, string, int> ValidSamples() => new()
        {
            { MinimalSample, "demo.minimal", 0 },
            { FullSupportedSample, "demo.full-supported", 2 },
            { BoundedSample, "demo.bounded", 0 }
        };

        [Fact]
        public void Parser_defaults_healthPolicy_to_complete_when_absent()
        {
            var result = ScheduleParser.Parse(MinimalSample);

            Assert.True(result.IsValid);
            Assert.Equal(JobHealthPolicy.Complete, result.Definition!.HealthPolicy);
        }

        [Theory]
        [InlineData("complete", JobHealthPolicy.Complete)]
        [InlineData("recent", JobHealthPolicy.Recent)]
        [InlineData("Recent", JobHealthPolicy.Recent)]
        [InlineData("COMPLETE", JobHealthPolicy.Complete)]
        public void Parser_accepts_healthPolicy_values(string value, JobHealthPolicy expected)
        {
            var result = ScheduleParser.Parse(WithTopLevel(MinimalSample, $"\"healthPolicy\": \"{value}\""));

            Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors.Select(e => $"{e.Field}: {e.Message}")));
            Assert.Equal(expected, result.Definition!.HealthPolicy);
        }

        [Fact]
        public void Parser_rejects_unknown_healthPolicy_value()
        {
            var result = ScheduleParser.Parse(WithTopLevel(MinimalSample, "\"healthPolicy\": \"bogus\""));

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Field == "healthPolicy");
        }

        [Fact]
        public void Parser_rejects_non_string_healthPolicy()
        {
            var result = ScheduleParser.Parse(WithTopLevel(MinimalSample, "\"healthPolicy\": true"));

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Field == "healthPolicy");
        }

        [Theory]
        [InlineData(1)]
        [InlineData(32)]
        public void Parser_accepts_supported_chunk_counts(int chunks)
        {
            var result = ScheduleParser.Parse(WithTopLevel(MinimalSample, $"\"chunks\": {chunks}"));

            Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors.Select(e => $"{e.Field}: {e.Message}")));
            Assert.Equal(chunks, result.Definition!.Chunks);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(33)]
        public void Parser_rejects_out_of_range_chunk_counts(int chunks)
        {
            var result = ScheduleParser.Parse(WithTopLevel(MinimalSample, $"\"chunks\": {chunks}"));

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Field == "chunks");
        }

        [Fact]
        public void Parser_rejects_non_integer_chunk_counts()
        {
            var result = ScheduleParser.Parse(WithTopLevel(MinimalSample, "\"chunks\": 2.5"));

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Field == "chunks");
        }

        private const string MinimalSample = """
        {
          "activityId": "demo.minimal",
          "functionName": "DemoMinimal",
          "outputTable": "DemoMinimalOutput",
          "queryWindowSize": "00:05:00",
          "delayFromUtcNow": "00:00:00",
          "maxParallelism": 1,
          "queryTimeout": "00:01:00",
          "isPaused": true,
          "startFrom": "2024-06-01T00:00:00+00:00",
          "target": {
            "clusterUri": "https://kolite-example.invalid",
            "database": "DemoDb"
          }
        }
        """;

        private const string FullSupportedSample = """
        {
          "activityId": "demo.full-supported",
          "functionName": "DemoFullSupported",
          "outputTable": "DemoFullSupportedOutput",
          "queryWindowSize": "01:00:00",
          "delayFromUtcNow": "00:15:00",
          "maxParallelism": 4,
          "queryTimeout": "00:30:00",
          "isPaused": false,
          "startFrom": "2024-01-01T00:00:00+00:00",
          "endOn": "2024-01-08T00:00:00+00:00",
          "folder": "Demo/Full",
          "dependsOn": [
            { "activityId": "demo.upstream-a" },
            { "activityId": "demo.upstream-b" }
          ],
          "jobSettings": {
            "foo": "bar",
            "nested": { "x": 1 }
          },
          "target": {
            "clusterUri": "https://kolite-example.invalid",
            "database": "DemoDb"
          }
        }
        """;

        private const string BoundedSample = """
        {
          "activityId": "demo.bounded",
          "functionName": "DemoBounded",
          "outputTable": "DemoBoundedOutput",
          "queryWindowSize": "00:15:00",
          "delayFromUtcNow": "00:05:00",
          "maxParallelism": 1,
          "queryTimeout": "00:05:00",
          "isPaused": false,
          "startFrom": "2026-01-01T00:00:00Z",
          "endOn": "2026-01-02T00:00:00Z",
          "folder": "Demo/Bounded",
          "target": {
            "clusterUri": "https://kolite-example.invalid",
            "database": "DemoDb"
          }
        }
        """;

        private static string WithTopLevel(string json, string propertyJson) =>
            json.Replace("  \"target\":", $"  {propertyJson},\n  \"target\":", StringComparison.Ordinal);
    }
}
