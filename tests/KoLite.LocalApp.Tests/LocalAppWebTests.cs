using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using KoLite.Local.Core.Orchestration;
using KoLite.Local.Core.Schedules;
using KoLite.Local.Core.Scheduling;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Lifecycle;
using KoLite.Local.Sqlite.Migrations;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Orchestration;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.State;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace KoLite.LocalApp.Tests
{
    public sealed class LocalAppWebTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "localapp-web-tests", Guid.NewGuid().ToString("N"));
        private readonly string databasePath;
        private readonly WebApplicationFactory<Program> factory;
        private readonly KoLiteSqliteConnectionFactory sqlite;

        public LocalAppWebTests()
        {
            Directory.CreateDirectory(testDirectory);
            databasePath = Path.Combine(testDirectory, "web.db");
            sqlite = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(databasePath));
            new KoLiteSqliteMigrator(sqlite).Migrate();
            factory = CreateFactory(enableScheduler: false);
        }

        [Fact]
        public void Scheduler_options_default_to_ten_second_tick_interval()
        {
            var configuration = new ConfigurationBuilder().Build();
            var options = LocalBackgroundSchedulerOptions.From(configuration);
            var workerPool = LocalBackgroundWorkerPoolOptions.From(configuration, options);

            Assert.True(options.Enabled);
            Assert.Equal(TimeSpan.FromSeconds(10), options.TickInterval);
            Assert.False(options.LogEveryPass);
            Assert.Equal("Fixed", workerPool.Mode);
            Assert.Equal(10, workerPool.MaxConcurrency);
            Assert.Equal("Default", workerPool.MaxConcurrencySource);
            Assert.Equal(TimeSpan.FromMilliseconds(250), workerPool.IdleDelay);
            Assert.Equal(100, workerPool.MaxDispatchStartsPerCycle);
            Assert.Equal("Default", workerPool.MaxDispatchStartsPerCycleSource);
        }

        [Fact]
        public void Scheduler_options_preserve_explicit_tick_interval_override()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KoLite:Scheduler:TickInterval"] = "00:00:30"
                })
                .Build();

            var options = LocalBackgroundSchedulerOptions.From(configuration);

            Assert.Equal(TimeSpan.FromSeconds(30), options.TickInterval);
        }

        [Fact]
        public void Worker_pool_options_preserve_scheduler_worker_concurrency_alias()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KoLite:Scheduler:WorkerConcurrency"] = "7"
                })
                .Build();

            var schedulerOptions = LocalBackgroundSchedulerOptions.From(configuration);
            var options = LocalBackgroundWorkerPoolOptions.From(configuration, schedulerOptions);

            Assert.Equal(7, options.MaxConcurrency);
            Assert.Equal("KoLite:Scheduler:WorkerConcurrency", options.MaxConcurrencySource);
        }

        [Fact]
        public void Worker_pool_options_prefer_explicit_worker_pool_settings_over_scheduler_aliases()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KoLite:Scheduler:WorkerConcurrency"] = "7",
                    ["KoLite:Scheduler:MaxWorkerIterations"] = "11",
                    ["KoLite:WorkerPool:MaxConcurrency"] = "12",
                    ["KoLite:WorkerPool:MaxDispatchStartsPerCycle"] = "13",
                    ["KoLite:WorkerPool:IdleDelay"] = "00:00:00.125"
                })
                .Build();

            var schedulerOptions = LocalBackgroundSchedulerOptions.From(configuration);
            var options = LocalBackgroundWorkerPoolOptions.From(configuration, schedulerOptions);

            Assert.Equal(12, options.MaxConcurrency);
            Assert.Equal("KoLite:WorkerPool:MaxConcurrency", options.MaxConcurrencySource);
            Assert.Equal(13, options.MaxDispatchStartsPerCycle);
            Assert.Equal("KoLite:WorkerPool:MaxDispatchStartsPerCycle", options.MaxDispatchStartsPerCycleSource);
            Assert.Equal(TimeSpan.FromMilliseconds(125), options.IdleDelay);
            Assert.Equal("KoLite:WorkerPool:IdleDelay", options.IdleDelaySource);
        }

        [Fact]
        public void Worker_pool_options_reject_invalid_worker_pool_values()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KoLite:WorkerPool:MaxConcurrency"] = "0"
                })
                .Build();

            var schedulerOptions = LocalBackgroundSchedulerOptions.From(configuration);
            var ex = Assert.Throws<InvalidOperationException>(() => LocalBackgroundWorkerPoolOptions.From(configuration, schedulerOptions));

            Assert.Contains("KoLite:WorkerPool:MaxConcurrency", ex.Message);
        }

        [Fact]
        public void Scheduler_options_preserve_explicit_log_every_pass_override()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KoLite:Scheduler:LogEveryPass"] = "true"
                })
                .Build();

            var options = LocalBackgroundSchedulerOptions.From(configuration);

            Assert.True(options.LogEveryPass);
        }

        [Fact]
        public async Task Health_reports_scheduler_log_every_pass_setting()
        {
            using var runFactory = CreateFactory(enableScheduler: false, logEveryPass: true);
            using var client = runFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var health = await client.GetStringAsync("/status/health");
            using var healthJson = JsonDocument.Parse(health);
            var scheduler = healthJson.RootElement.GetProperty("scheduler");

            Assert.True(scheduler.GetProperty("logEveryPass").GetBoolean());
        }

        [Fact]
        public async Task Health_reports_worker_pool_snapshot_and_effective_configuration_sources()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("job.health.pool", "HealthFunction", isPaused: false));
            var state = new SqliteSliceStateRepository(sqlite);
            state.Append("health-pool-queued", "job.health.pool", At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
            var queue = new SqliteWorkQueueRepository(sqlite);
            queue.Enqueue("job.health.pool", At(0), At(5), "health-pool-work", At(0));
            using var runFactory = CreateFactory(
                enableScheduler: false,
                workerPoolMaxConcurrency: 3,
                workerPoolIdleDelay: "00:00:00.123",
                workerPoolMaxDispatchStartsPerCycle: 4);
            using var client = runFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var health = await client.GetStringAsync("/status/health");
            using var healthJson = JsonDocument.Parse(health);
            var workerPool = healthJson.RootElement.GetProperty("workerPool");

            Assert.Equal("Fixed", workerPool.GetProperty("mode").GetString());
            Assert.False(workerPool.GetProperty("enabled").GetBoolean());
            Assert.Equal("KoLite:Scheduler:Enabled", workerPool.GetProperty("enabledSource").GetString());
            Assert.Equal(3, workerPool.GetProperty("maxConcurrency").GetInt32());
            Assert.Equal("KoLite:WorkerPool:MaxConcurrency", workerPool.GetProperty("maxConcurrencySource").GetString());
            Assert.Equal(TimeSpan.FromMilliseconds(123).ToString(), workerPool.GetProperty("idleDelay").GetString());
            Assert.Equal("KoLite:WorkerPool:IdleDelay", workerPool.GetProperty("idleDelaySource").GetString());
            Assert.Equal(4, workerPool.GetProperty("maxDispatchStartsPerCycle").GetInt32());
            Assert.Equal("KoLite:WorkerPool:MaxDispatchStartsPerCycle", workerPool.GetProperty("maxDispatchStartsPerCycleSource").GetString());
            Assert.Equal(0, workerPool.GetProperty("activeWorkerCount").GetInt32());
            Assert.Equal(3, workerPool.GetProperty("availableSlots").GetInt32());
            Assert.Equal(1, workerPool.GetProperty("claimableBacklog").GetInt32());
            Assert.Equal(1, workerPool.GetProperty("activeQueueRows").GetInt32());
            Assert.False(workerPool.GetProperty("isIdle").GetBoolean());
        }

        [Fact]
        public async Task Shutdown_status_reports_running_mode_in_health_and_status_endpoint()
        {
            using var runFactory = CreateFactory(enableScheduler: false);
            using var client = runFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var health = await client.GetStringAsync("/status/health");
            using var healthJson = JsonDocument.Parse(health);
            var shutdown = healthJson.RootElement.GetProperty("shutdown");
            Assert.Equal("Running", shutdown.GetProperty("mode").GetString());
            Assert.Equal(0, shutdown.GetProperty("activeWorkerCount").GetInt32());

            var status = await client.GetStringAsync("/status/shutdown");
            using var statusJson = JsonDocument.Parse(status);
            Assert.Equal("Running", statusJson.RootElement.GetProperty("mode").GetString());
        }

        [Fact]
        public async Task Drain_endpoint_is_idempotent_and_returns_shutdown_status()
        {
            using var runFactory = CreateFactory(enableScheduler: false);
            using var client = runFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            using var first = await client.PostAsync("/status/shutdown/drain?reason=web-test", content: null);
            first.EnsureSuccessStatusCode();
            var firstBody = await first.Content.ReadAsStringAsync();
            using var firstJson = JsonDocument.Parse(firstBody);

            Assert.True(firstJson.RootElement.GetProperty("isDrainRequested").GetBoolean());
            Assert.Equal("web-test", firstJson.RootElement.GetProperty("reason").GetString());
            Assert.Contains(firstJson.RootElement.GetProperty("mode").GetString(), new[] { "DrainRequested", "Drained", "Stopping" });
        }

        [Fact]
        public async Task Dashboard_history_and_slice_routes_render_seeded_sqlite_data()
        {
            SeedOperationalData();
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var dashboard = await client.GetStringAsync("/");
            var history = await client.GetStringAsync("/jobs/job.web/history?from=2026-01-01T00%3A00%3A00Z&to=2026-01-01T00%3A15%3A00Z");
            var boundaryHistory = await client.GetStringAsync("/jobs/job.web/history?from=2026-01-01T00%3A05&to=2026-01-01T00%3A15");
            var wideHistory = await client.GetStringAsync("/jobs/job.web/history?from=2025-12-31T00%3A00&to=2026-01-02T00%3A00");
            var hourlyHistory = await client.GetStringAsync("/jobs/job.hourly/history?from=2026-01-01T00%3A00%3A00Z&to=2026-01-03T00%3A00%3A00Z");
            var multiHourHistory = await client.GetStringAsync("/jobs/job.multihour/history?from=2026-01-01T00%3A00%3A00Z&to=2026-01-03T00%3A00%3A00Z");
            var pausedHistory = await client.GetStringAsync("/jobs/job.paused/history?from=2026-01-01T00%3A00%3A00Z&to=2026-01-01T00%3A15%3A00Z");
            var slice = await client.GetStringAsync("/jobs/job.web/slices?start=2026-01-01T00%3A05%3A00Z&end=2026-01-01T00%3A10%3A00Z");
            var script = await client.GetStringAsync("/js/site.js");
            var css = await client.GetStringAsync("/css/site.css");

            Assert.Contains("KO Lite Local Dashboard", dashboard);
            Assert.Contains("job.web", dashboard);
            Assert.True(dashboard.IndexOf("Active jobs", StringComparison.Ordinal) < dashboard.IndexOf("Success Statistics", StringComparison.Ordinal));
            Assert.Contains("Next eligible", dashboard);
            Assert.Contains("Eligible now", dashboard);
            Assert.Contains(">Healthy</span>", dashboard);
            Assert.Contains("Success Rate By Function", dashboard);
            Assert.Contains("Success Rate After Retries by function", dashboard);
            Assert.Contains("src=\"/lib/chartjs/chart.umd.min.js\"", dashboard);
            Assert.Contains("class=\"ko-table job-table job-table-dashboard\"", dashboard);
            Assert.DoesNotContain("class=\"success-chart-svg\"", dashboard);
            Assert.DoesNotContain("class=\"success-chart-marker\"", dashboard);
            Assert.DoesNotContain("View point details", dashboard);
            Assert.DoesNotContain("success-chart-data", dashboard);
            Assert.DoesNotContain("Dashboard charts", dashboard);
            Assert.DoesNotContain("Function / Output", dashboard);
            Assert.DoesNotContain("<th>Updated</th>", dashboard);
            Assert.DoesNotContain("<th>Progress</th>", dashboard);
            Assert.DoesNotContain("/jobs/job.web/history\">History</a>", dashboard);
            Assert.DoesNotContain("Web Folder", dashboard);
            Assert.DoesNotContain(" running,", dashboard);
            Assert.DoesNotContain("KO.Web-style local dashboard backed by SQLite.", dashboard);
            Assert.DoesNotContain("Background scheduler:", dashboard);
            Assert.Contains("Slice History: job.web", history);
            Assert.Contains("class=\"cell completed\"", history);
            Assert.Contains("class=\"cell completed-after-retry\"", history);
            Assert.Contains("class=\"cell failed\"", history);
            Assert.Contains("class=\"cell queued\"", history);
            Assert.Contains("class=\"cell running\"", history);
            Assert.Contains("class=\"cell blocked\"", history);
            Assert.Contains("class=\"cell missing\"", history);
            Assert.Contains("aria-label=\"2026-01-01T00:10:00Z to 2026-01-01T00:15:00Z; Queued; attempt 0\"", history);
            Assert.Contains("aria-label=\"2026-01-01T00:15:00Z to 2026-01-01T00:20:00Z; Running; attempt 1\"", history);
            Assert.Contains("aria-label=\"2026-01-01T00:20:00Z to 2026-01-01T00:25:00Z; Missing; attempt 0\"", history);
            Assert.Contains("aria-label=\"2026-01-01T00:30:00Z to 2026-01-01T00:35:00Z; Completed after retry; attempt 2\"", history);
            Assert.Contains("aria-label=\"2026-01-01T00:35:00Z to 2026-01-01T00:40:00Z; Dependency blocked; attempt 0\"", history);
            Assert.Contains("data-slice-start=\"2026-01-01T00:10:00Z\"", history);
            Assert.Contains("data-slice-end=\"2026-01-01T00:15:00Z\"", history);
            Assert.Contains("data-slice-status=\"Queued\"", history);
            Assert.Contains("data-slice-tooltip-title=\"Slice\"", history);
            Assert.Contains("title=\"Start: 2026-01-01T00:10:00Z", history);
            Assert.Contains("Queue attempts: 0/3", history);
            Assert.Contains("data-tooltip-label=\"Start\" data-tooltip-value=\"2026-01-01T00:10:00Z\"", history);
            Assert.Contains("data-tooltip-label=\"Status\" data-tooltip-value=\"Queued\"", history);
            Assert.Contains("data-tooltip-label=\"Queue\" data-tooltip-value=\"Queued\"", history);
            Assert.Contains("data-tooltip-label=\"Available\" data-tooltip-value=\"2026-01-01T00:00:00Z\"", history);
            Assert.Contains("data-tooltip-label=\"Queue attempts\" data-tooltip-value=\"0/3\"", history);
            Assert.Contains("data-tooltip-label=\"Status\" data-tooltip-value=\"Completed after retry\"", history);
            Assert.Contains("data-tooltip-label=\"Status\" data-tooltip-value=\"Dependency blocked\"", history);
            Assert.Contains("Completed after retry", history);
            Assert.Contains("Dependency blocked", history);
            Assert.Contains("data-slice-start=\"2026-01-01T00:55:00Z\"", history);
            Assert.Equal(12, Regex.Matches(history, "class=\"cell ").Count);
            Assert.Contains("class=\"cell paused\"", pausedHistory);
            Assert.Contains("aria-label=\"2026-01-01T00:00:00Z to 2026-01-01T00:05:00Z; Paused; attempt 0\"", pausedHistory);
            Assert.Contains("data-slice-status=\"Paused\"", pausedHistory);
            Assert.Contains("data-tooltip-label=\"Status\" data-tooltip-value=\"Paused\"", pausedHistory);
            Assert.Contains("type=\"datetime-local\" name=\"from\" value=\"2026-01-01T00:05\"", boundaryHistory);
            Assert.Contains("type=\"datetime-local\" name=\"to\" value=\"2026-01-01T00:15\"", boundaryHistory);
            Assert.Contains("containing row boundary", boundaryHistory);
            Assert.DoesNotContain("containing hour row boundary", boundaryHistory);
            Assert.Contains("aria-label=\"2026-01-01T00:00:00Z to 2026-01-01T00:05:00Z; Completed; attempt 1\"", boundaryHistory);
            Assert.Equal(12, Regex.Matches(boundaryHistory, "class=\"cell ").Count);
            Assert.Contains("2025-12-31 00:00", wideHistory);
            Assert.Contains("Showing 2025-12-31T00:00:00Z to 2026-01-02T00:00:00Z", wideHistory);
            Assert.DoesNotContain("Selected range is too large", wideHistory);
            Assert.Contains("Slice History: job.hourly", hourlyHistory);
            Assert.Contains("<span class=\"slice-hour\">2026-01-01</span>", hourlyHistory);
            Assert.Contains("<span class=\"slice-hour\">2026-01-02</span>", hourlyHistory);
            Assert.DoesNotContain("<span class=\"slice-hour\">2026-01-01 00:00</span>", hourlyHistory);
            Assert.Equal(48, Regex.Matches(hourlyHistory, "class=\"cell ").Count);
            Assert.Contains("Slice History: job.multihour", multiHourHistory);
            Assert.Contains("<span class=\"slice-hour\">2026-01-01</span>", multiHourHistory);
            Assert.Contains("<span class=\"slice-hour\">2026-01-02</span>", multiHourHistory);
            Assert.Equal(8, Regex.Matches(multiHourHistory, "class=\"cell ").Count);
            Assert.Contains("Slice detail: job.web", slice);
            Assert.Contains("boom", slice);
            Assert.Contains("default", slice);
            Assert.Contains("data-slice-tooltip-line", script);
            Assert.Contains("data-slice-tooltip-title", script);
            Assert.Contains("BuildSuccessRateChart", script);
            Assert.Contains("toggleSuccessRateSeries", script);
            Assert.Contains(".slice-history-tooltip", css);
            Assert.Contains("--slice-stat-paused: #6e7781;", css);
            Assert.Contains("--slice-stat-blocked: #afb8c1;", css);
            Assert.Contains(".completed-after-retry", css);
            Assert.Contains(".blocked { background-color: var(--slice-stat-blocked); }", css);
            Assert.Contains(".tooltip-row", css);
            Assert.Contains(".chart-grid", css);
            Assert.Contains(".job-table-dashboard", css);
            Assert.Contains(".paused-job-indicator", css);
            Assert.Contains(".catalog-diff-table", css);
        }

        [Fact]
        public void Dashboard_next_eligible_future_times_are_utc()
        {
            var clock = new ManualClock(At(1));
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("job.future", "FutureFunction", isPaused: false));
            var query = new DashboardPageQuery(
                catalog,
                new SqliteOperationalReadModelRepository(sqlite),
                new LifecycleReadModel(sqlite),
                new JobChartQuery(sqlite, clock),
                sqlite,
                clock);

            var data = query.Get(TimeSpan.FromDays(1));
            var job = Assert.Single(data.ActiveJobs);

            Assert.Equal("2026-01-01T00:05:00Z", job.NextSlice.Text);
            Assert.Equal("Next window 2026-01-01T00:00:00Z to 2026-01-01T00:05:00Z.", job.NextSlice.Detail);
        }

        [Fact]
        public async Task Dashboard_and_catalog_constrain_long_job_names()
        {
            const string longJobId = "CopilotUsage.GhcpReportingUserLanguageToolUsageAndModelToolUsageExtraLongIdentifierForLayout";
            const string longFunctionName = "GhcpReportingUserLanguageToolUsageAndModelToolUsageFunctionNameThatKeepsGoing";
            const string longOutputTable = "CopilotUsageGhcpReportingUserLanguageToolUsageAndModelToolUsageOutputTableThatKeepsGoing";
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var state = new SqliteSliceStateRepository(sqlite);
            var readModels = new SqliteOperationalReadModelRepository(sqlite);
            var earlierSliceStart = DateTimeOffset.UtcNow.AddMinutes(-70);
            var earlierSliceEnd = DateTimeOffset.UtcNow.AddMinutes(-65);
            var sliceStart = DateTimeOffset.UtcNow.AddMinutes(-10);
            var sliceEnd = DateTimeOffset.UtcNow.AddMinutes(-5);
            catalog.Create(Schedule(longJobId, longFunctionName, isPaused: false, outputTable: longOutputTable));
            state.Append("long-layout-complete-earlier", longJobId, earlierSliceStart, earlierSliceEnd, DurableSliceStatus.Completed, expectedVersion: 0);
            state.Append("long-layout-complete", longJobId, sliceStart, sliceEnd, DurableSliceStatus.Completed, expectedVersion: 0);
            readModels.RecordAttempt("long-layout-attempt-earlier", longJobId, earlierSliceStart, earlierSliceEnd, 1, "Succeeded", "worker", earlierSliceStart, earlierSliceEnd);
            readModels.RecordAttempt("long-layout-attempt-latest", longJobId, sliceStart, sliceEnd, 1, "Succeeded", "worker", sliceStart, sliceEnd);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var dashboard = await client.GetStringAsync("/");
            var catalogPage = await client.GetStringAsync("/catalog");
            var css = await client.GetStringAsync("/css/site.css");

            Assert.Contains($"\"name\":\"{longJobId}\"", dashboard);
            Assert.Contains("data-chartjs-success=\"success-rate-by-function-chart\"", dashboard);
            Assert.Contains("job-table-dashboard", dashboard);
            Assert.Contains($"class=\"strong text-truncate\" title=\"{longJobId}\"", dashboard);
            Assert.DoesNotContain($"title=\"{longFunctionName}\"", dashboard);
            Assert.Contains($"title=\"{longOutputTable}\"", catalogPage);
            Assert.Contains("class=\"action-group\"", dashboard);
            Assert.Contains("Inactive jobs", dashboard);
            Assert.Contains("class=\"card disclosure-card inactive-jobs\"", dashboard);
            Assert.DoesNotContain("status-dot", dashboard);
            Assert.DoesNotContain($"/catalog/{Uri.EscapeDataString(longJobId)}/soft-delete", dashboard);
            Assert.Contains($"/catalog/{Uri.EscapeDataString(longJobId)}/soft-delete", catalogPage);
            Assert.Contains("grid-template-columns: minmax(0, 240px) minmax(120px, 1fr) max-content;", css);
            Assert.Contains(".success-chart-canvas-wrap", css);
            Assert.Contains(".success-chart-canvas", css);
            Assert.DoesNotContain(".success-chart-svg", css);
            Assert.DoesNotContain(".success-chart-row", css);
            Assert.Contains("table-layout: fixed;", css);
            Assert.Contains("text-overflow: ellipsis;", css);
            Assert.Contains("flex-wrap: wrap;", css);
            Assert.Contains(".disclosure-card > summary::before", css);
            Assert.Contains("list-style: none;", css);
            Assert.DoesNotContain(".status-dot", css);
        }

        [Fact]
        public async Task Catalog_gets_render_and_post_create_update_enable_disable_in_local_sqlite()
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var createToken = await ReadFormToken(client, "/catalog/new");
            var create = await PostForm(client, "/catalog/create", createToken, new Dictionary<string, string>
            {
                ["scheduleJson"] = Schedule("job.catalog", "CatalogFunction", isPaused: false)
            });
            Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
            Assert.Equal("/jobs/job.catalog", create.Headers.Location?.OriginalString);

            var catalog = new SqliteJobCatalogRepository(sqlite);
            Assert.True(catalog.Get("job.catalog")?.IsEnabled);

            var editToken = await ReadFormToken(client, "/catalog/job.catalog/edit");
            var update = await PostForm(client, "/catalog/job.catalog/update", editToken, new Dictionary<string, string>
            {
                ["expectedVersion"] = "1",
                ["scheduleJson"] = Schedule("job.catalog", "CatalogFunctionV2", isPaused: false)
            });
            Assert.Equal(HttpStatusCode.Redirect, update.StatusCode);
            Assert.Equal("CatalogFunctionV2", catalog.Get("job.catalog")?.QueryRef);

            var disableToken = await ReadFormToken(client, "/catalog/job.catalog/edit");
            var disable = await PostForm(client, "/catalog/job.catalog/disable", disableToken, new Dictionary<string, string>
            {
                ["expectedVersion"] = "2"
            });
            Assert.Equal(HttpStatusCode.Redirect, disable.StatusCode);
            Assert.False(catalog.Get("job.catalog")?.IsEnabled);
            var pausedDetails = await client.GetStringAsync("/jobs/job.catalog");
            Assert.Contains("class=\"paused-job-indicator\"", pausedDetails);
            Assert.Contains("Scheduling is paused. New slices and queued retries will not run until this job is resumed.", pausedDetails);

            var enableToken = await ReadFormToken(client, "/catalog/job.catalog/edit");
            var enable = await PostForm(client, "/catalog/job.catalog/enable", enableToken, new Dictionary<string, string>
            {
                ["expectedVersion"] = "3"
            });
            Assert.Equal(HttpStatusCode.Redirect, enable.StatusCode);
            Assert.True(catalog.Get("job.catalog")?.IsEnabled);

            var page = await client.GetStringAsync("/catalog");
            Assert.Contains("Job Catalog", page);
            Assert.Contains("job.catalog", page);
            Assert.Contains("<th>Progress</th>", page);
            Assert.Contains("/jobs/job.catalog/history\">History</a>", page);

            var details = await client.GetStringAsync("/jobs/job.catalog");
            Assert.Contains("Schedule fields", details);
            Assert.Contains("Raw JSON", details);
            Assert.Contains("Slice history", details);
            Assert.Contains("Job definition", details);
            Assert.Contains("Change history", details);
            Assert.Contains("Catalog definition history", details);
            Assert.Contains("catalog-diff-table", details);
            Assert.Contains("$.functionName", details);
            Assert.Contains("CatalogFunctionV2", details);
            Assert.Contains("No schedule JSON changes.", details);
            Assert.Contains("Initial schedule definition.", details);
            Assert.DoesNotContain("class=\"paused-job-indicator\"", details);
            Assert.DoesNotContain("<span class=\"stat-label\">Catalog version</span>", details);
            Assert.DoesNotContain("<span class=\"stat-label\">Enabled</span>", details);
            Assert.DoesNotContain("<span class=\"stat-label\">Completed</span>", details);
            Assert.DoesNotContain("<span class=\"stat-label\">Failed/blocked</span>", details);
            Assert.DoesNotContain("Job state history", details);

            var softDeleteToken = await ReadFormToken(client, "/jobs/job.catalog");
            var softDelete = await PostForm(client, "/catalog/job.catalog/soft-delete", softDeleteToken, new Dictionary<string, string>
            {
                ["expectedVersion"] = "4",
                ["reason"] = "test soft delete"
            });
            Assert.Equal(HttpStatusCode.Redirect, softDelete.StatusCode);
            Assert.False(catalog.Get("job.catalog")?.IsEnabled);
            var softDeletedDetails = await client.GetStringAsync("/jobs/job.catalog");
            Assert.DoesNotContain("class=\"paused-job-indicator\"", softDeletedDetails);

            var softDeletedPage = await client.GetStringAsync("/");
            Assert.Contains("Inactive jobs", softDeletedPage);
            Assert.Contains("Soft-deleted jobs", softDeletedPage);
            Assert.Contains("job.catalog", softDeletedPage);

            var restoreToken = await ReadFormToken(client, "/");
            var restore = await PostForm(client, "/catalog/job.catalog/restore", restoreToken, new Dictionary<string, string>
            {
                ["expectedVersion"] = "5",
                ["reason"] = "test restore"
            });
            Assert.Equal(HttpStatusCode.Redirect, restore.StatusCode);
            Assert.True(catalog.Get("job.catalog")?.IsEnabled);

            var softDeleteAgainToken = await ReadFormToken(client, "/jobs/job.catalog");
            var softDeleteAgain = await PostForm(client, "/catalog/job.catalog/soft-delete", softDeleteAgainToken, new Dictionary<string, string>
            {
                ["expectedVersion"] = "6",
                ["reason"] = "test hard-delete precondition"
            });
            Assert.Equal(HttpStatusCode.Redirect, softDeleteAgain.StatusCode);

            var hardDeleteToken = await ReadFormToken(client, "/catalog/job.catalog/hard-delete");
            var blockedHardDelete = await PostForm(client, "/catalog/job.catalog/hard-delete", hardDeleteToken, new Dictionary<string, string>
            {
                ["confirmation"] = "DELETE wrong.job",
                ["reason"] = "bad confirmation"
            });
            Assert.Equal(HttpStatusCode.BadRequest, blockedHardDelete.StatusCode);
            Assert.NotNull(catalog.Get("job.catalog"));

            hardDeleteToken = await ReadFormToken(client, "/catalog/job.catalog/hard-delete");
            var hardDelete = await PostForm(client, "/catalog/job.catalog/hard-delete", hardDeleteToken, new Dictionary<string, string>
            {
                ["confirmation"] = "DELETE job.catalog",
                ["reason"] = "test hard delete"
            });
            var hardDeleteBody = await hardDelete.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, hardDelete.StatusCode);
            Assert.Contains("Hard delete completed", hardDeleteBody);
            Assert.Null(catalog.Get("job.catalog"));
        }

        [Fact]
        public async Task Started_job_edit_page_marks_protected_fields_readonly_and_rejects_raw_json_tampering()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("job.readonly", "ReadOnlyFunction", isPaused: false));
            var state = new SqliteSliceStateRepository(sqlite);
            state.Append("readonly-started", "job.readonly", At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var edit = await client.GetStringAsync("/catalog/job.readonly/edit");

            Assert.Matches("name=\"Input\\.ActivityId\"[^>]*readonly", edit);
            Assert.Matches("name=\"Input\\.QueryWindowSize\"[^>]*readonly", edit);
            Assert.Matches("name=\"Input\\.StartFrom\"[^>]*readonly", edit);
            Assert.DoesNotContain("These fields are read-only because this job has execution history", edit);
            Assert.DoesNotContain("Raw JSON changes to read-only fields are rejected server-side", edit);

            var token = await ReadFormToken(client, "/catalog/job.readonly/edit");
            var tampered = await PostForm(client, "/catalog/job.readonly/update", token, new Dictionary<string, string>
            {
                ["expectedVersion"] = "1",
                ["scheduleJson"] = Schedule("job.readonly", "ReadOnlyFunction", isPaused: false)
                    .Replace("\"queryWindowSize\": \"00:05:00\"", "\"queryWindowSize\": \"00:10:00\"", StringComparison.Ordinal)
            });
            var body = await tampered.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);
            Assert.Contains("queryWindowSize", body);
            var stored = catalog.Get("job.readonly")!;
            Assert.Equal(1, stored.CatalogVersion);
            Assert.Equal(TimeSpan.FromMinutes(5), stored.Definition.QueryWindowSize);
        }

        [Fact]
        public async Task Import_page_accepts_array_payload_and_file_upload_without_deleting_omitted_jobs()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("job.existing", "ExistingFunction", isPaused: false));
            catalog.Create(Schedule("job.omitted", "OmittedFunction", isPaused: false));
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var importPage = await client.GetStringAsync("/catalog/import");
            Assert.DoesNotContain("Started jobs keep their activityId, queryWindowSize, and startFrom values", importPage);

            var pasteToken = await ReadFormToken(client, "/catalog/import");
            var pasted = await PostForm(client, "/catalog/import", pasteToken, new Dictionary<string, string>
            {
                ["importSource"] = "paste",
                ["scheduleJson"] = "[" + Schedule("job.existing", "ExistingFunctionV2", isPaused: true) + "," + Schedule("job.new", "NewFunction", isPaused: false) + "]"
            });
            var pastedBody = await pasted.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, pasted.StatusCode);
            Assert.Contains("Imported 2 jobs: 1 created, 1 updated. No jobs were deleted.", pastedBody);
            Assert.Contains("0 deleted", pastedBody);
            Assert.Equal("ExistingFunctionV2", catalog.Get("job.existing")?.QueryRef);
            Assert.False(catalog.Get("job.existing")?.IsEnabled);
            Assert.NotNull(catalog.Get("job.new"));
            Assert.Equal("OmittedFunction", catalog.Get("job.omitted")?.QueryRef);

            var fileToken = await ReadFormToken(client, "/catalog/import");
            var fileImport = await PostMultipart(client, "/catalog/import", fileToken, "[" + Schedule("job.file-a", "FileFunctionA", isPaused: false) + "," + Schedule("job.file-b", "FileFunctionB", isPaused: false) + "]", "jobs.json");
            var fileBody = await fileImport.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, fileImport.StatusCode);
            Assert.Contains("job.file-a", fileBody);
            Assert.Contains("job.file-b", fileBody);
            Assert.NotNull(catalog.Get("job.file-a"));
            Assert.NotNull(catalog.Get("job.file-b"));
            Assert.NotNull(catalog.Get("job.omitted"));
        }

        [Fact]
        public async Task Dashboard_links_export_all_and_route_exports_non_soft_deleted_jobs()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("job.active", "ActiveFunction", isPaused: false));
            catalog.Create(Schedule("job.disabled", "DisabledFunction", isPaused: true));
            var softDeleted = catalog.Create(Schedule("job.soft", "SoftDeletedFunction", isPaused: false));
            new SqliteJobLifecycleService(sqlite, catalog).SoftDelete("job.soft", softDeleted.CatalogVersion, "web-test", "exclude from export all");
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var dashboard = await client.GetStringAsync("/");
            using var response = await client.GetAsync("/catalog/export");
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var ids = document.RootElement.EnumerateArray()
                .Select(item => item.GetProperty("activityId").GetString() ?? string.Empty)
                .ToArray();

            Assert.Contains("href=\"/catalog/export\">Export all</a>", dashboard);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(["job.active", "job.disabled"], ids);
        }

        [Fact]
        public async Task Import_page_rejects_invalid_array_without_persisting_valid_items()
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var token = await ReadFormToken(client, "/catalog/import");

            var response = await PostForm(client, "/catalog/import", token, new Dictionary<string, string>
            {
                ["importSource"] = "paste",
                ["scheduleJson"] = "[" + Schedule("job.valid", "ValidFunction", isPaused: false) + "," + Schedule("job.bad", "BadFunction", isPaused: false).Replace("\"outputTable\": \"Output\",", "\"unknownField\": true,", StringComparison.Ordinal) + "]"
            });
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("[1].unknownField", body);
            Assert.Contains("[1].outputTable", body);
            Assert.Contains("<form method=\"post\" action=\"/catalog/import\"", body);
            Assert.Null(new SqliteJobCatalogRepository(sqlite).Get("job.valid"));
            Assert.Null(new SqliteJobCatalogRepository(sqlite).Get("job.bad"));
        }

        [Fact]
        public async Task Rerun_pages_preview_create_and_execute_slice_reset()
        {
            SeedOperationalData();
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var slice = await client.GetStringAsync("/jobs/job.web/slices?start=2026-01-01T00%3A00%3A00Z&end=2026-01-01T00%3A05%3A00Z");
            var previewPath = "/jobs/job.web/rerun?start=2026-01-01T00%3A00&end=2026-01-01T00%3A05&reason=web%20rerun";
            var preview = await client.GetStringAsync(previewPath);
            var createToken = await ReadFormToken(client, previewPath);

            Assert.Contains("Rerun this slice", slice);
            Assert.Contains("Plan rerun: job.web", preview);
            Assert.Contains(".delete table Output records &lt;|", preview);
            Assert.Contains("StartTime &lt; datetime(2026-01-01T00:05:00.0000000Z)", preview);
            Assert.Contains("Create rerun batch", preview);

            var create = await PostForm(client, "/jobs/job.web/rerun", createToken, new Dictionary<string, string>
            {
                ["start"] = "2026-01-01T00:00",
                ["end"] = "2026-01-01T00:05",
                ["requestedBy"] = "web-test",
                ["reason"] = "web rerun"
            });
            Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
            var rerunPath = create.Headers.Location?.OriginalString ?? string.Empty;
            Assert.StartsWith("/reruns/", rerunPath, StringComparison.Ordinal);

            var batch = await client.GetStringAsync(rerunPath);
            Assert.Contains("Suggested Kusto cleanup", batch);
            Assert.Contains("I acknowledge that Kusto cleanup has been handled", batch);

            var executeToken = await ReadFormToken(client, rerunPath);
            var execute = await PostForm(client, rerunPath + "/execute", executeToken, new Dictionary<string, string>
            {
                ["kustoCleanupAcknowledged"] = "true"
            });
            Assert.Equal(HttpStatusCode.Redirect, execute.StatusCode);

            var state = new SqliteSliceStateRepository(sqlite);
            Assert.Equal(DurableSliceStatus.Missing, state.Get("job.web", At(0), At(5)).Status);

            var completedBatch = await client.GetStringAsync(rerunPath);
            var resetSlice = await client.GetStringAsync("/jobs/job.web/slices?start=2026-01-01T00%3A00%3A00Z&end=2026-01-01T00%3A05%3A00Z");
            Assert.Contains("Archived previous local details", completedBatch);
            Assert.Contains("attempt-s0", completedBatch);
            Assert.Contains("No attempts recorded.", resetSlice);
        }

        [Fact]
        public async Task Background_scheduler_executes_enabled_jobs_with_registered_executor()
        {
            new SqliteJobCatalogRepository(sqlite).Create(Schedule("job.runner", "RunnerFunction", isPaused: false));
            using var runFactory = CreateFactory(
                enableScheduler: true,
                tickInterval: "00:00:00.050",
                services => services.AddScoped<ILocalSliceOutputExecutor, TestSliceOutputExecutor>());
            using var client = runFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var dashboard = await client.GetStringAsync("/");
            Assert.DoesNotContain("Background scheduler:", dashboard);
            Assert.DoesNotContain("executor: <strong>live Kusto SDK</strong>", dashboard);

            var state = new SqliteSliceStateRepository(sqlite);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline && state.Get("job.runner", At(0), At(5))?.Status != DurableSliceStatus.Completed)
            {
                await Task.Delay(50);
            }

            Assert.Equal(DurableSliceStatus.Completed, state.Get("job.runner", At(0), At(5)).Status);

            var history = await client.GetStringAsync("/jobs/job.runner/history?from=2026-01-01T00%3A00%3A00Z&to=2026-01-01T00%3A05%3A00Z");
            Assert.Contains("class=\"cell completed\"", history);
        }

        [Fact]
        public async Task Background_scheduler_logs_each_pass_when_diagnostic_enabled()
        {
            using var runFactory = CreateFactory(
                enableScheduler: true,
                tickInterval: "00:00:00.050",
                logEveryPass: true);
            using var client = runFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var health = await client.GetStringAsync("/status/health");
            using var healthJson = JsonDocument.Parse(health);
            Assert.True(healthJson.RootElement.GetProperty("scheduler").GetProperty("logEveryPass").GetBoolean());

            var snapshot = (Count: 0, LatestPropertiesJson: (string?)null);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                snapshot = SchedulerPassLogSnapshot();
                if (snapshot.Count >= 2)
                {
                    break;
                }

                await Task.Delay(50);
            }

            Assert.True(snapshot.Count >= 2, $"Expected at least two scheduler-pass logs, found {snapshot.Count}.");
            Assert.False(string.IsNullOrWhiteSpace(snapshot.LatestPropertiesJson));
            using var properties = JsonDocument.Parse(snapshot.LatestPropertiesJson!);
            Assert.Equal(TimeSpan.FromMilliseconds(50).ToString(), properties.RootElement.GetProperty("configuredTickInterval").GetString());
            Assert.Equal(0, properties.RootElement.GetProperty("enqueued").GetInt32());
            Assert.True(properties.RootElement.GetProperty("workerDispatchDecoupled").GetBoolean());
            Assert.True(properties.RootElement.GetProperty("durationMs").GetDouble() >= 0);
        }

        [Fact]
        public async Task Background_worker_logs_idle_dispatch_cycles_when_diagnostic_enabled()
        {
            using var runFactory = CreateFactory(
                enableScheduler: true,
                tickInterval: "00:00:00.050",
                logEveryPass: true,
                workerPoolIdleDelay: "00:00:00.050");
            using var client = runFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            await client.GetStringAsync("/");
            var snapshot = (Count: 0, LatestPropertiesJson: (string?)null);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                snapshot = OperationalLogSnapshot("worker-dispatch");
                if (snapshot.Count >= 1)
                {
                    break;
                }

                await Task.Delay(50);
            }

            Assert.True(snapshot.Count >= 1, $"Expected at least one worker-dispatch log, found {snapshot.Count}.");
            Assert.False(string.IsNullOrWhiteSpace(snapshot.LatestPropertiesJson));
            using var properties = JsonDocument.Parse(snapshot.LatestPropertiesJson!);
            Assert.Equal(0, properties.RootElement.GetProperty("started").GetInt32());
            Assert.Equal(0, properties.RootElement.GetProperty("claimableBeforeDispatch").GetInt32());
            Assert.True(properties.RootElement.GetProperty("isIdle").GetBoolean());
            Assert.False(properties.RootElement.GetProperty("isSaturated").GetBoolean());
            Assert.Equal(TimeSpan.FromMilliseconds(50).ToString(), properties.RootElement.GetProperty("idleDelay").GetString());
        }

        [Fact]
        public async Task Background_scheduler_keeps_scheduling_ready_slices_while_unrelated_slice_runs()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("job.fast.refill", "FastFunction", isPaused: false, maxParallelism: 1));
            catalog.Create(Schedule("job.slow.blocking", "SlowFunction", isPaused: false, maxParallelism: 1));
            var executor = new SelectiveBlockingSliceOutputExecutor("job.slow.blocking");
            using var runFactory = CreateFactory(
                enableScheduler: true,
                tickInterval: "00:00:00.050",
                workerConcurrency: 2,
                configureServices: services =>
                {
                    services.AddSingleton(executor);
                    services.AddScoped<ILocalSliceOutputExecutor>(sp => sp.GetRequiredService<SelectiveBlockingSliceOutputExecutor>());
                });
            using var client = runFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var state = new SqliteSliceStateRepository(sqlite);

            try
            {
                await client.GetStringAsync("/");
                await executor.WaitForBlockedStartsAsync(1, TimeSpan.FromSeconds(5));
                await WaitUntilAsync(
                    () => state.Get("job.fast.refill", At(5), At(10)).Status == DurableSliceStatus.Completed,
                    TimeSpan.FromSeconds(5));

                Assert.Equal(DurableSliceStatus.Running, state.Get("job.slow.blocking", At(0), At(5)).Status);
                Assert.Equal(DurableSliceStatus.Completed, state.Get("job.fast.refill", At(0), At(5)).Status);
                Assert.Equal(DurableSliceStatus.Completed, state.Get("job.fast.refill", At(5), At(10)).Status);
                Assert.True(executor.FastSucceededCount >= 2);
            }
            finally
            {
                executor.ReleaseBlocked();
            }
        }

        [Fact]
        public async Task Background_scheduler_starts_slices_concurrently_up_to_job_parallelism()
        {
            new SqliteJobCatalogRepository(sqlite).Create(Schedule("job.parallel", "ParallelFunction", isPaused: false, maxParallelism: 2));
            var executor = new BlockingSliceOutputExecutor(expectedStarts: 2);
            using var runFactory = CreateFactory(
                enableScheduler: true,
                tickInterval: "00:00:00.050",
                services =>
                {
                    services.AddSingleton(executor);
                    services.AddScoped<ILocalSliceOutputExecutor>(sp => sp.GetRequiredService<BlockingSliceOutputExecutor>());
                });
            using var client = runFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            await client.GetStringAsync("/");
            await executor.WaitForStartsAsync(TimeSpan.FromSeconds(5));

            var state = new SqliteSliceStateRepository(sqlite);
            Assert.Equal(DurableSliceStatus.Running, state.Get("job.parallel", At(0), At(5)).Status);
            Assert.Equal(DurableSliceStatus.Running, state.Get("job.parallel", At(5), At(10)).Status);

            var runningHistory = await client.GetStringAsync("/jobs/job.parallel/history?from=2026-01-01T00%3A00%3A00Z&to=2026-01-01T00%3A10%3A00Z");
            Assert.Equal(2, Regex.Matches(runningHistory, "class=\"cell running\"").Count);

            executor.Release();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline &&
                   (state.Get("job.parallel", At(0), At(5)).Status != DurableSliceStatus.Completed ||
                    state.Get("job.parallel", At(5), At(10)).Status != DurableSliceStatus.Completed))
            {
                await Task.Delay(50);
            }

            Assert.Equal(DurableSliceStatus.Completed, state.Get("job.parallel", At(0), At(5)).Status);
            Assert.Equal(DurableSliceStatus.Completed, state.Get("job.parallel", At(5), At(10)).Status);
        }

        [Fact]
        public async Task Background_worker_pool_applies_global_cap_across_jobs_and_uses_unique_worker_ids()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("job.global.one", "GlobalOneFunction", isPaused: false, maxParallelism: 2));
            catalog.Create(Schedule("job.global.two", "GlobalTwoFunction", isPaused: false, maxParallelism: 2));
            var executor = new BlockingSliceOutputExecutor(expectedStarts: 2);
            using var runFactory = CreateFactory(
                enableScheduler: true,
                tickInterval: "00:00:00.050",
                workerPoolMaxConcurrency: 2,
                configureServices: services =>
                {
                    services.AddSingleton(executor);
                    services.AddScoped<ILocalSliceOutputExecutor>(sp => sp.GetRequiredService<BlockingSliceOutputExecutor>());
                });
            using var client = runFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            await client.GetStringAsync("/");
            await executor.WaitForStartsAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(250);

            var state = new SqliteSliceStateRepository(sqlite);
            var runningCount = new[]
            {
                state.Get("job.global.one", At(0), At(5)).Status,
                state.Get("job.global.one", At(5), At(10)).Status,
                state.Get("job.global.two", At(0), At(5)).Status,
                state.Get("job.global.two", At(5), At(10)).Status
            }.Count(status => status == DurableSliceStatus.Running);
            Assert.Equal(2, executor.StartedCount);
            Assert.Equal(2, runningCount);

            var workerIds = StartedWorkerIds();
            Assert.Equal(2, workerIds.Count);
            Assert.Equal(2, workerIds.Distinct(StringComparer.Ordinal).Count());
            Assert.All(workerIds, workerId => Assert.StartsWith("local-web-worker-", workerId, StringComparison.Ordinal));

            var health = await client.GetStringAsync("/status/health");
            using var healthJson = JsonDocument.Parse(health);
            var workerPool = healthJson.RootElement.GetProperty("workerPool");
            Assert.Equal(2, workerPool.GetProperty("maxConcurrency").GetInt32());
            Assert.Equal("KoLite:WorkerPool:MaxConcurrency", workerPool.GetProperty("maxConcurrencySource").GetString());
            Assert.Equal(2, workerPool.GetProperty("activeWorkerCount").GetInt32());
            Assert.Equal(0, workerPool.GetProperty("availableSlots").GetInt32());

            executor.Release();
            await WaitUntilAsync(
                () => new[]
                {
                    state.Get("job.global.one", At(0), At(5)).Status,
                    state.Get("job.global.one", At(5), At(10)).Status,
                    state.Get("job.global.two", At(0), At(5)).Status,
                    state.Get("job.global.two", At(5), At(10)).Status
                }.All(status => status != DurableSliceStatus.Running),
                TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task Drain_shutdown_allows_active_worker_to_finish_without_claiming_more_work()
        {
            new SqliteJobCatalogRepository(sqlite).Create(Schedule("job.drain", "DrainFunction", isPaused: false, maxParallelism: 2));
            var executor = new BlockingSliceOutputExecutor(expectedStarts: 1);
            using var runFactory = CreateFactory(
                enableScheduler: true,
                tickInterval: "00:00:00.050",
                workerConcurrency: 1,
                configureServices: services =>
                {
                    services.AddSingleton(executor);
                    services.AddScoped<ILocalSliceOutputExecutor>(sp => sp.GetRequiredService<BlockingSliceOutputExecutor>());
                });
            using var client = runFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var state = new SqliteSliceStateRepository(sqlite);

            await client.GetStringAsync("/");
            await executor.WaitForStartsAsync(TimeSpan.FromSeconds(5));
            using var drain = await client.PostAsync("/status/shutdown/drain?reason=active-worker-test", content: null);
            drain.EnsureSuccessStatusCode();

            await Task.Delay(250);
            Assert.Equal(1, executor.StartedCount);
            Assert.False(executor.CancellationWasRequestedBeforeRelease);

            executor.Release();
            await WaitUntilAsync(
                () => state.Get("job.drain", At(0), At(5)).Status == DurableSliceStatus.Completed,
                TimeSpan.FromSeconds(5));

            Assert.Equal(1, executor.StartedCount);
            Assert.False(executor.CancellationWasRequestedBeforeRelease);
            Assert.NotEqual(DurableSliceStatus.Completed, state.Get("job.drain", At(5), At(10)).Status);
        }

        [Fact]
        public async Task Drain_shutdown_does_not_claim_retryable_work_after_active_failure()
        {
            new SqliteJobCatalogRepository(sqlite).Create(Schedule("job.drain.retry", "DrainRetryFunction", isPaused: false, maxParallelism: 1));
            var executor = new BlockingRetryableFailureExecutor();
            using var runFactory = CreateFactory(
                enableScheduler: true,
                tickInterval: "00:00:00.050",
                workerConcurrency: 1,
                configureServices: services =>
                {
                    services.AddSingleton(executor);
                    services.AddScoped<ILocalSliceOutputExecutor>(sp => sp.GetRequiredService<BlockingRetryableFailureExecutor>());
                    services.AddSingleton(new LocalWorkerOptions(WorkerId: "drain-retry-test-worker", InitialRetryDelay: TimeSpan.Zero));
                });
            using var client = runFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var queue = new SqliteWorkQueueRepository(sqlite);
            var state = new SqliteSliceStateRepository(sqlite);

            await client.GetStringAsync("/");
            await executor.WaitForStartAsync(TimeSpan.FromSeconds(5));
            using var drain = await client.PostAsync("/status/shutdown/drain?reason=retry-test", content: null);
            drain.EnsureSuccessStatusCode();

            executor.Release();
            await WaitUntilAsync(
                () => state.Get("job.drain.retry", At(0), At(5)).Status == DurableSliceStatus.Failed,
                TimeSpan.FromSeconds(5));
            await Task.Delay(250);

            Assert.Equal(1, executor.StartedCount);
            var item = Assert.Single(queue.List("job.drain.retry"));
            Assert.Equal(DurableWorkQueueState.Queued, item.State);
            Assert.Equal(1, item.Attempts);
        }

        [Fact]
        public async Task Background_scheduler_does_not_start_paused_queued_work()
        {
            new SqliteJobCatalogRepository(sqlite).Create(Schedule("job.concurrent.paused", "PausedFunction", isPaused: true));
            var state = new SqliteSliceStateRepository(sqlite);
            state.Append("paused-concurrent-queued", "job.concurrent.paused", At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
            var queue = new SqliteWorkQueueRepository(sqlite);
            var item = queue.Enqueue("job.concurrent.paused", At(0), At(5), "paused-concurrent-work", At(0));
            var executor = new CountingSliceOutputExecutor();
            using var runFactory = CreateFactory(
                enableScheduler: true,
                tickInterval: "00:00:00.050",
                services =>
                {
                    services.AddSingleton(executor);
                    services.AddScoped<ILocalSliceOutputExecutor>(sp => sp.GetRequiredService<CountingSliceOutputExecutor>());
                });
            using var client = runFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            await client.GetStringAsync("/");
            await Task.Delay(200);

            Assert.Equal(0, executor.StartedCount);
            var queued = queue.Get(item.QueueItemId)!;
            Assert.Equal(DurableWorkQueueState.Queued, queued.State);
            Assert.Equal(0, queued.Attempts);
        }

        [Fact]
        public async Task Invalid_schedule_json_and_unknown_fields_are_rejected_with_useful_response()
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var token = await ReadFormToken(client, "/catalog/new");

            var response = await PostForm(client, "/catalog/create", token, new Dictionary<string, string>
            {
                ["scheduleJson"] = Schedule("job.bad", "BadFunction", isPaused: false).Replace("\"outputTable\": \"Output\"", "\"unknownField\": true,\n  \"outputTable\": \"Output\"", StringComparison.Ordinal)
            });
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("Schedule JSON is invalid", body);
            Assert.Contains("unknownField", body);
            Assert.Contains("<form method=\"post\" action=\"/catalog/create\">", body);
            Assert.Contains("<textarea name=\"scheduleJson\"", body);
            Assert.Null(new SqliteJobCatalogRepository(sqlite).Get("job.bad"));
        }

        [Fact]
        public async Task Malformed_schedule_json_is_rejected_with_html_form_response()
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var token = await ReadFormToken(client, "/catalog/new");

            var response = await PostForm(client, "/catalog/create", token, new Dictionary<string, string>
            {
                ["scheduleJson"] = "{ bad json"
            });
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("Schedule JSON is invalid", body);
            Assert.Contains("<form method=\"post\" action=\"/catalog/create\">", body);
            Assert.Contains("<textarea name=\"scheduleJson\"", body);
        }

        [Fact]
        public async Task Catalog_mutations_require_post_and_csrf_token()
        {
            new SqliteJobCatalogRepository(sqlite).Create(Schedule("job.safe", "SafeFunction", isPaused: false));
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/catalog/create")).StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/catalog/job.safe/update")).StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/catalog/job.safe/enable")).StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/catalog/job.safe/disable")).StatusCode);

            var noToken = await client.PostAsync("/catalog/job.safe/disable", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["expectedVersion"] = "1"
            }));
            Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
            Assert.True(new SqliteJobCatalogRepository(sqlite).Get("job.safe")?.IsEnabled);
        }

        public void Dispose()
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
        }

        private (int Count, string? LatestPropertiesJson) SchedulerPassLogSnapshot()
        {
            return OperationalLogSnapshot("scheduler-pass");
        }

        private (int Count, string? LatestPropertiesJson) OperationalLogSnapshot(string category)
        {
            using var connection = sqlite.OpenConnection();
            using var countCommand = connection.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(*) FROM operational_logs WHERE category=$category;";
            countCommand.Parameters.AddWithValue("$category", category);
            var count = Convert.ToInt32(countCommand.ExecuteScalar());
            using var latestCommand = connection.CreateCommand();
            latestCommand.CommandText = "SELECT properties_json FROM operational_logs WHERE category=$category ORDER BY recorded_at_utc DESC, log_id DESC LIMIT 1;";
            latestCommand.Parameters.AddWithValue("$category", category);
            return (count, latestCommand.ExecuteScalar() as string);
        }

        private IReadOnlyList<string> StartedWorkerIds()
        {
            using var connection = sqlite.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT DISTINCT worker_id
                FROM slice_attempts
                WHERE status='Started' AND worker_id IS NOT NULL
                ORDER BY worker_id;
                """;
            using var reader = command.ExecuteReader();
            var workerIds = new List<string>();
            while (reader.Read())
            {
                workerIds.Add(reader.GetString(0));
            }

            return workerIds;
        }

        private void SeedOperationalData()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var state = new SqliteSliceStateRepository(sqlite);
            var queue = new SqliteWorkQueueRepository(sqlite);
            var readModels = new SqliteOperationalReadModelRepository(sqlite);

            catalog.Create(Schedule("job.web", "WebFunction", isPaused: false, folder: "Web Folder"));
            state.Append("s0", "job.web", At(0), At(5), DurableSliceStatus.Completed, expectedVersion: 0);
            state.Append("s1", "job.web", At(5), At(10), DurableSliceStatus.Failed, expectedVersion: 0, reason: "boom");
            state.Append("s2", "job.web", At(10), At(15), DurableSliceStatus.Queued, expectedVersion: 0);
            state.AcquireLease("s3", "job.web", At(15), At(20), "worker", TimeSpan.FromMinutes(5), At(16));
            state.Append("s5", "job.web", At(25), At(30), DurableSliceStatus.Failed, expectedVersion: 0, reason: "terminal boom");
            state.Append("s6", "job.web", At(30), At(35), DurableSliceStatus.Completed, expectedVersion: 0);
            state.Append("s7", "job.web", At(35), At(40), DurableSliceStatus.DependencyBlocked, expectedVersion: 0, reason: "upstream");
            queue.Enqueue("job.web", At(5), At(10), "queue-s1", At(0));
            queue.Enqueue("job.web", At(10), At(15), "queue-s2", At(0));
            readModels.RecordAttempt("attempt-s0", "job.web", At(0), At(5), 1, "Succeeded", "worker", At(0), At(1));
            readModels.RecordAttempt("attempt-s6-1", "job.web", At(30), At(35), 1, "FailedRetryable", "worker", At(30), At(31));
            readModels.RecordAttempt("attempt-s6-2", "job.web", At(30), At(35), 2, "Succeeded", "worker", At(32), At(33));

            catalog.Create(Schedule("job.hourly", "HourlyFunction", isPaused: false, queryWindowSize: "01:00:00"));
            state.Append("hourly-s0", "job.hourly", At(0), At(60), DurableSliceStatus.Completed, expectedVersion: 0);
            state.Append("hourly-s1", "job.hourly", At(24 * 60), At(25 * 60), DurableSliceStatus.Completed, expectedVersion: 0);

            catalog.Create(Schedule("job.multihour", "MultiHourFunction", isPaused: false, queryWindowSize: "06:00:00"));
            state.Append("multihour-s0", "job.multihour", At(0), At(6 * 60), DurableSliceStatus.Completed, expectedVersion: 0);
            state.Append("multihour-s1", "job.multihour", At(24 * 60), At(30 * 60), DurableSliceStatus.Failed, expectedVersion: 0, reason: "multi-hour boom");

            catalog.Create(Schedule("job.paused", "PausedFunction", isPaused: true));
            state.Append("paused-s0", "job.paused", At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
            queue.Enqueue("job.paused", At(0), At(5), "paused-queue-s0", At(0));
        }

        private async Task<FormToken> ReadFormToken(HttpClient client, string path)
        {
            using var response = await client.GetAsync(path);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync();
            var token = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"(?<token>[^\"]+)\"", RegexOptions.CultureInvariant).Groups["token"].Value;
            Assert.False(string.IsNullOrWhiteSpace(token));
            var cookie = response.Headers.TryGetValues("Set-Cookie", out var cookies)
                ? string.Join("; ", cookies.Select(v => v.Split(';', 2)[0]))
                : string.Empty;
            return new FormToken(token, cookie);
        }

        private static async Task<HttpResponseMessage> PostForm(HttpClient client, string path, FormToken token, Dictionary<string, string> values)
        {
            values["__RequestVerificationToken"] = token.Value;
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new FormUrlEncodedContent(values)
            };
            if (!string.IsNullOrEmpty(token.Cookie)) request.Headers.Add("Cookie", token.Cookie);
            return await client.SendAsync(request);
        }

        private static async Task<HttpResponseMessage> PostMultipart(HttpClient client, string path, FormToken token, string fileText, string fileName)
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(token.Value), "__RequestVerificationToken");
            content.Add(new StringContent("file"), "importSource");
            content.Add(new StringContent(fileText), "scheduleFile", fileName);
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = content
            };
            if (!string.IsNullOrEmpty(token.Cookie)) request.Headers.Add("Cookie", token.Cookie);
            return await client.SendAsync(request);
        }

        private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        private WebApplicationFactory<Program> CreateFactory(
            bool enableScheduler,
            string? tickInterval = null,
            Action<IServiceCollection>? configureServices = null,
            bool? logEveryPass = null,
            int? workerConcurrency = null,
            int? maxWorkerIterations = null,
            int? workerPoolMaxConcurrency = null,
            string? workerPoolIdleDelay = null,
            int? workerPoolMaxDispatchStartsPerCycle = null) =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    var values = new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:KoLiteSqlite"] = databasePath,
                        ["KoLite:Scheduler:Enabled"] = enableScheduler.ToString()
                    };
                    if (tickInterval is not null) values["KoLite:Scheduler:TickInterval"] = tickInterval;
                    if (logEveryPass is not null) values["KoLite:Scheduler:LogEveryPass"] = logEveryPass.Value.ToString();
                    if (workerConcurrency is not null) values["KoLite:Scheduler:WorkerConcurrency"] = workerConcurrency.Value.ToString();
                    if (maxWorkerIterations is not null) values["KoLite:Scheduler:MaxWorkerIterations"] = maxWorkerIterations.Value.ToString();
                    if (workerPoolMaxConcurrency is not null) values["KoLite:WorkerPool:MaxConcurrency"] = workerPoolMaxConcurrency.Value.ToString();
                    if (workerPoolIdleDelay is not null) values["KoLite:WorkerPool:IdleDelay"] = workerPoolIdleDelay;
                    if (workerPoolMaxDispatchStartsPerCycle is not null) values["KoLite:WorkerPool:MaxDispatchStartsPerCycle"] = workerPoolMaxDispatchStartsPerCycle.Value.ToString();
                    config.AddInMemoryCollection(values);
                });
                if (configureServices is not null)
                {
                    builder.ConfigureServices(configureServices);
                }
            });

        private static string Schedule(string activityId, string functionName, bool isPaused, string outputTable = "Output", int maxParallelism = 1, string queryWindowSize = "00:05:00", string? folder = null)
        {
            var schedule = $$"""
            {
              "activityId": "{{activityId}}",
              "functionName": "{{functionName}}",
              "outputTable": "{{outputTable}}",
              "queryWindowSize": "{{queryWindowSize}}",
              "delayFromUtcNow": "00:00:00",
              "maxParallelism": {{maxParallelism}},
              "queryTimeout": "00:01:00",
              "isPaused": {{isPaused.ToString().ToLowerInvariant()}},
              "startFrom": "2026-01-01T00:00:00Z",
              "target": { "clusterUri": "https://kolite-example.invalid", "database": "DemoDb" }
            }
            """;

            return folder is null
                ? schedule
                : schedule.Replace("  \"target\":", $"  \"folder\": \"{folder}\",\n  \"target\":", StringComparison.Ordinal);
        }

        private sealed class TestSliceOutputExecutor : ILocalSliceOutputExecutor
        {
            public Task<LocalSliceOutputResult> ExecuteAsync(JobDefinition job, SliceRange slice, CancellationToken cancellationToken = default) =>
                Task.FromResult(LocalSliceOutputResult.Success($"test://{slice.ToKey().Value}"));
        }

        private sealed class BlockingSliceOutputExecutor : ILocalSliceOutputExecutor
        {
            private readonly int expectedStarts;

            public BlockingSliceOutputExecutor(int expectedStarts)
            {
                this.expectedStarts = expectedStarts;
            }

            private readonly TaskCompletionSource allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int started;
            private int cancellationWasRequestedBeforeRelease;

            public int StartedCount => Volatile.Read(ref started);
            public bool CancellationWasRequestedBeforeRelease => Volatile.Read(ref cancellationWasRequestedBeforeRelease) == 1;

            public async Task<LocalSliceOutputResult> ExecuteAsync(JobDefinition job, SliceRange slice, CancellationToken cancellationToken = default)
            {
                if (Interlocked.Increment(ref started) >= expectedStarts)
                {
                    allStarted.TrySetResult();
                }

                using var registration = cancellationToken.Register(() => Interlocked.Exchange(ref cancellationWasRequestedBeforeRelease, 1));
                if (cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Exchange(ref cancellationWasRequestedBeforeRelease, 1);
                }

                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return LocalSliceOutputResult.Success($"test://{slice.ToKey().Value}");
            }

            public async Task WaitForStartsAsync(TimeSpan timeout)
            {
                await allStarted.Task.WaitAsync(timeout).ConfigureAwait(false);
            }

            public void Release() => release.TrySetResult();
        }

        private sealed class BlockingRetryableFailureExecutor : ILocalSliceOutputExecutor
        {
            private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int startedCount;

            public int StartedCount => Volatile.Read(ref startedCount);

            public async Task<LocalSliceOutputResult> ExecuteAsync(JobDefinition job, SliceRange slice, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref startedCount);
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return LocalSliceOutputResult.Failure("Transient", "retry later", isRetryable: true);
            }

            public Task WaitForStartAsync(TimeSpan timeout) => started.Task.WaitAsync(timeout);
            public void Release() => release.TrySetResult();
        }

        private sealed class SelectiveBlockingSliceOutputExecutor : ILocalSliceOutputExecutor
        {
            private readonly string blockedActivityId;

            public SelectiveBlockingSliceOutputExecutor(string blockedActivityId)
            {
                this.blockedActivityId = blockedActivityId;
            }

            private readonly TaskCompletionSource blockedStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource releaseBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int blockedStartCount;
            private int fastSucceededCount;

            public int FastSucceededCount => Volatile.Read(ref fastSucceededCount);

            public async Task<LocalSliceOutputResult> ExecuteAsync(JobDefinition job, SliceRange slice, CancellationToken cancellationToken = default)
            {
                if (StringComparer.Ordinal.Equals(job.ActivityId, blockedActivityId))
                {
                    if (Interlocked.Increment(ref blockedStartCount) >= 1)
                    {
                        blockedStarted.TrySetResult();
                    }

                    await releaseBlocked.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    return LocalSliceOutputResult.Success($"test://{slice.ToKey().Value}");
                }

                Interlocked.Increment(ref fastSucceededCount);
                return LocalSliceOutputResult.Success($"test://{slice.ToKey().Value}");
            }

            public async Task WaitForBlockedStartsAsync(int expectedStarts, TimeSpan timeout)
            {
                if (Volatile.Read(ref blockedStartCount) >= expectedStarts)
                {
                    return;
                }

                await blockedStarted.Task.WaitAsync(timeout).ConfigureAwait(false);
            }

            public void ReleaseBlocked() => releaseBlocked.TrySetResult();
        }

        private sealed class CountingSliceOutputExecutor : ILocalSliceOutputExecutor
        {
            private int started;
            public int StartedCount => started;

            public Task<LocalSliceOutputResult> ExecuteAsync(JobDefinition job, SliceRange slice, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref started);
                return Task.FromResult(LocalSliceOutputResult.Success($"test://{slice.ToKey().Value}"));
            }
        }

        private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (predicate())
                {
                    return;
                }

                await Task.Delay(50);
            }

            Assert.True(predicate(), "Condition was not met before the timeout.");
        }

        private sealed record FormToken(string Value, string Cookie);
    }
}
