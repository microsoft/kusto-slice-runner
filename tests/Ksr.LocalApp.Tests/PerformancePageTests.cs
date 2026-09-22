// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ksr.Local.Core.Performance;
using Ksr.Local.Core.Time;
using Ksr.Local.Sqlite.Catalog;
using Ksr.Local.Sqlite.Connections;
using Ksr.Local.Sqlite.Lifecycle;
using Ksr.Local.Sqlite.Observability;
using Ksr.Local.Sqlite.Schema;
using Ksr.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Ksr.LocalApp.Tests
{
    public sealed class PerformancePageTests : IDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 9, 11, 19, 17, 23, 456, TimeSpan.Zero);
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "performance-page-tests", Guid.NewGuid().ToString("N"));
        private readonly string databasePath;
        private readonly KsrSqliteConnectionFactory sqlite;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly ManualClock clock = new(Now);
        private readonly FakeReportRepository repository = new();
        private WebApplicationFactory<Program>? factory;

        public PerformancePageTests()
        {
            Directory.CreateDirectory(testDirectory);
            databasePath = Path.Combine(testDirectory, "performance.db");
            sqlite = new KsrSqliteConnectionFactory(new KsrSqliteConnectionOptions(databasePath));
            new KsrSqliteSchema(sqlite).EnsureSchema();
            catalog = new SqliteJobCatalogRepository(sqlite);
        }

        [Theory]
        [InlineData(null, "7d", 168)]
        [InlineData("invalid", "7d", 168)]
        [InlineData("1h", "1h", 1)]
        [InlineData("1d", "1d", 24)]
        [InlineData("7d", "7d", 168)]
        [InlineData("30d", "30d", 720)]
        public void Presets_use_one_exact_trailing_completion_interval(string? requested, string expected, int hours)
        {
            var job = CreateJob("period");
            repository.Rows.Add(Row(job.JobId));

            var data = Query().Get(requested);
            var call = Assert.Single(repository.Calls);

            Assert.Equal(expected, data.State.Range);
            Assert.Equal(Now, data.ToUtc);
            Assert.Equal(Now.AddHours(-hours), data.FromUtc);
            Assert.Equal(data.FromUtc, call.FromUtc);
            Assert.Equal(data.ToUtc, call.ToUtc);
            Assert.Equal(Now - PerformanceCoveragePolicy.GracePeriod, call.CoverageCutoffUtc);
            Assert.Equal([job.JobId], call.JobIds);
            Assert.Equal("1d", ChartRangeOptions.Normalize(null));
            Assert.Equal("24 hours", PerformanceRangeOptions.Links.Single(link => link.Key == "1d").Label);
        }

        [Fact]
        public void Includes_paused_and_ended_jobs_but_not_soft_deleted_or_empty_jobs()
        {
            var paused = CreateJob("alpha.paused", paused: true);
            var ended = CreateJob("beta.ended", endOn: "2026-01-01T00:10:00Z");
            var deleted = CreateJob("deleted");
            var empty = CreateJob("empty");
            new SqliteJobLifecycleService(sqlite, catalog).SoftDelete(deleted.JobId, deleted.CatalogVersion, "test", "exclude");
            repository.Rows.AddRange([Row(paused.JobId), Row(ended.JobId), Row(deleted.JobId)]);

            var data = Query().Get();

            Assert.Equal([paused.JobId, ended.JobId], data.Jobs.Select(job => job.JobId));
            var included = Assert.Single(repository.Calls).JobIds!;
            Assert.DoesNotContain(deleted.JobId, included);
            Assert.Contains(empty.JobId, included);
        }

        [Fact]
        public void Retains_pooled_job_statistics_and_fills_all_configured_chunk_ids()
        {
            var job = CreateJob("chunked", chunks: 12);
            repository.Rows.AddRange([
                Row(job.JobId, completed: 17, succeeded: 16, cpu: 40),
                Row(job.JobId, cpu: 100, chunkId: 10, total: false),
                Row(job.JobId, cpu: 1, chunkId: 2, total: false)
            ]);

            var group = Assert.Single(Query().Get().Jobs);

            Assert.Equal(17L, group.Total.CompletedAttempts);
            Assert.Equal(16L, group.Total.SucceededAttempts);
            Assert.Equal(40d, group.Total.CpuSeconds.P50);
            Assert.Equal("94.1%", PerformanceFormatting.Success(group.Total));
            Assert.Equal(Enumerable.Range(0, 12), group.Chunks.Select(chunk => chunk.ChunkId!.Value));
            Assert.Equal(0L, group.Chunks[0].CompletedAttempts);
            Assert.Null(group.Chunks[0].CpuSeconds.P50);
            Assert.Equal(100d, group.Chunks[10].CpuSeconds.P50);
            Assert.Equal(1d, group.Chunks[2].CpuSeconds.P50);
        }

        [Fact]
        public void Explicit_one_chunk_remains_expandable_and_unchunked_has_no_children()
        {
            var chunked = CreateJob("chunked.one", chunks: 1);
            var unchunked = CreateJob("unchunked");
            repository.Rows.AddRange([
                Row(chunked.JobId),
                Row(chunked.JobId, chunkId: 0, total: false),
                Row(unchunked.JobId)
            ]);

            var data = Query().Get();

            Assert.Single(data.Jobs.Single(job => job.JobId == chunked.JobId).Chunks);
            Assert.Empty(data.Jobs.Single(job => job.JobId == unchunked.JobId).Chunks);
        }

        [Fact]
        public void Numeric_sorting_keeps_missing_values_last_and_ties_stable()
        {
            var alpha = CreateJob("alpha");
            var beta = CreateJob("beta");
            var gamma = CreateJob("gamma");
            var delta = CreateJob("delta");
            repository.Rows.AddRange([
                Row(alpha.JobId, cpu: 2),
                Row(beta.JobId, cpu: 10),
                Row(gamma.JobId, cpu: null),
                Row(delta.JobId, cpu: 2)
            ]);

            Assert.Equal(["alpha", "delta", "beta", "gamma"],
                Query().Get(sort: "cpu-p95").Jobs.Select(job => job.ActivityId));
            Assert.Equal(["beta", "alpha", "delta", "gamma"],
                Query().Get(sort: "cpu-p95", direction: "desc").Jobs.Select(job => job.ActivityId));
            Assert.Equal("activity", PerformanceSort.Parse("not-a-column", "asc").Key);
        }

        [Fact]
        public void Count_sort_does_not_round_large_long_values_to_double()
        {
            var smaller = CreateJob("z-smaller");
            var larger = CreateJob("a-larger");
            repository.Rows.AddRange([
                Row(smaller.JobId, completed: 9_007_199_254_740_992, succeeded: 1),
                Row(larger.JobId, completed: 9_007_199_254_740_993, succeeded: 1)
            ]);

            Assert.Equal([smaller.JobId, larger.JobId],
                Query().Get(sort: "attempts").Jobs.Select(job => job.JobId));
        }

        [Theory]
        [InlineData("activity")]
        [InlineData("success")]
        [InlineData("cpu-p50")]
        [InlineData("cpu-p90")]
        [InlineData("cpu-p95")]
        [InlineData("duration-p50")]
        [InlineData("duration-p90")]
        [InlineData("duration-p95")]
        [InlineData("memory-p50")]
        [InlineData("memory-p90")]
        [InlineData("memory-p95")]
        public void Header_links_start_descending_then_toggle_without_changing_initial_order(string key)
        {
            var state = new PerformanceViewState("7d", "A B", ["ops"], "job-id", PerformanceSort.Default);

            Assert.Contains($"sort={key}&dir=desc", state.SortHref(key));
            var descending = state with { Sort = new PerformanceSort(key, true) };
            var ascendingLink = descending.SortHref(key);
            if (key == "activity")
            {
                Assert.Equal(state.Href, ascendingLink);
            }
            else
            {
                Assert.Contains($"sort={key}&dir=asc", ascendingLink);
            }

            Assert.Contains($"sort={key}&dir=desc", (state with { Sort = new PerformanceSort(key, false) }).SortHref(key));
            var otherKey = key == "cpu-p50" ? "cpu-p90" : "cpu-p50";
            var switched = (state with { Sort = new PerformanceSort(otherKey, true) }).SortHref(key);
            Assert.Contains($"sort={key}&dir=desc", switched);
            Assert.Contains("view=performance&range=7d&q=A%20B&tag=ops&jobId=job-id", switched);
            Assert.Equal(new PerformanceSort("activity", false), PerformanceSort.Default);
            Assert.False(PerformanceSort.Parse("cpu-p50", null).Descending);
            Assert.Equal(new PerformanceSort("attempts", true), PerformanceSort.Parse("attempts", "desc"));
        }

        [Fact]
        public void Metric_tooltips_preserve_exact_values_and_distinct_sample_counts()
        {
            var row = Row("job", completed: 17, succeeded: 16) with
            {
                CpuSeconds = new PerformancePercentiles(2, 12.3456789, 20, 30),
                DurationSeconds = new PerformancePercentiles(1, 0, 0, 0),
                MemoryGiB = PerformancePercentiles.Empty
            };

            Assert.Equal("CPU P50: 12.3456789 s; 2 samples from 16 successful attempts.",
                PerformanceFormatting.MetricTooltip(row, PerformanceMetrics.All[0], "p50"));
            Assert.Equal("Duration P95: 0 s; 1 sample from 16 successful attempts.",
                PerformanceFormatting.MetricTooltip(row, PerformanceMetrics.All[1], "p95"));
            Assert.Equal("Memory peak P50: unavailable; 0 samples from 16 successful attempts.",
                PerformanceFormatting.MetricTooltip(row, PerformanceMetrics.All[2], "p50"));
            Assert.Equal("12.35", PerformanceFormatting.Value(row.CpuSeconds.P50, "s"));
            Assert.Equal("0.123", PerformanceFormatting.Value(0.123456, "GiB"));
            Assert.Contains("1 sample from 1 successful attempt.",
                PerformanceFormatting.MetricTooltip(row with { SucceededAttempts = 1 }, PerformanceMetrics.All[1], "p50"));
        }

        [Fact]
        public void Filters_preserve_GUID_identity_current_label_tags_range_and_sort()
        {
            var job = CreateJob("old.label", tags: ["prod", "daily"]);
            var other = CreateJob("other", tags: ["prod", "weekly"]);
            catalog.Update(job.JobId, Schedule("new.label", id: job.JobId, tags: ["prod", "daily"]), job.CatalogVersion);
            repository.Rows.AddRange([Row(job.JobId), Row(other.JobId)]);

            var data = Query().Get(
                range: "1d", search: " NEW ", tags: ["PROD", "daily"],
                jobId: Guid.Parse(job.JobId).ToString("D"), sort: "memory-p90", direction: "desc");

            Assert.Equal("new.label", Assert.Single(data.Jobs).ActivityId);
            Assert.Equal(job.JobId, data.State.JobId);
            Assert.Equal(1, data.VisibleJobCount);
            Assert.Equal([job.JobId], Assert.Single(repository.Calls).JobIds);
            var link = data.State.RangeHref("30d");
            Assert.Contains("view=performance&range=30d", link);
            Assert.Contains("q=NEW", link);
            Assert.Contains("tag=prod", link);
            Assert.Contains("tag=daily", link);
            Assert.Contains("jobId=" + job.JobId, link);
            Assert.Contains("sort=memory-p90&dir=desc", link);
            Assert.DoesNotContain("q=", data.State.ClearFiltersHref);
            Assert.DoesNotContain("jobId=", data.State.ClearFiltersHref);
            Assert.DoesNotContain("tag=", data.State.ClearFiltersHref);
        }

        [Fact]
        public void Text_filter_retains_groups_for_local_clear_and_expansion_preservation()
        {
            var alpha = CreateJob("alpha");
            var beta = CreateJob("beta");
            repository.Rows.AddRange([Row(alpha.JobId), Row(beta.JobId)]);

            var data = Query().Get(search: "alpha");

            Assert.Equal(2, data.Jobs.Count);
            Assert.Equal(1, data.VisibleJobCount);
        }

        [Fact]
        public void Initialization_withholds_aggregates_and_invalid_job_filter_is_explicit()
        {
            var job = CreateJob("initializing");
            repository.Rows.Add(Row(job.JobId));
            repository.Status = repository.Status with { HistoryInitialized = false };

            var initializing = Query(enabled: false).Get();

            Assert.False(initializing.Collection.HistoryInitialized);
            Assert.False(initializing.ExecutionEnabled);
            Assert.Empty(initializing.Jobs);
            Assert.Empty(repository.Calls);

            repository.Status = repository.Status with { HistoryInitialized = true };
            var invalid = Query().Get(jobId: "not-a-guid");
            Assert.Equal("The job filter must be a valid job ID.", invalid.FilterError);
            Assert.Empty(repository.Calls);
        }

        [Fact]
        public async Task Live_view_never_reads_the_performance_repository()
        {
            repository.ThrowOnRead = true;
            using var client = CreateClient();

            var page = await client.GetStringAsync("/activity?range=1h");

            Assert.Contains("Running now", page);
            Assert.Contains("Executions processed over time", page);
            Assert.Contains("/activity?view=performance&amp;range=7d", page);
            Assert.DoesNotContain("data-performance-table", page);
            Assert.Equal(0, repository.StatusReads);
            Assert.Empty(repository.Calls);
        }

        [Fact]
        public async Task Performance_renders_nine_metrics_grouped_headers_and_collapsed_raw_children()
        {
            var job = CreateJob("job.performance", chunks: 32, tags: ["ops"]);
            repository.Rows.Add(Row(job.JobId, completed: 17, succeeded: 16) with
            {
                CpuSeconds = new PerformancePercentiles(2, 10, 11, 12),
                DurationSeconds = new PerformancePercentiles(1, 20, 20, 20),
                MemoryGiB = PerformancePercentiles.Empty
            });
            repository.Rows.Add(Row(job.JobId, chunkId: 10, total: false));
            using var client = CreateClient();

            var page = await client.GetStringAsync("/activity?view=performance");
            var headers = Regex.Match(page, @"<thead>(.*?)</thead>", RegexOptions.Singleline).Groups[1].Value;

            Assert.Equal(2, Regex.Matches(headers, "rowspan=\"2\"").Count);
            Assert.Equal(3, Regex.Matches(headers, "scope=\"colgroup\" colspan=\"3\"").Count);
            Assert.Equal(9, Regex.Matches(headers, "data-performance-sort=\"").Count);
            Assert.Contains("CPU (s)", page);
            Assert.Contains("Duration (s)", page);
            Assert.Contains("Memory peak (GiB)", page);
            Assert.Contains("94.1%", page);
            Assert.Contains("title=\"CPU P50: 10 s; 2 samples from 16 successful attempts.\"", page);
            Assert.Contains("title=\"Duration P50: 20 s; 1 sample from 16 successful attempts.\"", page);
            Assert.Contains("aria-label=\"Memory peak P50: unavailable; 0 samples from 16 successful attempts.\"", page);
            Assert.DoesNotContain("performance-col-attempts", page);
            Assert.DoesNotContain("performance-col-samples", page);
            Assert.DoesNotContain("Metric samples", headers);
            Assert.DoesNotContain("Completed attempts", headers);
            Assert.Contains("n/a", page);
            Assert.Contains("aria-expanded=\"false\"", page);
            Assert.Contains("tabindex=\"0\" role=\"region\" aria-label=\"Historical job performance\"", page);
            Assert.Contains($"/jobs/{job.JobId}", page);
            Assert.Contains(PerformanceFormatting.Timestamp(Now.AddDays(-7)), page);
            Assert.Contains(PerformanceFormatting.Timestamp(Now), page);
            Assert.Contains("data-performance-no-execution", page);
            Assert.DoesNotContain("Running now", page);
            Assert.DoesNotContain("Unknown attempts", page);
            Assert.DoesNotContain("Ignored attempts", page);
            Assert.DoesNotContain("data-running-chunk-id", page);
            var childTags = Regex.Matches(page, @"<tr\b[^>]*data-performance-chunk-row[^>]*>");
            Assert.Equal(32, childTags.Count);
            Assert.All(childTags.Cast<Match>(), match => Assert.Matches(@"\bhidden(?:\s|=|>)", match.Value));
            Assert.Equal(Enumerable.Range(0, 32), Regex.Matches(page, "data-performance-chunk-id=\"(\\d+)\"").Cast<Match>().Select(match => int.Parse(match.Groups[1].Value)));
            foreach (Match body in Regex.Matches(page, @"<tbody\b.*?</tbody>", RegexOptions.Singleline))
            {
                foreach (Match row in Regex.Matches(body.Value, @"<tr\b.*?</tr>", RegexOptions.Singleline))
                {
                    Assert.Equal(11, Regex.Matches(row.Value, @"<(?:td|th)\b").Count);
                }
            }
        }

        [Fact]
        public async Task Performance_links_preserve_filters_and_sort_and_initialization_is_not_an_empty_report()
        {
            var job = CreateJob("job.filtered", tags: ["ops"]);
            repository.Rows.Add(Row(job.JobId));
            using var client = CreateClient();
            var path = $"/activity?view=performance&range=7d&tag=ops&q=job&jobId={job.JobId}&sort=cpu-p95&dir=desc";

            var page = WebUtility.HtmlDecode(await client.GetStringAsync(path));
            Assert.Contains($"view=performance&range=1d&q=job&tag=ops&jobId={job.JobId}&sort=cpu-p95&dir=desc", page);
            Assert.Contains("24 hours", page);

            repository.Status = repository.Status with { HistoryInitialized = false, LastError = "<collection failed>" };
            var initializing = await client.GetStringAsync(path);
            Assert.Contains("data-performance-initializing", initializing);
            Assert.Contains("&lt;collection failed&gt;", initializing);
            Assert.DoesNotContain("data-performance-table", initializing);
            Assert.DoesNotContain("No completed attempts in this period.", initializing);
        }

        [Fact]
        public async Task Job_details_link_to_the_GUID_filtered_report_without_querying_it()
        {
            var job = CreateJob("job.discovery");
            repository.ThrowOnRead = true;
            using var client = CreateClient();

            var page = await client.GetStringAsync("/jobs/" + job.JobId);

            Assert.Contains($"/activity?view=performance&amp;range=7d&amp;jobId={job.JobId}", page);
            Assert.Equal(0, repository.StatusReads);
        }

        [Theory]
        [InlineData(0, 0, false, true)]
        [InlineData(3, 3, false, true)]
        [InlineData(20, 4, false, true)]
        [InlineData(26, 5, false, true)]
        [InlineData(25, 5, true, false)]
        [InlineData(25, 6, true, true)]
        public async Task Prominent_warning_uses_scoped_coverage_not_the_global_error(
            long eligible, long missing, bool expectedWarning, bool hasDiagnostic)
        {
            const string diagnostic = "Authentication failed for an older retained-history lookup.";
            var job = CreateJob("coverage");
            repository.Rows.Add(Row(job.JobId, completed: Math.Max(1, eligible), succeeded: Math.Max(1, eligible)) with
            {
                Coverage = new PerformanceCoverageCounts(eligible, missing)
            });
            repository.Status = repository.Status with { LastError = hasDiagnostic ? diagnostic : null };
            using var client = CreateClient();

            var page = await client.GetStringAsync("/activity?view=performance");
            var warning = Regex.Match(page, @"<div\b[^>]*data-performance-coverage-warning\b[^>]*>");

            Assert.True(warning.Success);
            Assert.Equal(!expectedWarning, Regex.IsMatch(warning.Value, @"\bhidden(?:\s|=|>)"));
            Assert.Contains("data-performance-threshold-percent=\"20\"", warning.Value);
            Assert.Contains("data-performance-minimum-missing=\"5\"", warning.Value);
            Assert.Contains(PerformanceFormatting.CoverageMessage(new PerformanceCoverageCounts(eligible, missing)), page);
            if (hasDiagnostic)
            {
                var details = Regex.Match(page, "<section class=\"performance-collection\"[^>]*>.*?</section>", RegexOptions.Singleline);
                Assert.Contains(diagnostic, details.Value);
                Assert.Contains("Latest diagnostic (all retained history)", details.Value);
                Assert.DoesNotContain(diagnostic, page.Replace(details.Value, string.Empty, StringComparison.Ordinal));
                Assert.DoesNotContain("role=\"alert\"", details.Value);
            }
        }

        [Fact]
        public async Task Warning_follows_job_tag_and_name_filters_and_never_sums_child_rows_twice()
        {
            var alpha = CreateJob("Alpha", chunks: 1, tags: ["incomplete"]);
            var beta = CreateJob("Beta", tags: ["complete"]);
            var alphaRow = Row(alpha.JobId, completed: 25, succeeded: 25) with { Coverage = new(25, 5) };
            repository.Rows.AddRange([
                alphaRow,
                alphaRow with { ChunkId = 0, IsJobTotal = false },
                Row(beta.JobId, completed: 75, succeeded: 75) with { Coverage = new(75, 0) }
            ]);
            Assert.Equal(new PerformanceCoverageCounts(100, 5), Query().Get().VisibleCoverage);
            Assert.Equal(new PerformanceCoverageCounts(25, 5), Query().Get(search: "alpha").VisibleCoverage);
            Assert.Equal(new PerformanceCoverageCounts(25, 5), Query().Get(tags: ["incomplete"]).VisibleCoverage);
            Assert.Equal(new PerformanceCoverageCounts(25, 5), Query().Get(jobId: alpha.JobId).VisibleCoverage);
            Assert.Equal(PerformanceCoverageCounts.Empty, Query().Get(search: "missing").VisibleCoverage);
            using var client = CreateClient();

            var all = await client.GetStringAsync("/activity?view=performance");
            var filtered = await client.GetStringAsync("/activity?view=performance&q=alpha");
            var sorted = await client.GetStringAsync("/activity?view=performance&q=alpha&sort=cpu-p95&dir=desc");
            var allWarning = Regex.Match(all, @"<div\b[^>]*data-performance-coverage-warning\b[^>]*>").Value;
            var filteredWarning = Regex.Match(filtered, @"<div\b[^>]*data-performance-coverage-warning\b[^>]*>").Value;

            Assert.Matches(@"\bhidden(?:\s|=|>)", allWarning);
            Assert.DoesNotMatch(@"\bhidden(?:\s|=|>)", filteredWarning);
            Assert.Contains("5 of 25 eligible successful attempts (20%)", filtered);
            Assert.Contains("5 of 25 eligible successful attempts (20%)", sorted);
            Assert.Contains("data-performance-eligible-attempts=\"25\"", filtered);
            Assert.Contains("data-performance-missing-attempts=\"5\"", filtered);
        }

        [Fact]
        public async Task Initializing_history_and_invalid_filters_do_not_evaluate_a_coverage_warning()
        {
            var job = CreateJob("initializing.warning");
            repository.Rows.Add(Row(job.JobId, completed: 5, succeeded: 5) with { Coverage = new(5, 5) });
            repository.Status = repository.Status with { HistoryInitialized = false, LastError = "<history failed>" };
            using var client = CreateClient();

            var initializing = await client.GetStringAsync("/activity?view=performance");
            Assert.DoesNotContain("data-performance-coverage-warning", initializing);
            Assert.Contains("&lt;history failed&gt;", initializing);
            Assert.Contains("data-performance-initializing", initializing);

            repository.Status = repository.Status with { HistoryInitialized = true };
            using var invalid = await client.GetAsync("/activity?view=performance&jobId=not-a-guid");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.DoesNotContain("data-performance-coverage-warning", await invalid.Content.ReadAsStringAsync());
        }

        [Theory]
        [InlineData("populated")]
        [InlineData("empty")]
        [InlineData("initializing")]
        [InlineData("invalid")]
        public async Task Collection_and_calculation_sections_are_plain_and_follow_every_report_state(string state)
        {
            var job = CreateJob("footer");
            if (state != "empty") repository.Rows.Add(Row(job.JobId));
            repository.Status = repository.Status with
            {
                HistoryInitialized = state != "initializing",
                LastError = "<footer diagnostic>"
            };
            using var client = CreateClient();
            var path = "/activity?view=performance" + (state == "invalid" ? "&jobId=invalid" : string.Empty);
            using var response = await client.GetAsync(path);
            var page = await response.Content.ReadAsStringAsync();
            var footer = Regex.Match(page, @"<div class=""performance-footer""[^>]*>.*?</div>", RegexOptions.Singleline);

            Assert.True(footer.Success);
            Assert.DoesNotContain("<details", footer.Value);
            Assert.DoesNotContain("<summary", footer.Value);
            Assert.DoesNotContain("aria-expanded", footer.Value);
            Assert.Single(Regex.Matches(page, "id=\"performance-collection-heading\""));
            Assert.Single(Regex.Matches(page, "id=\"performance-definitions-heading\""));
            Assert.Contains("Collection details", footer.Value);
            Assert.Contains("How these statistics are calculated", footer.Value);
            Assert.Contains("&lt;footer diagnostic&gt;", footer.Value);
            Assert.Contains("data-performance-no-execution", footer.Value);
            Assert.Contains("id=\"performance-statistics-help\"", footer.Value);
            Assert.Contains("Hover a metric value", footer.Value);
            Assert.DoesNotContain("role=\"alert\"", footer.Value);
            var preceding = state switch
            {
                "populated" => "</table>",
                "empty" => "No completed attempts in this period.",
                "initializing" => "data-performance-initializing",
                _ => "The job filter must be a valid job ID."
            };
            Assert.True(page.IndexOf(preceding, StringComparison.Ordinal) >= 0);
            Assert.True(page.IndexOf(preceding, StringComparison.Ordinal) < footer.Index);
            if (state == "invalid") Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        private PerformancePageQuery Query(bool enabled = true) =>
            new(repository, catalog, new LifecycleReadModel(new SqliteLifecycleReadModelRepository(sqlite)),
                clock, new LocalBackgroundSchedulerOptions(enabled, TimeSpan.FromSeconds(10), false));

        private JobCatalogRecord CreateJob(string label, int? chunks = null, bool paused = false, string[]? tags = null, string? endOn = null) =>
            catalog.Create(Schedule(label, chunks: chunks, paused: paused, tags: tags, endOn: endOn));

        private static string Schedule(string label, string? id = null, int? chunks = null, bool paused = false, string[]? tags = null, string? endOn = null)
        {
            var schedule = JsonNode.Parse(SampleScheduleFactory.CreateJson())?.AsObject()
                ?? throw new InvalidOperationException("The sample schedule must be a JSON object.");
            schedule["id"] = id ?? Guid.NewGuid().ToString("N");
            schedule["activityId"] = label;
            schedule["isPaused"] = paused;
            schedule["queryWindowSize"] = "00:05:00";
            schedule["startFrom"] = "2026-01-01T00:00:00Z";
            schedule["maxParallelism"] = chunks ?? 1;
            if (chunks is { } count) schedule["chunks"] = count;
            else schedule.Remove("chunks");
            if (endOn is not null) schedule["endOn"] = endOn;
            else schedule.Remove("endOn");
            var tagArray = new JsonArray();
            foreach (var tag in tags ?? []) tagArray.Add(tag);
            schedule["tags"] = tagArray;
            schedule["target"] = new JsonObject
            {
                ["clusterUri"] = "https://performance-tests.invalid",
                ["database"] = "Tests"
            };
            return schedule.ToJsonString();
        }

        private HttpClient CreateClient()
        {
            factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:KsrSqlite"] = databasePath,
                    ["Ksr:Scheduler:Enabled"] = "false",
                    ["Ksr:Retention:Enabled"] = "false",
                    ["Ksr:UpdateCheck:Enabled"] = "false"
                }));
                builder.ConfigureServices(services =>
                {
                    services.AddLogging(logging => logging.ClearProviders());
                    services.AddTestLocalRequestPolicy();
                    services.RemoveAll<IClock>();
                    services.AddSingleton<IClock>(clock);
                    services.RemoveAll<IPerformanceReportRepository>();
                    services.AddSingleton<IPerformanceReportRepository>(repository);
                    services.RemoveAll<IKustoCommandStatisticsReader>();
                    services.AddSingleton<IKustoCommandStatisticsReader>(new EmptyStatisticsReader());
                });
            });
            return factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        }

        private static PerformanceAggregateRow Row(string jobId, long completed = 1, long succeeded = 1, double? cpu = 10, int? chunkId = null, bool total = true) =>
            new(jobId, chunkId, total, completed, succeeded,
                cpu is { } value ? new PerformancePercentiles(succeeded, value, value, value) : PerformancePercentiles.Empty,
                new PerformancePercentiles(succeeded, 20, 20, 20),
                new PerformancePercentiles(succeeded, 0.5, 0.5, 0.5));

        public void Dispose()
        {
            factory?.Dispose();
            TestCleanup.DeleteDirectoryBestEffort(testDirectory);
        }

        private sealed record AggregateCall(DateTimeOffset FromUtc, DateTimeOffset ToUtc, IReadOnlyCollection<string>? JobIds, DateTimeOffset? CoverageCutoffUtc);

        private sealed class FakeReportRepository : IPerformanceReportRepository
        {
            public List<PerformanceAggregateRow> Rows { get; } = [];
            public List<AggregateCall> Calls { get; } = [];
            public PerformanceCollectionReadout Status { get; set; } = new(true, Now.AddMinutes(-1), Now.AddSeconds(-2), null, 0, 1);
            public int StatusReads { get; private set; }
            public bool ThrowOnRead { get; set; }

            public IReadOnlyList<PerformanceAggregateRow> GetAggregates(
                DateTimeOffset fromUtc, DateTimeOffset toUtc, IReadOnlyCollection<string>? jobIds = null, DateTimeOffset? coverageCutoffUtc = null)
            {
                if (ThrowOnRead) throw new InvalidOperationException("Unexpected Performance query.");
                Calls.Add(new AggregateCall(fromUtc, toUtc, jobIds?.ToArray(), coverageCutoffUtc));
                return Rows.Where(row => jobIds is null || jobIds.Contains(row.JobId, StringComparer.Ordinal)).ToArray();
            }

            public PerformanceCollectionReadout GetCollectionStatus()
            {
                if (ThrowOnRead) throw new InvalidOperationException("Unexpected Performance status read.");
                StatusReads++;
                return Status;
            }
        }

        private sealed class EmptyStatisticsReader : IKustoCommandStatisticsReader
        {
            public Task<IReadOnlyList<KustoCommandStatistics>> ReadAsync(KustoCommandStatisticsQuery query, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<KustoCommandStatistics>>(Array.Empty<KustoCommandStatistics>());
            }
        }
    }
}
