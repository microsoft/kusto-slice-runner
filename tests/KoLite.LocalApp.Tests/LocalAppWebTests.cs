using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using KoLite.Local.Core.Orchestration;
using KoLite.Local.Core.Schedules;
using KoLite.Local.Core.Scheduling;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Lifecycle;
using KoLite.Local.Sqlite.Schema;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Orchestration;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.Repair;
using KoLite.Local.Sqlite.State;
using KoLite.Local.Sqlite.Throttling;
using KoLite.LocalApp.Retention;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

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
            new KoLiteSqliteSchema(sqlite).EnsureSchema();
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
            Assert.Equal(LocalBackgroundWorkerPoolOptions.Unbounded, workerPool.MaxConcurrency);
            Assert.True(workerPool.MaxConcurrencyUnbounded);
            Assert.Equal("Unbounded", workerPool.MaxConcurrencyDisplay);
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
        public void Worker_pool_options_accept_a_configured_global_cap_above_the_chunk_limit()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KoLite:WorkerPool:MaxConcurrency"] = "64"
                })
                .Build();

            var schedulerOptions = LocalBackgroundSchedulerOptions.From(configuration);
            var options = LocalBackgroundWorkerPoolOptions.From(configuration, schedulerOptions);

            Assert.Equal(64, options.MaxConcurrency);
            Assert.False(options.MaxConcurrencyUnbounded);
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

            var health = await client.GetStringAsync("/api/v1/system/status");
            using var healthJson = JsonDocument.Parse(health);
            var scheduler = healthJson.RootElement.GetProperty("scheduler");

            Assert.True(scheduler.GetProperty("logEveryPass").GetBoolean());
        }

        [Fact]
        public async Task Health_reports_retention_configuration_and_last_run()
        {
            var snapshot = new RetentionSnapshot(
                Enabled: true,
                LastRunUtc: DateTimeOffset.Parse("2026-06-20T00:00:00Z"),
                LogsDeleted: 5,
                AttemptsDeleted: 4,
                ScheduledSlicesDeleted: 3,
                IngestionThrottlesDeleted: 2,
                QueueRowsDeleted: 1,
                TotalDeleted: 15,
                LastError: null);
            using var runFactory = CreateFactory(enableScheduler: false, configureServices: services =>
            {
                services.RemoveAll<RetentionRuntimeState>();
                services.AddSingleton(new RetentionRuntimeState(snapshot));
            });
            using var client = runFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var health = await client.GetStringAsync("/api/v1/system/status");
            using var healthJson = JsonDocument.Parse(health);
            var retention = healthJson.RootElement.GetProperty("retention");

            Assert.True(retention.GetProperty("enabled").GetBoolean());
            Assert.Equal(30, retention.GetProperty("windowDays").GetDouble());
            Assert.Equal(15, retention.GetProperty("lastRunDeleted").GetInt32());
            Assert.Equal(1, retention.GetProperty("queueRowsDeleted").GetInt32());
            Assert.Equal(DateTimeOffset.Parse("2026-06-20T00:00:00Z"), retention.GetProperty("lastRunUtc").GetDateTimeOffset());
        }

        [Fact]
        public async Task Health_reports_worker_pool_snapshot_and_effective_configuration_sources()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("job.health.pool", "HealthFunction", isPaused: false));
            var state = new SqliteSliceStateRepository(sqlite);
            state.Append("health-pool-queued", JobId("job.health.pool"), At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
            var queue = new SqliteWorkQueueRepository(sqlite);
            queue.Enqueue(JobId("job.health.pool"), At(0), At(5), "health-pool-work", At(0));
            using var runFactory = CreateFactory(
                enableScheduler: false,
                workerPoolMaxConcurrency: 3,
                workerPoolIdleDelay: "00:00:00.123",
                workerPoolMaxDispatchStartsPerCycle: 4);
            using var client = runFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var health = await client.GetStringAsync("/api/v1/system/status");
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

            var health = await client.GetStringAsync("/api/v1/system/status");
            using var healthJson = JsonDocument.Parse(health);
            var shutdown = healthJson.RootElement.GetProperty("shutdown");
            Assert.Equal("Running", shutdown.GetProperty("mode").GetString());
            Assert.Equal(0, shutdown.GetProperty("activeWorkerCount").GetInt32());

            var status = await client.GetStringAsync("/control/v1/shutdown");
            using var statusJson = JsonDocument.Parse(status);
            Assert.Equal("Running", statusJson.RootElement.GetProperty("mode").GetString());
        }

        [Fact]
        public async Task Drain_endpoint_is_idempotent_and_returns_shutdown_status()
        {
            using var runFactory = CreateFactory(enableScheduler: false);
            using var client = runFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            using var first = await client.PostAsJsonAsync("/control/v1/shutdown/drain", new { reason = "web-test" });
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
            var now = DateTimeOffset.UtcNow;
            var currentHour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
            var detailChartSliceStart = currentHour.AddMinutes(-10);
            var detailChartSliceEnd = currentHour.AddMinutes(-5);
            var detailChartState = new SqliteSliceStateRepository(sqlite);
            var detailChartReadModels = new SqliteOperationalReadModelRepository(sqlite);
            detailChartState.Append("detail-chart-slice", JobId("job.web"), detailChartSliceStart, detailChartSliceEnd, DurableSliceStatus.Completed, expectedVersion: 0);
            detailChartReadModels.RecordAttempt("detail-chart-attempt", JobId("job.web"), detailChartSliceStart, detailChartSliceEnd, 1, "Started", "worker", detailChartSliceStart, null);
            detailChartReadModels.RecordAttempt("detail-chart-attempt", JobId("job.web"), detailChartSliceStart, detailChartSliceEnd, 1, "Succeeded", "worker", null, detailChartSliceEnd);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var dashboard = await client.GetStringAsync("/");
            var details = await client.GetStringAsync($"/jobs/{JobId("job.web")}?range=1d");
            var history = await client.GetStringAsync($"/jobs/{JobId("job.web")}/history?from=2026-01-01T00%3A00%3A00Z&to=2026-01-01T00%3A15%3A00Z");
            var boundaryHistory = await client.GetStringAsync($"/jobs/{JobId("job.web")}/history?from=2026-01-01T00%3A05&to=2026-01-01T00%3A15");
            var wideHistory = await client.GetStringAsync($"/jobs/{JobId("job.web")}/history?from=2025-12-31T00%3A00&to=2026-01-02T00%3A00");
            var hourlyHistory = await client.GetStringAsync($"/jobs/{JobId("job.hourly")}/history?from=2026-01-01T00%3A00%3A00Z&to=2026-01-03T00%3A00%3A00Z");
            var multiHourHistory = await client.GetStringAsync($"/jobs/{JobId("job.multihour")}/history?from=2026-01-01T00%3A00%3A00Z&to=2026-01-03T00%3A00%3A00Z");
            var pausedHistory = await client.GetStringAsync($"/jobs/{JobId("job.paused")}/history?from=2026-01-01T00%3A00%3A00Z&to=2026-01-01T00%3A15%3A00Z");
            var slice = await client.GetStringAsync($"/jobs/{JobId("job.web")}/slices?start=2026-01-01T00%3A05%3A00Z&end=2026-01-01T00%3A10%3A00Z");
            var script = await client.GetStringAsync("/js/site.js");
            var css = await client.GetStringAsync("/css/site.css");

            Assert.Contains("KO Lite Local Dashboard", dashboard);
            Assert.Contains("job.web", dashboard);
            Assert.True(dashboard.IndexOf("Active jobs", StringComparison.Ordinal) < dashboard.IndexOf("Success Statistics", StringComparison.Ordinal));
            Assert.Contains("Next eligible", dashboard);
            Assert.Contains("Eligible now", dashboard);
            Assert.Contains("status-seg status-seg-health status-healthy", dashboard);
            Assert.Contains("aria-label=\"Healthy", dashboard);
            Assert.Contains("Execution Attempt Success Rate by Function", dashboard);
            Assert.Contains("Logical Slice Success Rate After Retries by Function", dashboard);
            Assert.Contains("the current incomplete bucket is omitted", dashboard);
            Assert.Contains("src=\"/lib/chartjs/chart.umd.min.js?v=", dashboard);
            Assert.Contains("class=\"ko-table job-table job-table-dashboard\"", dashboard);
            Assert.Contains("data-dashboard-filter-input=\"true\"", dashboard);
            Assert.Contains("data-dashboard-job-table=\"true\"", dashboard);
            Assert.Contains($"data-dashboard-job-id=\"{JobId("job.web")}\"", dashboard);
            Assert.Contains("data-dashboard-search=\"job.web ", dashboard);
            Assert.Contains("data-dashboard-resizable=\"true\"", dashboard);
            Assert.Contains("class=\"column-resize-handle\"", dashboard);
            Assert.Contains("<symbol id=\"icon-edit\"", dashboard);
            Assert.Contains("class=\"btn small icon-only\" title=\"Edit\" aria-label=\"Edit\"", dashboard);
            Assert.Contains("<use href=\"#icon-edit\">", dashboard);
            Assert.Contains("<span class=\"visually-hidden\">Edit</span>", dashboard);
            Assert.Contains("aria-label=\"Copy\"", dashboard);
            Assert.DoesNotContain($"/jobs/{JobId("job.web")}/export", dashboard);
            Assert.DoesNotContain("aria-label=\"Export\"", dashboard);
            Assert.Contains(">New job</span>", dashboard);
            Assert.DoesNotContain("class=\"success-chart-svg\"", dashboard);
            Assert.DoesNotContain("class=\"success-chart-marker\"", dashboard);
            Assert.DoesNotContain("View point details", dashboard);
            Assert.DoesNotContain("success-chart-data", dashboard);
            Assert.DoesNotContain("Dashboard charts", dashboard);
            Assert.DoesNotContain("Function / Output", dashboard);
            Assert.DoesNotContain("<th>Updated</th>", dashboard);
            Assert.DoesNotContain("<th>Progress</th>", dashboard);
            Assert.DoesNotContain($"/jobs/{JobId("job.web")}/history", dashboard);
            Assert.DoesNotContain("Web Folder", dashboard);
            Assert.DoesNotContain(" running,", dashboard);
            Assert.DoesNotContain("KO.Web-style local dashboard backed by SQLite.", dashboard);
            Assert.DoesNotContain("Background scheduler:", dashboard);
            Assert.Contains("role=\"tablist\" aria-label=\"Job details sections\"", details);
            Assert.Contains("href=\"#slice-history\" role=\"tab\" aria-controls=\"slice-history\"", details);
            Assert.Contains("href=\"#definition\" role=\"tab\" aria-controls=\"definition\"", details);
            Assert.Contains("href=\"#operations\" role=\"tab\" aria-controls=\"operations\"", details);
            Assert.Contains("href=\"#change-history\" role=\"tab\" aria-controls=\"change-history\"", details);
            Assert.Contains("id=\"slice-history\" class=\"tab-panel active\" role=\"tabpanel\"", details);
            Assert.Contains("Open full history", details);
            Assert.Contains($"/jobs/{JobId("job.web")}/history", details);
            Assert.Contains("Query Results by Time of Execution", details);
            Assert.Contains("Successful Query Duration by Time of Execution", details);
            Assert.Contains("LeaseLost is shown in the Error bucket", details);
            Assert.Contains("data-chartjs-job=\"job-attempt-result-chart\"", details);
            Assert.Contains("data-chartjs-job=\"job-successful-duration-chart\"", details);
            Assert.Contains("\"kind\":\"result-counts\"", details);
            Assert.Contains("\"kind\":\"duration\"", details);
            Assert.Contains("Averages include 1 successful execution(s)", details);
            Assert.Contains("no successful executions were missing duration data", details);
            Assert.Contains("\"missingCount\":0", details);
            Assert.Contains($"/jobs/{JobId("job.web")}?range=7d#slice-history", details);
            Assert.Contains("Slice History: job.web", history);
            Assert.Contains("class=\"cell completed\"", history);
            Assert.Contains("class=\"cell completed-after-retry\"", history);
            Assert.Contains("class=\"cell failed\"", history);
            Assert.Contains("class=\"cell queued\"", history);
            Assert.Contains("class=\"cell running\"", history);
            Assert.Contains("class=\"cell waiting\"", history);
            Assert.Contains("class=\"cell waiting-scheduled\"", history);
            Assert.Contains("aria-label=\"2026-01-01T00:10:00Z to 2026-01-01T00:15:00Z; Queued; attempt 0\"", history);
            Assert.Contains("aria-label=\"2026-01-01T00:15:00Z to 2026-01-01T00:20:00Z; Running; attempt 1\"", history);
            Assert.Contains("aria-label=\"2026-01-01T00:20:00Z to 2026-01-01T00:25:00Z; Waiting to be scheduled; attempt 0\"", history);
            Assert.Contains("aria-label=\"2026-01-01T00:30:00Z to 2026-01-01T00:35:00Z; Completed after retry; attempt 2\"", history);
            Assert.Contains("aria-label=\"2026-01-01T00:35:00Z to 2026-01-01T00:40:00Z; Waiting on dependency; attempt 0\"", history);
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
            Assert.Contains("data-tooltip-label=\"Status\" data-tooltip-value=\"Waiting on dependency\"", history);
            Assert.Contains("Completed after retry", history);
            Assert.Contains("Waiting on dependency", history);
            Assert.Contains("data-slice-start=\"2026-01-01T00:55:00Z\"", history);
            Assert.Equal(12, Regex.Matches(history, "class=\"cell ").Count);
            Assert.Contains("class=\"cell queued\"", pausedHistory);
            Assert.Contains("class=\"cell waiting-scheduled\"", pausedHistory);
            Assert.DoesNotContain("class=\"cell paused\"", pausedHistory);
            Assert.Contains("aria-label=\"2026-01-01T00:05:00Z to 2026-01-01T00:10:00Z; Waiting to be scheduled; attempt 0\"", pausedHistory);
            Assert.Contains("aria-label=\"2026-01-01T00:00:00Z to 2026-01-01T00:05:00Z; Queued; attempt 0\"", pausedHistory);
            Assert.Contains("data-slice-status=\"Queued\"", pausedHistory);
            Assert.Contains("data-tooltip-label=\"Status\" data-tooltip-value=\"Queued\"", pausedHistory);
            Assert.Contains("class=\"paused-job-indicator\"", pausedHistory);
            Assert.Contains("Scheduling is paused. New slices and queued retries will not run until this job is resumed.", pausedHistory);
            Assert.Contains("type=\"datetime-local\" name=\"from\" value=\"2026-01-01T00:05\"", boundaryHistory);
            Assert.Contains("type=\"datetime-local\" name=\"to\" value=\"2026-01-01T00:15\"", boundaryHistory);
            Assert.Contains("containing row boundary", boundaryHistory);
            Assert.DoesNotContain("containing hour row boundary", boundaryHistory);
            Assert.Contains("aria-label=\"2026-01-01T00:00:00Z to 2026-01-01T00:05:00Z; Completed; attempt 1\"", boundaryHistory);
            Assert.Equal(12, Regex.Matches(boundaryHistory, "class=\"cell ").Count);
            Assert.Contains("2025-12-31 00:00", wideHistory);
            Assert.Contains("class=\"cell missing\"", wideHistory);
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
            Assert.Contains("BuildJobDetailChart", script);
            Assert.Contains("initJobDetailTabs", script);
            Assert.Contains("initDashboardJobFilter", script);
            Assert.Contains("initDashboardColumnResize", script);
            Assert.Contains("applyDashboardChartFilter", script);
            Assert.Contains("dashboardVisibleActiveJobIds", script);
            Assert.Contains("successRateChartEntries", script);
            Assert.Contains("localStorage", script);
            Assert.Contains("dashboardAvailableTableWidth", script);
            Assert.Contains("table.style.width = \"100%\"", script);
            Assert.Contains("table.style.minWidth = \"0\"", script);
            Assert.Contains("data-chartjs-job", script);
            Assert.Contains("toggleSuccessRateSeries", script);
            Assert.Contains(".slice-history-tooltip", css);
            Assert.Contains(".job-tabs", css);
            Assert.Contains("border: 1px solid var(--border);", css);
            Assert.Contains("box-shadow: 0 1px 2px rgba(27, 31, 36, 0.04);", css);
            Assert.Contains(".tab-panel", css);
            Assert.Contains(".tab-list", css);
            Assert.Contains("border-bottom: 1px solid var(--border);", css);
            Assert.Contains(".job-chart-canvas-wrap", css);
            Assert.Contains("--slice-stat-waiting: #afb8c1;", css);
            Assert.Contains(".completed-after-retry", css);
            Assert.Contains(".waiting { background-color: var(--slice-stat-waiting); }", css);
            Assert.Contains(".tooltip-row", css);
            Assert.Contains(".chart-grid", css);
            Assert.Contains(".job-table-dashboard", css);
            Assert.Contains(".paused-job-indicator", css);
            Assert.Contains(".catalog-diff-table", css);
            Assert.Contains(".tag-chip", css);
            Assert.Contains(".jobs-with-tags", css);
            Assert.Contains("align-items: stretch;", css);
            Assert.Contains("margin-bottom: 18px;", css);
            Assert.Contains(".jobs-main > .card:last-child", css);
            Assert.Contains(".tag-filter-pane", css);
            Assert.Contains("flex: 1;", css);
            Assert.Contains(".dashboard-text-filter", css);
            Assert.Contains(".dashboard-filter-input", css);
            Assert.Contains(".column-resize-handle", css);
            Assert.Contains("cursor: col-resize;", css);
            Assert.Contains("right: 0;", css);
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
                new LifecycleReadModel(new SqliteLifecycleReadModelRepository(sqlite)),
                new JobChartQuery(sqlite, clock),
                clock);

            var data = query.Get(TimeSpan.FromDays(1));
            var job = Assert.Single(data.ActiveJobs);

            Assert.Equal("2026-01-01T00:05:00Z", job.NextSlice.Text);
            Assert.Equal("Next window 2026-01-01T00:00:00Z to 2026-01-01T00:05:00Z.", job.NextSlice.Detail);
        }

        [Fact]
        public void Dashboard_keeps_incomplete_finite_jobs_active_after_endOn()
        {
            var clock = new ManualClock(At(120));
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var state = new SqliteSliceStateRepository(sqlite);
            var readModels = new SqliteOperationalReadModelRepository(sqlite);
            catalog.Create(Schedule("job.blocked.finished-window", "BlockedFunction", isPaused: false, endOn: "2026-01-01T00:30:00Z"));
            catalog.Create(Schedule("job.failed.finished-window", "FailedFunction", isPaused: false, endOn: "2026-01-01T00:30:00Z"));
            catalog.Create(Schedule("job.missing.finished-window", "MissingFunction", isPaused: false, endOn: "2026-01-01T00:30:00Z"));
            catalog.Create(Schedule("job.running.finished-window", "RunningFunction", isPaused: false, endOn: "2026-01-01T00:30:00Z"));
            state.Append("blocked-completed", JobId("job.blocked.finished-window"), At(0), At(5), DurableSliceStatus.Completed, expectedVersion: 0);
            state.Append("blocked", JobId("job.blocked.finished-window"), At(25), At(30), DurableSliceStatus.DependencyBlocked, expectedVersion: 0, reason: "upstream");
            state.Append("failed", JobId("job.failed.finished-window"), At(25), At(30), DurableSliceStatus.Failed, expectedVersion: 0, reason: "boom");
            state.Append("missing-completed", JobId("job.missing.finished-window"), At(25), At(30), DurableSliceStatus.Completed, expectedVersion: 0);
            state.AcquireLease("running", JobId("job.running.finished-window"), At(25), At(30), "worker", TimeSpan.FromMinutes(5), At(120));
            var query = new DashboardPageQuery(
                catalog,
                readModels,
                new LifecycleReadModel(new SqliteLifecycleReadModelRepository(sqlite)),
                new JobChartQuery(sqlite, clock),
                clock);

            var data = query.Get(TimeSpan.FromDays(1));

            var blocked = Assert.Single(data.ActiveJobs, job => job.Record.JobId == JobId("job.blocked.finished-window"));
            var failed = Assert.Single(data.ActiveJobs, job => job.Record.JobId == JobId("job.failed.finished-window"));
            var missing = Assert.Single(data.ActiveJobs, job => job.Record.JobId == JobId("job.missing.finished-window"));
            var running = Assert.Single(data.ActiveJobs, job => job.Record.JobId == JobId("job.running.finished-window"));
            Assert.Empty(data.CompletedJobs);
            Assert.Equal(("DependencyBlocked", "Blocked", false), (blocked.PrimaryState, blocked.NextSlice.Text, blocked.IsCompleted));
            Assert.Equal(("Attention", "Attention", false), (failed.PrimaryState, failed.NextSlice.Text, failed.IsCompleted));
            Assert.Equal(("Healthy", "Incomplete", false), (missing.PrimaryState, missing.NextSlice.Text, missing.IsCompleted));
            Assert.Equal(("Healthy", "In progress", false), (running.PrimaryState, running.NextSlice.Text, running.IsCompleted));
            Assert.True(running.InProgress);
        }

        [Fact]
        public void Dashboard_shows_downstream_waiting_on_healthy_upstream_as_calm_green()
        {
            var clock = new ManualClock(At(120));
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var state = new SqliteSliceStateRepository(sqlite);
            catalog.Create(Schedule("up.healthy", "UpstreamFunction", isPaused: false));
            catalog.Create(ScheduleWithDependencies("down.waiting", "DownstreamFunction", "up.healthy"));
            state.Append("down-blocked", JobId("down.waiting"), At(25), At(30), DurableSliceStatus.DependencyBlocked, expectedVersion: 0, reason: "upstream");

            var down = Assert.Single(CreateDashboardQuery(clock).GetAllJobs(), job => job.Record.JobId == JobId("down.waiting"));

            // A downstream blocked only because a healthy upstream is behind is calm, not a warning.
            Assert.Equal(("WaitingOnUpstream", "Waiting on upstream", "badge-success"), (down.PrimaryState, down.StatusText, down.StatusCss));
        }

        [Fact]
        public void Dashboard_shows_downstream_blocked_by_failed_upstream_as_attention_yellow()
        {
            var clock = new ManualClock(At(120));
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var state = new SqliteSliceStateRepository(sqlite);
            catalog.Create(Schedule("up.failed", "UpstreamFunction", isPaused: false));
            catalog.Create(ScheduleWithDependencies("down.blocked", "DownstreamFunction", "up.failed"));
            state.Append("up-failed", JobId("up.failed"), At(25), At(30), DurableSliceStatus.Failed, expectedVersion: 0, reason: "boom");
            state.Append("down-blocked", JobId("down.blocked"), At(25), At(30), DurableSliceStatus.DependencyBlocked, expectedVersion: 0, reason: "upstream");

            var down = Assert.Single(CreateDashboardQuery(clock).GetAllJobs(), job => job.Record.JobId == JobId("down.blocked"));

            // The upstream has genuinely failed, so the downstream stays flagged as blocked.
            Assert.Equal(("DependencyBlocked", "Blocked (upstream)", "badge-warning"), (down.PrimaryState, down.StatusText, down.StatusCss));
        }

        [Fact]
        public void Dashboard_propagates_upstream_failure_transitively_through_a_blocked_chain()
        {
            var clock = new ManualClock(At(120));
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var state = new SqliteSliceStateRepository(sqlite);
            // C(failed) <- B(blocked) <- A(blocked): both B and A are genuinely blocked.
            catalog.Create(Schedule("chain.c", "CFunction", isPaused: false));
            catalog.Create(ScheduleWithDependencies("chain.b", "BFunction", "chain.c"));
            catalog.Create(ScheduleWithDependencies("chain.a", "AFunction", "chain.b"));
            state.Append("c-failed", JobId("chain.c"), At(25), At(30), DurableSliceStatus.Failed, expectedVersion: 0, reason: "boom");
            state.Append("b-blocked", JobId("chain.b"), At(25), At(30), DurableSliceStatus.DependencyBlocked, expectedVersion: 0, reason: "upstream");
            state.Append("a-blocked", JobId("chain.a"), At(25), At(30), DurableSliceStatus.DependencyBlocked, expectedVersion: 0, reason: "upstream");

            var jobs = CreateDashboardQuery(clock).GetAllJobs();
            var b = Assert.Single(jobs, job => job.Record.JobId == JobId("chain.b"));
            var a = Assert.Single(jobs, job => job.Record.JobId == JobId("chain.a"));

            Assert.Equal("DependencyBlocked", b.PrimaryState);
            Assert.Equal("DependencyBlocked", a.PrimaryState);
        }

        [Fact]
        public void Dashboard_shows_running_work_ahead_of_a_dependency_block()
        {
            var clock = new ManualClock(At(120));
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var state = new SqliteSliceStateRepository(sqlite);
            catalog.Create(Schedule("up.healthy2", "UpstreamFunction", isPaused: false));
            catalog.Create(ScheduleWithDependencies("down.running", "DownstreamFunction", "up.healthy2"));
            state.Append("down-blocked", JobId("down.running"), At(25), At(30), DurableSliceStatus.DependencyBlocked, expectedVersion: 0, reason: "upstream");
            state.AcquireLease("down-running", JobId("down.running"), At(30), At(35), "worker", TimeSpan.FromMinutes(5), At(120));

            var down = Assert.Single(CreateDashboardQuery(clock).GetAllJobs(), job => job.Record.JobId == JobId("down.running"));

            // In-progress work no longer overrides the primary status; a healthy upstream stays calm
            // green and the running/queued state is surfaced via the health-half tooltip.
            Assert.Equal(("WaitingOnUpstream", "badge-success"), (down.PrimaryState, down.StatusCss));
            Assert.True(down.InProgress);
            Assert.Contains("work in progress", down.HealthTooltip, StringComparison.Ordinal);
        }

        private DashboardPageQuery CreateDashboardQuery(IClock clock) => new(
            new SqliteJobCatalogRepository(sqlite),
            new SqliteOperationalReadModelRepository(sqlite),
            new LifecycleReadModel(new SqliteLifecycleReadModelRepository(sqlite)),
            new JobChartQuery(sqlite, clock),
            clock);

        private static string ScheduleWithDependencies(string activityId, string functionName, params string[] upstreamActivityIds)
        {
            var deps = string.Join(", ", upstreamActivityIds.Select(up => $$"""{ "id": "{{JobId(up)}}", "activityId": "{{up}}" }"""));
            return $$"""
            {
              "id": "{{JobId(activityId)}}",
              "activityId": "{{activityId}}",
              "functionName": "{{functionName}}",
              "outputTable": "Output",
              "queryWindowSize": "00:05:00",
              "delayFromUtcNow": "00:00:00",
              "maxParallelism": 1,
              "queryTimeout": "00:01:00",
              "isPaused": false,
              "startFrom": "2026-01-01T00:00:00Z",
              "dependsOn": [ {{deps}} ],
              "target": { "clusterUri": "https://kolite-example.invalid", "database": "DemoDb" }
            }
            """;
        }

        [Fact]
        public void Dashboard_moves_finite_job_to_completed_after_all_slices_complete()
        {
            var clock = new ManualClock(At(120));
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var state = new SqliteSliceStateRepository(sqlite);
            catalog.Create(Schedule("job.complete.finished-window", "CompleteFunction", isPaused: false, endOn: "2026-01-01T00:15:00Z"));
            state.Append("complete-0", JobId("job.complete.finished-window"), At(0), At(5), DurableSliceStatus.Completed, expectedVersion: 0);
            state.Append("complete-1", JobId("job.complete.finished-window"), At(5), At(10), DurableSliceStatus.Completed, expectedVersion: 0);
            state.Append("complete-2", JobId("job.complete.finished-window"), At(10), At(15), DurableSliceStatus.Completed, expectedVersion: 0);
            var query = new DashboardPageQuery(
                catalog,
                new SqliteOperationalReadModelRepository(sqlite),
                new LifecycleReadModel(new SqliteLifecycleReadModelRepository(sqlite)),
                new JobChartQuery(sqlite, clock),
                clock);

            var data = query.Get(TimeSpan.FromDays(1));

            var completedJob = Assert.Single(data.CompletedJobs);
            Assert.Equal(JobId("job.complete.finished-window"), completedJob.Record.JobId);
            Assert.Equal("Completed", completedJob.PrimaryState);
            Assert.True(completedJob.IsCompleted);
            Assert.Empty(data.ActiveJobs);
        }

        [Fact]
        public void Dashboard_scores_recent_health_and_surfaces_old_gaps_per_policy()
        {
            var clock = new ManualClock(At(1000));
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var state = new SqliteSliceStateRepository(sqlite);
            // Two jobs, identical slice history: the oldest two windows dead-lettered (gaps that
            // fall OUTSIDE the recent window), the newest ten completed. Only the health policy differs.
            catalog.Create(Schedule("job.strict", "StrictFunction", isPaused: false, healthPolicy: "complete"));
            catalog.Create(Schedule("job.relaxed", "RelaxedFunction", isPaused: false, healthPolicy: "recent"));
            foreach (var activityId in new[] { "job.strict", "job.relaxed" })
            {
                for (var i = 0; i < 12; i++)
                {
                    var status = i < 2 ? DurableSliceStatus.DeadLettered : DurableSliceStatus.Completed;
                    state.Append($"{activityId}-{i}", JobId(activityId), At(i * 5), At(i * 5 + 5), status, expectedVersion: 0);
                }
            }

            var jobs = CreateDashboardQuery(clock).GetAllJobs();
            var strict = Assert.Single(jobs, job => job.Record.JobId == JobId("job.strict"));
            var relaxed = Assert.Single(jobs, job => job.Record.JobId == JobId("job.relaxed"));

            // Recent window (10 newest) is all completed, so both read Healthy regardless of old gaps.
            Assert.Equal("Healthy", strict.PrimaryState);
            Assert.Equal("Healthy", relaxed.PrimaryState);

            // The strict (complete) job surfaces the two historical dead-letters; the relaxed one hides them.
            Assert.True(strict.ShowCompleteness);
            Assert.Equal(2, strict.GapCount);
            Assert.False(relaxed.ShowCompleteness);
        }

        [Fact]
        public async Task Dashboard_renders_color_only_status_pill_with_gaps()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var state = new SqliteSliceStateRepository(sqlite);
            catalog.Create(Schedule("job.pill", "PillFunction", isPaused: false, healthPolicy: "complete"));
            for (var i = 0; i < 12; i++)
            {
                var status = i < 2 ? DurableSliceStatus.DeadLettered : DurableSliceStatus.Completed;
                state.Append($"pill-{i}", JobId("job.pill"), At(i * 5), At(i * 5 + 5), status, expectedVersion: 0);
            }

            using var client = factory.CreateClient();
            var html = await client.GetStringAsync("/");

            // Color-only pill: a green health half and an amber gaps half, no in-pill text; the gap
            // detail lives in the completeness half's tooltip/aria-label.
            Assert.Contains("class=\"status-pill\"", html);
            Assert.Contains("status-seg status-seg-health status-healthy", html);
            Assert.Contains("status-seg status-seg-completeness status-gaps", html);
            Assert.Contains("2 unaddressed dead-lettered", html);
        }

        [Fact]
        public async Task Job_details_header_drops_pill_and_links_function_and_table_to_adx()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("job.links", "MyFunction", isPaused: false, outputTable: "MyTable"));

            using var client = factory.CreateClient();
            var html = await client.GetStringAsync($"/jobs/{JobId("job.links")}");

            // The header health pill is gone: the wrapping p.job-status-line no longer renders.
            Assert.DoesNotContain("job-status-line", html);

            // Function and output table are now Azure Data Explorer deep links on the job's own cluster/db.
            Assert.Contains("class=\"entity-link\"", html);
            Assert.Contains(AppFormatting.KustoShowFunctionLink("https://kolite-example.invalid", "DemoDb", "MyFunction"), html);
            Assert.Contains(AppFormatting.KustoTablePreviewLink("https://kolite-example.invalid", "DemoDb", "MyTable"), html);
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
            state.Append("long-layout-complete-earlier", JobId(longJobId), earlierSliceStart, earlierSliceEnd, DurableSliceStatus.Completed, expectedVersion: 0);
            state.Append("long-layout-complete", JobId(longJobId), sliceStart, sliceEnd, DurableSliceStatus.Completed, expectedVersion: 0);
            readModels.RecordAttempt("long-layout-attempt-earlier", JobId(longJobId), earlierSliceStart, earlierSliceEnd, 1, "Succeeded", "worker", earlierSliceStart, earlierSliceEnd);
            readModels.RecordAttempt("long-layout-attempt-latest", JobId(longJobId), sliceStart, sliceEnd, 1, "Succeeded", "worker", sliceStart, sliceEnd);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var dashboard = await client.GetStringAsync("/");
            var catalogPage = await client.GetStringAsync("/jobs");
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
            Assert.DoesNotContain($"/jobs/{Uri.EscapeDataString(JobId(longJobId))}/actions/soft-delete", dashboard);
            Assert.Contains($"/jobs/{Uri.EscapeDataString(JobId(longJobId))}/actions/soft-delete", catalogPage);
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
        public void Schedule_form_input_round_trips_normalized_tags()
        {
            var input = ScheduleFormInput.FromJson(Schedule("job.tags", "TagFunction", isPaused: false, tags: ["Prod", " daily ", "PROD"]));

            Assert.Equal("prod" + Environment.NewLine + "daily", input.Tags);
            Assert.Equal(["prod", "daily"], input.NormalizedTags);

            input.Tags = "Security; PROD\nsecurity";
            var outputJson = input.ToScheduleJson();
            var parsed = ScheduleParser.Parse(outputJson);

            Assert.True(parsed.IsValid, string.Join(Environment.NewLine, parsed.Errors.Select(e => $"{e.Field}: {e.Message}")));
            Assert.Equal(["security", "prod"], input.NormalizedTags);
            Assert.Equal(["security", "prod"], parsed.Definition!.Tags);
            using var document = JsonDocument.Parse(outputJson);
            Assert.Equal(["security", "prod"], document.RootElement.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString() ?? string.Empty).ToArray());
        }

        [Fact]
        public void Schedule_form_input_round_trips_markdown_description_and_omits_blank_values()
        {
            const string description = "# Purpose\n\nRuns **daily**.\n\n    keep indentation";
            var input = ScheduleFormInput.FromJson(Schedule(
                "job.description.form",
                "DescriptionFunction",
                isPaused: false,
                description: description));

            Assert.Equal(description, input.Description);
            var output = ScheduleParser.Parse(input.ToScheduleJson());
            Assert.True(output.IsValid, string.Join(Environment.NewLine, output.Errors.Select(e => $"{e.Field}: {e.Message}")));
            Assert.Equal(description, output.Definition!.Description);

            input.Description = " \r\n ";
            using var blankDocument = JsonDocument.Parse(input.ToScheduleJson());
            Assert.False(blankDocument.RootElement.TryGetProperty("description", out _));
        }

        [Fact]
        public async Task Description_editor_details_copy_and_history_preserve_encoded_markdown()
        {
            const string description = "# Purpose\n\n<script>alert('description')</script>\n\n[unsafe](javascript:alert(1))";
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var record = catalog.Create(Schedule(
                "job.description.web",
                "DescriptionWebFunction",
                isPaused: false,
                description: description));
            var empty = catalog.Create(Schedule("job.description.empty", "EmptyDescriptionFunction", isPaused: false));
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var details = await client.GetStringAsync($"/jobs/{record.JobId}");
            var copy = await client.GetStringAsync($"/jobs/{record.JobId}/copy");
            var emptyDetails = await client.GetStringAsync($"/jobs/{empty.JobId}");

            Assert.Contains("class=\"card job-description-card\" data-job-description", details, StringComparison.Ordinal);
            Assert.Contains("data-job-description-source hidden", details, StringComparison.Ordinal);
            Assert.Contains("&lt;script&gt;", details, StringComparison.Ordinal);
            Assert.DoesNotContain("<script>alert('description')</script>", details, StringComparison.Ordinal);
            Assert.Contains("name=\"Input.Description\" rows=\"6\" maxlength=\"65536\"", details, StringComparison.Ordinal);
            Assert.Contains("name=\"Input.Description\" rows=\"6\" maxlength=\"65536\"", copy, StringComparison.Ordinal);
            Assert.Contains("&lt;script&gt;", copy, StringComparison.Ordinal);
            Assert.DoesNotContain("data-job-description", emptyDetails, StringComparison.Ordinal);

            _ = catalog.Update(
                record.JobId,
                Schedule("job.description.web", "DescriptionWebFunction", isPaused: false, description: "Updated purpose"),
                expectedVersion: record.CatalogVersion);
            var updatedDetails = await client.GetStringAsync($"/jobs/{record.JobId}");
            Assert.Contains("<code>$.description</code>", updatedDetails, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Shared_schedule_editor_renders_multiple_tags_as_separate_chips()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var record = catalog.Create(Schedule("job.tag.editor", "TagEditorFunction", isPaused: false, tags: ["Harvest", "v2"]));
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var taggedPages = new[]
            {
                $"/jobs/{record.JobId}",
                $"/jobs/{record.JobId}/edit",
                $"/jobs/{record.JobId}/copy"
            };
            foreach (var path in taggedPages)
            {
                var html = await client.GetStringAsync(path);
                Assert.Contains("data-tag-picker", html, StringComparison.Ordinal);
                Assert.Contains("data-tag-chip data-tag-value=\"harvest\"", html, StringComparison.Ordinal);
                Assert.Contains("data-tag-chip data-tag-value=\"v2\"", html, StringComparison.Ordinal);
                Assert.Contains("aria-label=\"Remove tag harvest\"", html, StringComparison.Ordinal);
                Assert.Contains("aria-label=\"Remove tag v2\"", html, StringComparison.Ordinal);
                Assert.DoesNotContain("<label>Tags<input", html, StringComparison.Ordinal);
            }

            var newJobHtml = await client.GetStringAsync("/jobs/new");
            Assert.Contains("data-tag-picker", newJobHtml, StringComparison.Ordinal);
            Assert.Contains("name=\"Input.Tags\" data-tag-hidden", newJobHtml, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Schedule_editor_renders_help_for_every_field_on_info_tooltips()
        {
            var record = new SqliteJobCatalogRepository(sqlite)
                .Create(Schedule("job.field.help", "FieldHelpFunction", isPaused: false));
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var html = await client.GetStringAsync($"/jobs/{record.JobId}");
            var expectedFields = new[]
            {
                "job-id",
                "activity-id",
                "description",
                "function-name",
                "output-table",
                "query-window-size",
                "delay-from-utc-now",
                "max-parallelism",
                "chunks",
                "query-timeout",
                "start-from",
                "end-on",
                "folder",
                "tags",
                "target-cluster-uri",
                "target-database",
                "health-policy",
                "is-paused",
                "dependencies",
                "job-settings"
            };

            Assert.All(
                expectedFields,
                field => Assert.Contains($"data-field-info=\"{field}\" role=\"img\" tabindex=\"0\"", html, StringComparison.Ordinal));
            Assert.Equal(expectedFields.Length, Regex.Matches(html, "data-field-info=\"").Count);
            Assert.Contains("title=\"Permanent immutable GUID assigned by KO Lite.", html, StringComparison.Ordinal);
            Assert.Contains("title=\"Concurrent execution units for this job: chunks for chunked jobs, otherwise slices.", html, StringComparison.Ordinal);
            Assert.Contains("title=\"Optional, 1-32. Adds chunkId and chunks arguments to every function call.", html, StringComparison.Ordinal);
            Assert.DoesNotContain("id=\"schedule-description-help\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("id=\"schedule-tags-help\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("id=\"schedule-max-parallelism-help\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("id=\"schedule-chunks-help\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("class=\"muted dependency-picker-help\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("aria-describedby=\"schedule-description-help\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("aria-describedby=\"schedule-tags-help\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("aria-describedby=\"schedule-max-parallelism-help\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("aria-describedby=\"schedule-chunks-help\"", html, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Slice_detail_labels_chunk_logs_and_child_events()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var record = catalog.Create(Schedule("job.chunk.evidence", "ChunkEvidenceFunction", isPaused: false, chunks: 2));
            var slice = new SliceRange(record.JobId, At(0), At(5));
            var chunks = new SqliteChunkStateRepository(sqlite);
            var children = chunks.EnsureWindow(slice, 2, "test");
            chunks.MarkQueued("chunk-0-queued", children[0].Execution, actor: "test");
            new SqliteOperationalReadModelRepository(sqlite).RecordLog(
                "Error",
                "chunk one failed",
                "worker",
                record.JobId,
                At(0),
                At(5),
                chunkId: 1,
                totalChunks: 2);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var html = await client.GetStringAsync(
                $"/jobs/{record.JobId}/slices?start={Uri.EscapeDataString(AppFormatting.Iso(At(0)))}&end={Uri.EscapeDataString(AppFormatting.Iso(At(5)))}");

            Assert.Contains("data-chunk-event=\"0\"", html, StringComparison.Ordinal);
            Assert.Contains("Chunk 0/2", html, StringComparison.Ordinal);
            Assert.Contains("data-chunk-log=\"1\"", html, StringComparison.Ordinal);
            Assert.Contains("chunk one failed", html, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Chunked_slice_history_renders_completion_progress_in_every_tooltip_representation()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var record = catalog.Create(Schedule("job.chunk.tooltip", "ChunkTooltipFunction", isPaused: false, chunks: 16));
            var slice = new SliceRange(record.JobId, At(0), At(5));
            var chunks = new SqliteChunkStateRepository(sqlite);
            var children = chunks.EnsureWindow(slice, 16, "test");
            for (var chunkId = 0; chunkId < 3; chunkId++)
            {
                var child = children[chunkId];
                chunks.MarkQueued($"chunk-{chunkId}-queued", child.Execution, actor: "test");
                var owner = $"chunk-{chunkId}-worker";
                var lease = chunks.AcquireLease($"chunk-{chunkId}-lease", child.Execution, owner, TimeSpan.FromMinutes(5), At(10))!;
                Assert.True(chunks.CompleteLease($"chunk-{chunkId}-complete", child.Execution, owner, lease.LeaseToken!, At(11)));
            }

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var html = await client.GetStringAsync($"/jobs/{record.JobId}");

            Assert.Contains("Chunks: 3/16", html, StringComparison.Ordinal);
            Assert.Contains("data-tooltip-label=\"Chunks\" data-tooltip-value=\"3/16\"", html, StringComparison.Ordinal);
            Assert.Contains("aria-label=\"2026-01-01T00:00:00Z to 2026-01-01T00:05:00Z; Queued; Chunks 3/16; attempt 1\"", html, StringComparison.Ordinal);
            Assert.Contains("data-tooltip-label=\"Chunks\" data-tooltip-value=\"0/16\"", html, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Schedule_editor_posts_multiple_normalized_tags()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var record = catalog.Create(Schedule("job.tag.post", "TagPostFunction", isPaused: false, tags: ["old"]));
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var editPath = $"/jobs/{record.JobId}/edit";
            var token = await ReadFormToken(client, editPath);

            var form = new Dictionary<string, string>
            {
                ["formMode"] = "fields",
                ["expectedVersion"] = record.CatalogVersion.ToString(),
                ["Input.Id"] = record.JobId,
                ["Input.ActivityId"] = "job.tag.post",
                ["Input.FunctionName"] = "TagPostFunction",
                ["Input.OutputTable"] = "Output",
                ["Input.QueryWindowSize"] = "00:05:00",
                ["Input.DelayFromUtcNow"] = "00:00:00",
                ["Input.MaxParallelism"] = "1",
                ["Input.QueryTimeout"] = "00:01:00",
                ["Input.IsPaused"] = "false",
                ["Input.HealthPolicy"] = "complete",
                ["Input.StartFrom"] = "2026-01-01T00:00:00Z",
                ["Input.ClusterUri"] = "https://kolite-example.invalid",
                ["Input.Database"] = "DemoDb",
                ["Input.Tags"] = "Harvest\nv2;HARVEST",
                ["Input.Description"] = "# Tagged job\n\nCatalog metadata only.",
                ["Input.JobSettingsJson"] = "{}"
            };

            using var response = await PostForm(client, $"/jobs/{record.JobId}/edit", token, form);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var updated = catalog.Get(record.JobId)!;
            Assert.Equal(["harvest", "v2"], updated.Definition.Tags);
            Assert.Equal("# Tagged job\n\nCatalog metadata only.", updated.Description);
        }

        [Fact]
        public async Task Dashboard_defaults_to_activity_ascending_and_sorts_by_requested_column()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("job.charlie", "CharlieFunction", isPaused: false, queryWindowSize: "00:10:00"));
            catalog.Create(Schedule("job.alpha", "AlphaFunction", isPaused: false, queryWindowSize: "00:15:00"));
            catalog.Create(Schedule("job.bravo", "BravoFunction", isPaused: false, queryWindowSize: "00:05:00"));

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var defaultDashboard = await client.GetStringAsync("/");
            var activityDescending = await client.GetStringAsync("/?sort=activity&dir=desc");
            var scheduleAscending = await client.GetStringAsync("/?sort=schedule&dir=asc");
            var scheduleDescending = await client.GetStringAsync("/?sort=schedule&dir=desc");

            // Default is activityId ascending even though the jobs were created out of order.
            Assert.Equal(
                [JobId("job.alpha"), JobId("job.bravo"), JobId("job.charlie")],
                DashboardJobOrder(defaultDashboard));
            Assert.Equal(
                [JobId("job.charlie"), JobId("job.bravo"), JobId("job.alpha")],
                DashboardJobOrder(activityDescending));
            // Schedule = query window size: bravo 5m, charlie 10m, alpha 15m.
            Assert.Equal(
                [JobId("job.bravo"), JobId("job.charlie"), JobId("job.alpha")],
                DashboardJobOrder(scheduleAscending));
            Assert.Equal(
                [JobId("job.alpha"), JobId("job.charlie"), JobId("job.bravo")],
                DashboardJobOrder(scheduleDescending));

            // Header markup: the active column advertises its direction and links toggle it.
            Assert.Contains("class=\"sort-link\"", defaultDashboard);
            Assert.Contains("aria-sort=\"ascending\"", defaultDashboard);
            Assert.Contains("href=\"/?range=1d&amp;sort=activity&amp;dir=desc\"", defaultDashboard);
            Assert.Contains("href=\"/?range=1d&amp;sort=status&amp;dir=asc\"", defaultDashboard);
            Assert.Contains("href=\"/?range=1d&amp;sort=schedule&amp;dir=asc\"", scheduleDescending);
        }

        [Fact]
        public async Task Dashboard_sort_is_preserved_across_range_and_tag_filter_links()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("job.ops.alpha", "OpsAlpha", isPaused: false, queryWindowSize: "00:15:00", tags: ["ops"]));
            catalog.Create(Schedule("job.ops.bravo", "OpsBravo", isPaused: false, queryWindowSize: "00:05:00", tags: ["ops"]));

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var filtered = await client.GetStringAsync("/?tag=ops&sort=schedule&dir=desc");

            // Sort links keep the active tag filter in their query string.
            Assert.Contains("href=\"/?range=1d&amp;tag=ops&amp;sort=schedule&amp;dir=asc\"", filtered);
            // Range buttons carry the current non-default sort forward.
            Assert.Contains("range=7d&amp;tag=ops&amp;sort=schedule&amp;dir=desc", filtered);
            // Ordering still honours the requested sort within the tag-filtered set.
            Assert.Equal(
                [JobId("job.ops.alpha"), JobId("job.ops.bravo")],
                DashboardJobOrder(filtered));
        }

        [Fact]
        public async Task Dashboard_and_catalog_render_and_filter_schedule_tags()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("job.prod.daily", "DailyFunction", isPaused: false, tags: ["Prod", "daily"]));
            catalog.Create(Schedule("job.prod.weekly", "WeeklyFunction", isPaused: false, tags: ["prod", "weekly"]));
            catalog.Create(Schedule("job.security", "SecurityFunction", isPaused: false, tags: ["security"]));
            var softDeleted = catalog.Create(Schedule("job.prod.soft", "SoftFunction", isPaused: false, tags: ["prod", "daily"]));
            new SqliteJobLifecycleService(sqlite, catalog).SoftDelete(JobId("job.prod.soft"), softDeleted.CatalogVersion, "web-test", "exclude from active charts");
            var state = new SqliteSliceStateRepository(sqlite);
            var readModels = new SqliteOperationalReadModelRepository(sqlite);
            var now = DateTimeOffset.UtcNow;
            var currentHour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
            var chartStart = currentHour.AddMinutes(-30);
            var chartEnd = currentHour.AddMinutes(-25);
            foreach (var jobId in new[] { "job.prod.daily", "job.prod.weekly", "job.security", "job.prod.soft" })
            {
                state.Append("chart-" + jobId, JobId(jobId), chartStart, chartEnd, DurableSliceStatus.Completed, expectedVersion: 0);
                readModels.RecordAttempt("attempt-" + jobId, JobId(jobId), chartStart, chartEnd, 1, "Succeeded", "worker", chartStart, chartEnd);
            }

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var dashboard = await client.GetStringAsync("/");
            var filteredDashboard = await client.GetStringAsync("/?tag=PROD&tag=daily");
            var catalogPage = await client.GetStringAsync("/jobs?tag=prod&tag=weekly");
            var dashboardChartSeries = DashboardSuccessChartSeries(dashboard);
            var filteredChartSeries = DashboardSuccessChartSeries(filteredDashboard);

            Assert.Contains("Schedule tag filters", dashboard);
            Assert.Contains("Text filter", dashboard);
            Assert.DoesNotContain("Matches activity ID, status, schedule, and next eligible text. Charts keep using the selected tags.", dashboard);
            Assert.DoesNotContain("dashboard-job-filter-help", dashboard);
            Assert.Contains("href=\"/?range=1d&amp;tag=prod\"", dashboard);
            Assert.Contains("class=\"jobs-with-tags\"", dashboard);
            Assert.Contains("class=\"tag-filter-pane\"", dashboard);
            Assert.Contains("data-dashboard-filter-pane=\"true\"", dashboard);
            Assert.DoesNotContain("<th>Tags</th>", dashboard);
            Assert.DoesNotContain("tag-chip compact", dashboard);
            Assert.Contains("Showing jobs tagged with all selected tags: prod, daily", filteredDashboard);
            Assert.Contains("href=\"/?range=7d&amp;tag=prod&amp;tag=daily\"", filteredDashboard);
            Assert.Contains("job.prod.daily", filteredDashboard);
            Assert.Contains("job.prod.soft", filteredDashboard);
            Assert.DoesNotContain("job.prod.weekly", filteredDashboard);
            Assert.DoesNotContain("job.security", filteredDashboard);
            Assert.DoesNotContain(JobId("job.prod.soft"), dashboardChartSeries);
            Assert.Equal([JobId("job.prod.daily")], filteredChartSeries);
            Assert.Contains("job.prod.weekly", catalogPage);
            Assert.DoesNotContain("job.prod.daily", catalogPage);
            Assert.DoesNotContain("job.security", catalogPage);
            Assert.Contains("href=\"/jobs?tag=prod\"", catalogPage);
            Assert.DoesNotContain("data-dashboard-filter-input=\"true\"", catalogPage);
            Assert.DoesNotContain("data-dashboard-resizable=\"true\"", catalogPage);
        }

        [Fact]
        public async Task Catalog_gets_render_and_post_create_update_enable_disable_in_local_sqlite()
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var createToken = await ReadFormToken(client, "/jobs/new");
            var create = await PostForm(client, "/jobs/new", createToken, new Dictionary<string, string>
            {
                ["scheduleJson"] = Schedule("job.catalog", "CatalogFunction", isPaused: false)
            });
            Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
            Assert.Equal($"/jobs/{JobId("job.catalog")}", create.Headers.Location?.OriginalString);

            var catalog = new SqliteJobCatalogRepository(sqlite);
            Assert.True(catalog.Get(JobId("job.catalog"))?.IsEnabled);

            var editToken = await ReadFormToken(client, $"/jobs/{JobId("job.catalog")}/edit");
            var update = await PostForm(client, $"/jobs/{JobId("job.catalog")}/edit", editToken, new Dictionary<string, string>
            {
                ["expectedVersion"] = "1",
                ["scheduleJson"] = Schedule("job.catalog", "CatalogFunctionV2", isPaused: false)
            });
            Assert.Equal(HttpStatusCode.Redirect, update.StatusCode);
            Assert.Equal("CatalogFunctionV2", catalog.Get(JobId("job.catalog"))?.QueryRef);

            var disableToken = await ReadFormToken(client, $"/jobs/{JobId("job.catalog")}/edit");
            var disable = await PostForm(client, $"/jobs/{JobId("job.catalog")}/actions/pause", disableToken, new Dictionary<string, string>
            {
                ["expectedVersion"] = "2"
            });
            Assert.Equal(HttpStatusCode.Redirect, disable.StatusCode);
            Assert.False(catalog.Get(JobId("job.catalog"))?.IsEnabled);
            var pausedDetails = await client.GetStringAsync($"/jobs/{JobId("job.catalog")}");
            Assert.Contains("class=\"paused-job-indicator\"", pausedDetails);
            Assert.Contains("Scheduling is paused. New slices and queued retries will not run until this job is resumed.", pausedDetails);

            var enableToken = await ReadFormToken(client, $"/jobs/{JobId("job.catalog")}/edit");
            var enable = await PostForm(client, $"/jobs/{JobId("job.catalog")}/actions/resume", enableToken, new Dictionary<string, string>
            {
                ["expectedVersion"] = "3"
            });
            Assert.Equal(HttpStatusCode.Redirect, enable.StatusCode);
            Assert.True(catalog.Get(JobId("job.catalog"))?.IsEnabled);

            var page = await client.GetStringAsync("/jobs");
            Assert.Contains("Job Catalog", page);
            Assert.Contains("job.catalog", page);
            Assert.Contains("<th>Progress</th>", page);
            Assert.Contains($"href=\"/jobs/{JobId("job.catalog")}/history\"", page);
            Assert.Contains("aria-label=\"History\"", page);

            var details = await client.GetStringAsync($"/jobs/{JobId("job.catalog")}");
            Assert.Contains("Schedule fields", details);
            Assert.Contains("Raw JSON", details);
            Assert.Contains("Slice history", details);
            Assert.Contains("Job definition", details);
            Assert.Contains("Operations", details);
            Assert.Contains("Change history", details);
            Assert.Contains("Catalog definition history", details);
            Assert.Contains("catalog-diff-table", details);
            Assert.Contains("$.functionName", details);
            Assert.Contains("CatalogFunctionV2", details);
            Assert.Contains("$.isPaused", details);
            Assert.Contains("Initial schedule definition.", details);
            Assert.DoesNotContain("class=\"paused-job-indicator\"", details);
            Assert.DoesNotContain("<span class=\"stat-label\">Catalog version</span>", details);
            Assert.DoesNotContain("<span class=\"stat-label\">Enabled</span>", details);
            Assert.DoesNotContain("<span class=\"stat-label\">Completed</span>", details);
            Assert.DoesNotContain("<span class=\"stat-label\">Failed/blocked</span>", details);
            Assert.DoesNotContain("Job state history", details);

            var softDeleteToken = await ReadFormToken(client, $"/jobs/{JobId("job.catalog")}");
            var softDelete = await PostForm(client, $"/jobs/{JobId("job.catalog")}/actions/soft-delete", softDeleteToken, new Dictionary<string, string>
            {
                ["expectedVersion"] = "4",
                ["reason"] = "test soft delete"
            });
            Assert.Equal(HttpStatusCode.Redirect, softDelete.StatusCode);
            Assert.False(catalog.Get(JobId("job.catalog"))?.IsEnabled);
            var softDeletedDetails = await client.GetStringAsync($"/jobs/{JobId("job.catalog")}");
            Assert.DoesNotContain("class=\"paused-job-indicator\"", softDeletedDetails);

            var softDeletedPage = await client.GetStringAsync("/");
            Assert.Contains("Inactive jobs", softDeletedPage);
            Assert.Contains("Soft-deleted jobs", softDeletedPage);
            Assert.Contains("job.catalog", softDeletedPage);

            var restoreToken = await ReadFormToken(client, "/");
            var restore = await PostForm(client, $"/jobs/{JobId("job.catalog")}/actions/restore", restoreToken, new Dictionary<string, string>
            {
                ["expectedVersion"] = "5",
                ["reason"] = "test restore"
            });
            Assert.Equal(HttpStatusCode.Redirect, restore.StatusCode);
            Assert.True(catalog.Get(JobId("job.catalog"))?.IsEnabled);

            var softDeleteAgainToken = await ReadFormToken(client, $"/jobs/{JobId("job.catalog")}");
            var softDeleteAgain = await PostForm(client, $"/jobs/{JobId("job.catalog")}/actions/soft-delete", softDeleteAgainToken, new Dictionary<string, string>
            {
                ["expectedVersion"] = "6",
                ["reason"] = "test hard-delete precondition"
            });
            Assert.Equal(HttpStatusCode.Redirect, softDeleteAgain.StatusCode);

            var hardDeletePage = await client.GetStringAsync($"/jobs/{JobId("job.catalog")}/hard-delete");
            Assert.Contains("Hard delete job.catalog", hardDeletePage);
            Assert.Contains("DELETE job.catalog", hardDeletePage);
            Assert.Contains($"id: {JobId("job.catalog")}", hardDeletePage);
            Assert.DoesNotContain($"Hard delete {JobId("job.catalog")}", hardDeletePage);

            var hardDeleteToken = await ReadFormToken(client, $"/jobs/{JobId("job.catalog")}/hard-delete");
            var blockedHardDelete = await PostForm(client, $"/jobs/{JobId("job.catalog")}/hard-delete", hardDeleteToken, new Dictionary<string, string>
            {
                ["confirmation"] = "DELETE wrong.job",
                ["reason"] = "bad confirmation"
            });
            Assert.Equal(HttpStatusCode.BadRequest, blockedHardDelete.StatusCode);
            Assert.NotNull(catalog.Get(JobId("job.catalog")));

            hardDeleteToken = await ReadFormToken(client, $"/jobs/{JobId("job.catalog")}/hard-delete");
            var hardDelete = await PostForm(client, $"/jobs/{JobId("job.catalog")}/hard-delete", hardDeleteToken, new Dictionary<string, string>
            {
                ["confirmation"] = "DELETE job.catalog",
                ["reason"] = "test hard delete"
            });
            var hardDeleteBody = await hardDelete.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, hardDelete.StatusCode);
            Assert.Contains("Hard delete completed", hardDeleteBody);
            Assert.Null(catalog.Get(JobId("job.catalog")));
        }

        [Fact]
        public async Task Stale_detail_actions_redirect_to_fresh_job_with_guidance()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var pauseJob = catalog.Create(Schedule("job.stale.pause", "PauseFunction", isPaused: false));
            var lifecycleJob = catalog.Create(Schedule("job.stale.lifecycle", "LifecycleFunction", isPaused: false));
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var pauseToken = await ReadFormToken(client, $"/jobs/{pauseJob.JobId}");
            var currentPause = catalog.Update(
                pauseJob.JobId,
                Schedule("job.stale.pause", "PauseFunction", isPaused: false, maxParallelism: 2),
                pauseJob.CatalogVersion);
            using var pause = await PostForm(client, $"/jobs/{pauseJob.JobId}/actions/pause", pauseToken, new Dictionary<string, string>
            {
                ["expectedVersion"] = pauseJob.CatalogVersion.ToString()
            });

            Assert.Equal(HttpStatusCode.Redirect, pause.StatusCode);
            Assert.Equal($"/jobs/{pauseJob.JobId}", pause.Headers.Location?.OriginalString);
            Assert.True(catalog.Get(pauseJob.JobId)!.IsEnabled);
            Assert.Equal(currentPause.CatalogVersion, catalog.Get(pauseJob.JobId)!.CatalogVersion);
            var pauseDetails = await client.GetStringAsync(pause.Headers.Location!.OriginalString);
            Assert.Contains("This job changed after the page loaded", pauseDetails);
            Assert.Contains("your request was not applied", pauseDetails);
            Assert.Contains("Review it and try again", pauseDetails);

            var softDeleteToken = await ReadFormToken(client, $"/jobs/{lifecycleJob.JobId}");
            var currentLifecycle = catalog.Update(
                lifecycleJob.JobId,
                Schedule("job.stale.lifecycle", "LifecycleFunction", isPaused: false, maxParallelism: 2),
                lifecycleJob.CatalogVersion);
            using var softDelete = await PostForm(client, $"/jobs/{lifecycleJob.JobId}/actions/soft-delete", softDeleteToken, new Dictionary<string, string>
            {
                ["expectedVersion"] = lifecycleJob.CatalogVersion.ToString(),
                ["reason"] = "stale soft delete"
            });

            Assert.Equal(HttpStatusCode.Redirect, softDelete.StatusCode);
            Assert.Equal($"/jobs/{lifecycleJob.JobId}", softDelete.Headers.Location?.OriginalString);
            Assert.True(catalog.Get(lifecycleJob.JobId)!.IsEnabled);
            Assert.Contains("This job changed after the page loaded", await client.GetStringAsync(softDelete.Headers.Location!.OriginalString));

            var lifecycle = new SqliteJobLifecycleService(sqlite, catalog);
            var softDeleted = lifecycle.SoftDelete(lifecycleJob.JobId, currentLifecycle.CatalogVersion, "test", "prepare restore conflict");
            var currentSoftDeleted = catalog.SetEnabled(lifecycleJob.JobId, enabled: false, expectedVersion: softDeleted.CatalogVersion);
            var restoreToken = await ReadFormToken(client, $"/jobs/{lifecycleJob.JobId}");
            using var restore = await PostForm(client, $"/jobs/{lifecycleJob.JobId}/actions/restore", restoreToken, new Dictionary<string, string>
            {
                ["expectedVersion"] = softDeleted.CatalogVersion.ToString(),
                ["reason"] = "stale restore"
            });

            Assert.Equal(HttpStatusCode.Redirect, restore.StatusCode);
            Assert.Equal($"/jobs/{lifecycleJob.JobId}", restore.Headers.Location?.OriginalString);
            Assert.False(catalog.Get(lifecycleJob.JobId)!.IsEnabled);
            Assert.Equal(currentSoftDeleted.CatalogVersion, catalog.Get(lifecycleJob.JobId)!.CatalogVersion);
            Assert.Contains("This job changed after the page loaded", await client.GetStringAsync(restore.Headers.Location!.OriginalString));
        }

        [Fact]
        public async Task Stale_edit_redirects_to_latest_definition_and_discards_submitted_values()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var created = catalog.Create(Schedule("job.stale.edit", "OriginalFunction", isPaused: false));
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var token = await ReadFormToken(client, $"/jobs/{created.JobId}/edit");
            var current = catalog.Update(
                created.JobId,
                Schedule("job.stale.edit", "CurrentFunction", isPaused: false),
                created.CatalogVersion);

            using var response = await PostForm(client, $"/jobs/{created.JobId}/edit", token, new Dictionary<string, string>
            {
                ["expectedVersion"] = created.CatalogVersion.ToString(),
                ["scheduleJson"] = Schedule("job.stale.edit", "StaleSubmittedFunction", isPaused: false)
            });

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal($"/jobs/{created.JobId}/edit", response.Headers.Location?.OriginalString);
            Assert.Equal("CurrentFunction", catalog.Get(created.JobId)!.QueryRef);
            Assert.Equal(current.CatalogVersion, catalog.Get(created.JobId)!.CatalogVersion);

            var edit = await client.GetStringAsync(response.Headers.Location!.OriginalString);
            Assert.Contains("This job changed after the page loaded", edit);
            Assert.Contains($"Catalog version {current.CatalogVersion}", edit);
            Assert.Contains("CurrentFunction", edit);
            Assert.DoesNotContain("StaleSubmittedFunction", edit);
        }

        [Fact]
        public async Task Dashboard_inline_pause_toggle_returns_json_state_without_redirecting()
        {
            new SqliteJobCatalogRepository(sqlite).Create(Schedule("job.toggle", "ToggleFunction", isPaused: false));
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var catalog = new SqliteJobCatalogRepository(sqlite);

            var dashboard = await client.GetStringAsync("/");
            Assert.Contains("data-dashboard-toggle=\"true\"", dashboard);
            Assert.Contains($"data-toggle-base=\"/jobs/{JobId("job.toggle")}/actions\"", dashboard);

            var token = await ReadFormToken(client, "/");

            var pause = await PostFormAjax(client, $"/jobs/{JobId("job.toggle")}/actions/pause", token, new Dictionary<string, string>
            {
                ["expectedVersion"] = "1"
            });
            Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
            Assert.Equal("application/json", pause.Content.Headers.ContentType?.MediaType);
            using (var pauseDoc = JsonDocument.Parse(await pause.Content.ReadAsStringAsync()))
            {
                var root = pauseDoc.RootElement;
                Assert.False(root.GetProperty("enabled").GetBoolean());
                Assert.Equal(2, root.GetProperty("version").GetInt64());
                Assert.Equal("paused", root.GetProperty("primaryKey").GetString());
                Assert.Contains("Paused", root.GetProperty("healthTooltip").GetString()!, StringComparison.Ordinal);
                Assert.False(root.GetProperty("conflict").GetBoolean());
            }
            Assert.False(catalog.Get(JobId("job.toggle"))?.IsEnabled);

            var resume = await PostFormAjax(client, $"/jobs/{JobId("job.toggle")}/actions/resume", token, new Dictionary<string, string>
            {
                ["expectedVersion"] = "2"
            });
            Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
            using (var resumeDoc = JsonDocument.Parse(await resume.Content.ReadAsStringAsync()))
            {
                var root = resumeDoc.RootElement;
                Assert.True(root.GetProperty("enabled").GetBoolean());
                Assert.Equal(3, root.GetProperty("version").GetInt64());
                Assert.Equal("healthy", root.GetProperty("primaryKey").GetString());
                Assert.Contains("Healthy", root.GetProperty("healthTooltip").GetString()!, StringComparison.Ordinal);
            }
            Assert.True(catalog.Get(JobId("job.toggle"))?.IsEnabled);

            var conflict = await PostFormAjax(client, $"/jobs/{JobId("job.toggle")}/actions/pause", token, new Dictionary<string, string>
            {
                ["expectedVersion"] = "1"
            });
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            using (var conflictDoc = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync()))
            {
                var root = conflictDoc.RootElement;
                Assert.True(root.GetProperty("conflict").GetBoolean());
                Assert.True(root.GetProperty("enabled").GetBoolean());
                Assert.Equal(3, root.GetProperty("version").GetInt64());
                Assert.Contains("your request was not applied", root.GetProperty("error").GetString());
                Assert.Contains("Review it and try again", root.GetProperty("error").GetString());
            }
            Assert.True(catalog.Get(JobId("job.toggle"))?.IsEnabled);
        }

        [Fact]
        public async Task Started_job_edit_page_marks_protected_fields_readonly_and_rejects_raw_json_tampering()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("job.readonly", "ReadOnlyFunction", isPaused: false));
            var state = new SqliteSliceStateRepository(sqlite);
            state.Append("readonly-started", JobId("job.readonly"), At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var edit = await client.GetStringAsync($"/jobs/{JobId("job.readonly")}/edit");

            Assert.DoesNotMatch("name=\"Input\\.ActivityId\"[^>]* readonly", edit);
            Assert.Matches("name=\"Input\\.QueryWindowSize\"[^>]*readonly", edit);
            Assert.Matches("name=\"Input\\.StartFrom\"[^>]*readonly", edit);
            Assert.DoesNotContain("These fields are read-only because this job has execution history", edit);
            Assert.DoesNotContain("Raw JSON changes to read-only fields are rejected server-side", edit);

            var token = await ReadFormToken(client, $"/jobs/{JobId("job.readonly")}/edit");
            var tampered = await PostForm(client, $"/jobs/{JobId("job.readonly")}/edit", token, new Dictionary<string, string>
            {
                ["expectedVersion"] = "1",
                ["scheduleJson"] = Schedule("job.readonly", "ReadOnlyFunction", isPaused: false)
                    .Replace("\"queryWindowSize\": \"00:05:00\"", "\"queryWindowSize\": \"00:10:00\"", StringComparison.Ordinal)
            });
            var body = await tampered.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);
            Assert.Contains("queryWindowSize", body);
            var stored = catalog.Get(JobId("job.readonly"))!;
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

            var importPage = await client.GetStringAsync("/jobs/import");
            Assert.DoesNotContain("Started jobs keep their activityId, queryWindowSize, and startFrom values", importPage);

            var pasteToken = await ReadFormToken(client, "/jobs/import");
            var pasted = await PostForm(client, "/jobs/import", pasteToken, new Dictionary<string, string>
            {
                ["importSource"] = "paste",
                ["scheduleJson"] = "[" + Schedule("job.existing", "ExistingFunctionV2", isPaused: true) + "," + Schedule("job.new", "NewFunction", isPaused: false) + "]"
            });
            var pastedBody = await pasted.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, pasted.StatusCode);
            Assert.Contains("Imported 2 jobs: 1 created, 1 updated. No jobs were deleted.", pastedBody);
            Assert.Contains("0 deleted", pastedBody);
            Assert.Equal("ExistingFunctionV2", catalog.Get(JobId("job.existing"))?.QueryRef);
            Assert.False(catalog.Get(JobId("job.existing"))?.IsEnabled);
            Assert.NotNull(catalog.Get(JobId("job.new")));
            Assert.Equal("OmittedFunction", catalog.Get(JobId("job.omitted"))?.QueryRef);

            var fileToken = await ReadFormToken(client, "/jobs/import");
            var fileImport = await PostMultipart(client, "/jobs/import", fileToken, "[" + Schedule("job.file-a", "FileFunctionA", isPaused: false) + "," + Schedule("job.file-b", "FileFunctionB", isPaused: false) + "]", "jobs.json");
            var fileBody = await fileImport.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, fileImport.StatusCode);
            Assert.Contains("job.file-a", fileBody);
            Assert.Contains("job.file-b", fileBody);
            Assert.NotNull(catalog.Get(JobId("job.file-a")));
            Assert.NotNull(catalog.Get(JobId("job.file-b")));
            Assert.NotNull(catalog.Get(JobId("job.omitted")));
        }

        [Fact]
        public async Task Dashboard_omits_export_all_button_and_route_exports_non_soft_deleted_jobs()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("job.active", "ActiveFunction", isPaused: false));
            catalog.Create(Schedule("job.disabled", "DisabledFunction", isPaused: true));
            var softDeleted = catalog.Create(Schedule("job.soft", "SoftDeletedFunction", isPaused: false));
            new SqliteJobLifecycleService(sqlite, catalog).SoftDelete(JobId("job.soft"), softDeleted.CatalogVersion, "web-test", "exclude from export all");
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var dashboard = await client.GetStringAsync("/");
            using var response = await client.GetAsync("/jobs/export");
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var ids = document.RootElement.EnumerateArray()
                .Select(item => item.GetProperty("activityId").GetString() ?? string.Empty)
                .ToArray();

            Assert.DoesNotContain("href=\"/jobs/export\"", dashboard);
            Assert.DoesNotContain(">Export all</span>", dashboard);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(["job.active", "job.disabled"], ids);
        }

        [Fact]
        public async Task Dashboard_limits_bulk_selection_to_active_and_completed_and_links_soft_deleted_management()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("job.alpha", "AlphaFunction", isPaused: false));
            var soft = catalog.Create(Schedule("job.soft", "SoftFunction", isPaused: false));
            new SqliteJobLifecycleService(sqlite, catalog).SoftDelete(JobId("job.soft"), soft.CatalogVersion, "web-test", "exclude");
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var dashboard = await client.GetStringAsync("/");
            var catalogPage = await client.GetStringAsync("/jobs");

            Assert.Contains("data-bulk-bar", dashboard);
            Assert.Contains("data-bulk-select-all data-bulk-section=\"active\"", dashboard);
            Assert.Contains($"data-bulk-select value=\"{JobId("job.alpha")}\" data-expected-version=\"1\"", dashboard);
            Assert.Contains("aria-label=\"Select job.alpha\"", dashboard);
            Assert.Contains("formaction=\"/jobs/actions/bulk/pause\"", dashboard);
            Assert.Contains("formaction=\"/jobs/actions/bulk/resume\"", dashboard);
            Assert.Contains("formaction=\"/jobs/actions/bulk/soft-delete\"", dashboard);
            Assert.Contains("formaction=\"/jobs/actions/bulk/export\"", dashboard);
            Assert.DoesNotContain("formaction=\"/jobs/soft-deleted/hard-delete\"", dashboard);
            // The bulk form must carry an antiforgery token so the no-fetch POST submit is accepted.
            var bulkForm = dashboard.Substring(dashboard.IndexOf("bulk-action-form", StringComparison.Ordinal));
            bulkForm = bulkForm.Substring(0, bulkForm.IndexOf("</form>", StringComparison.Ordinal));
            Assert.Contains("__RequestVerificationToken", bulkForm);
            // The action bar must sit outside the two-column jobs grid so it does not break the layout.
            Assert.True(
                dashboard.IndexOf("data-bulk-bar", StringComparison.Ordinal) < dashboard.IndexOf("data-dashboard-filter-root", StringComparison.Ordinal),
                "The bulk action bar should render before (outside) the jobs grid.");
            Assert.DoesNotContain("data-bulk-select-all data-bulk-section=\"soft-deleted\"", dashboard);
            Assert.DoesNotContain($"data-bulk-select value=\"{JobId("job.soft")}\"", dashboard);
            Assert.Contains("href=\"/jobs/soft-deleted\">Manage soft-deleted jobs</a>", dashboard);
            // The catalog page does not get the bulk experience at all.
            Assert.DoesNotContain("data-bulk-bar", catalogPage);
            Assert.DoesNotContain("data-bulk-select-all", catalogPage);
        }

        [Fact]
        public async Task Dashboard_bulk_pause_disables_selected_jobs_and_skips_soft_deleted()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("job.one", "OneFunction", isPaused: false));
            catalog.Create(Schedule("job.two", "TwoFunction", isPaused: false));
            var soft = catalog.Create(Schedule("job.soft", "SoftFunction", isPaused: false));
            new SqliteJobLifecycleService(sqlite, catalog).SoftDelete(JobId("job.soft"), soft.CatalogVersion, "web-test", "exclude");
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var token = await ReadFormToken(client, "/");

            var response = await PostFormValues(client, "/jobs/actions/bulk/pause", token,
            [
                new("jobIds", JobId("job.one")), new("expectedVersions", "1"),
                new("jobIds", JobId("job.two")), new("expectedVersions", "1"),
                new("jobIds", JobId("job.soft")), new("expectedVersions", "2")
            ]);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/", response.Headers.Location?.OriginalString);
            Assert.False(catalog.Get(JobId("job.one"))?.IsEnabled);
            Assert.False(catalog.Get(JobId("job.two"))?.IsEnabled);
            // Soft-deleted job is skipped, so no further catalog-version bump beyond the soft delete (version 2).
            Assert.Equal(2, catalog.Get(JobId("job.soft"))?.CatalogVersion);
        }

        [Fact]
        public async Task Dashboard_bulk_resume_enables_selected_paused_jobs()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("job.one", "OneFunction", isPaused: true));
            catalog.Create(Schedule("job.two", "TwoFunction", isPaused: true));
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var token = await ReadFormToken(client, "/");

            var response = await PostFormValues(client, "/jobs/actions/bulk/resume", token,
            [
                new("jobIds", JobId("job.one")), new("expectedVersions", "1"),
                new("jobIds", JobId("job.two")), new("expectedVersions", "1")
            ]);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.True(catalog.Get(JobId("job.one"))?.IsEnabled);
            Assert.True(catalog.Get(JobId("job.two"))?.IsEnabled);
        }

        [Fact]
        public async Task Dashboard_bulk_soft_delete_marks_selected_jobs_soft_deleted()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("job.one", "OneFunction", isPaused: false));
            catalog.Create(Schedule("job.two", "TwoFunction", isPaused: false));
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var token = await ReadFormToken(client, "/");

            var response = await PostFormValues(client, "/jobs/actions/bulk/soft-delete", token,
            [
                new("jobIds", JobId("job.one")), new("expectedVersions", "1"),
                new("jobIds", JobId("job.two")), new("expectedVersions", "1")
            ]);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var states = new LifecycleReadModel(new SqliteLifecycleReadModelRepository(sqlite)).GetLatestStates();
            Assert.True(states[JobId("job.one")].IsSoftDeleted);
            Assert.True(states[JobId("job.two")].IsSoftDeleted);
        }

        [Fact]
        public async Task Dashboard_bulk_export_downloads_selected_jobs_in_catalog_order()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("job.b", "BFunction", isPaused: false));
            catalog.Create(Schedule("job.a", "AFunction", isPaused: false));
            catalog.Create(Schedule("job.c", "CFunction", isPaused: false));
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var token = await ReadFormToken(client, "/");

            using var response = await PostFormValues(client, "/jobs/actions/bulk/export", token,
            [
                new("jobIds", JobId("job.b")),
                new("jobIds", JobId("job.a"))
            ]);
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var ids = document.RootElement.EnumerateArray()
                .Select(item => item.GetProperty("activityId").GetString() ?? string.Empty)
                .ToArray();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal("ko-lite-jobs.json", response.Content.Headers.ContentDisposition?.FileName);
            Assert.Equal(["job.a", "job.b"], ids);
        }

        [Fact]
        public async Task Dashboard_bulk_pause_dedupes_skips_missing_and_reports_version_conflicts()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("job.already", "AlreadyFunction", isPaused: false));
            catalog.Create(Schedule("job.conflict", "ConflictFunction", isPaused: false));
            // Bump job.conflict to version 3 while leaving it enabled, so an expectedVersion of 1 is stale.
            catalog.SetEnabled(JobId("job.conflict"), enabled: false, expectedVersion: 1);
            catalog.SetEnabled(JobId("job.conflict"), enabled: true, expectedVersion: 2);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var token = await ReadFormToken(client, "/");

            var response = await PostFormValues(client, "/jobs/actions/bulk/pause", token,
            [
                new("jobIds", JobId("job.already")), new("expectedVersions", "1"),
                new("jobIds", JobId("job.already")), new("expectedVersions", "1"),
                new("jobIds", JobId("job.conflict")), new("expectedVersions", "1"),
                new("jobIds", JobId("job.missing")), new("expectedVersions", "1")
            ]);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            // job.already paused exactly once (version 1 -> 2), despite the duplicate id.
            Assert.False(catalog.Get(JobId("job.already"))?.IsEnabled);
            Assert.Equal(2, catalog.Get(JobId("job.already"))?.CatalogVersion);
            // job.conflict left untouched because the submitted version was stale.
            Assert.True(catalog.Get(JobId("job.conflict"))?.IsEnabled);
            Assert.Equal(3, catalog.Get(JobId("job.conflict"))?.CatalogVersion);

            var dashboard = await client.GetStringAsync("/");
            Assert.Contains("bulk-summary-banner", dashboard);
            Assert.Contains("Paused 1 job(s).", dashboard);
            Assert.Contains("1 already in the requested state or no longer eligible.", dashboard);
            Assert.Contains("1 skipped because they changed since the page loaded. Review the refreshed jobs and try again.", dashboard);
        }

        [Fact]
        public async Task Dashboard_bulk_endpoints_require_post_and_csrf_token()
        {
            new SqliteJobCatalogRepository(sqlite).Create(Schedule("job.safe", "SafeFunction", isPaused: false));
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/jobs/actions/bulk/pause")).StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/jobs/actions/bulk/export")).StatusCode);

            var noToken = await client.PostAsync("/jobs/actions/bulk/pause", new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("jobIds", JobId("job.safe")),
                new KeyValuePair<string, string>("expectedVersions", "1")
            }));
            Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
            Assert.True(new SqliteJobCatalogRepository(sqlite).Get(JobId("job.safe"))?.IsEnabled);
        }

        [Fact]
        public async Task Dashboard_bulk_pause_with_no_selection_is_a_safe_noop()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("job.one", "OneFunction", isPaused: false));
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var token = await ReadFormToken(client, "/");

            var response = await PostFormValues(client, "/jobs/actions/bulk/pause", token, Array.Empty<KeyValuePair<string, string>>());

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.True(catalog.Get(JobId("job.one"))?.IsEnabled);
        }

        [Fact]
        public async Task Soft_deleted_management_page_is_the_only_bulk_hard_delete_selection_surface()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("job.active", "ActiveFunction", isPaused: false));
            var soft = catalog.Create(Schedule("job.soft", "SoftFunction", isPaused: false));
            var deleted = new SqliteJobLifecycleService(sqlite, catalog).SoftDelete(soft.JobId, soft.CatalogVersion, "web-test", "bulk");
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var page = await client.GetStringAsync("/jobs/soft-deleted");

            Assert.Contains("Manage soft-deleted jobs", page);
            Assert.Contains("Jobs must be soft-deleted from the dashboard", page);
            Assert.Contains("It does not delete data from Kusto.", page);
            Assert.Contains("data-bulk-bar", page);
            Assert.Contains("data-bulk-select-all data-bulk-section=\"managed-soft-deleted\"", page);
            Assert.Contains($"data-bulk-select value=\"{soft.JobId}\" data-expected-version=\"{deleted.CatalogVersion}\"", page);
            Assert.Contains("formaction=\"/jobs/soft-deleted/hard-delete\"", page);
            Assert.Contains("__RequestVerificationToken", page);
            Assert.Contains($"action=\"/jobs/{soft.JobId}/actions/restore\"", page);
            Assert.Contains($"href=\"/jobs/{soft.JobId}/hard-delete\"", page);
            Assert.DoesNotContain("job.active", page);
            Assert.DoesNotContain("formaction=\"/jobs/actions/bulk/pause\"", page);
            Assert.DoesNotContain("formaction=\"/jobs/actions/bulk/soft-delete\"", page);
        }

        [Fact]
        public async Task Soft_deleted_management_page_has_no_destructive_controls_when_empty()
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var page = await client.GetStringAsync("/jobs/soft-deleted");

            Assert.Contains("No jobs in this section.", page);
            Assert.DoesNotContain("data-bulk-bar", page);
            Assert.DoesNotContain("formaction=\"/jobs/soft-deleted/hard-delete\"", page);
            Assert.DoesNotContain("data-bulk-select-all", page);
        }

        [Fact]
        public async Task Soft_deleted_management_bulk_hard_delete_reviews_and_atomically_purges_selected_jobs()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var lifecycle = new SqliteJobLifecycleService(sqlite, catalog);
            var second = catalog.Create(Schedule("job.bulk.zulu", "ZuluFunction", isPaused: false));
            var first = catalog.Create(Schedule("job.bulk.alpha", "AlphaFunction", isPaused: false));
            var deletedSecond = lifecycle.SoftDelete(second.JobId, second.CatalogVersion, "web-test", "bulk");
            var deletedFirst = lifecycle.SoftDelete(first.JobId, first.CatalogVersion, "web-test", "bulk");
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var token = await ReadFormToken(client, "/jobs/soft-deleted");
            var selection = new[]
            {
                new KeyValuePair<string, string>("jobIds", second.JobId),
                new("expectedVersions", deletedSecond.CatalogVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("jobIds", first.JobId),
                new("expectedVersions", deletedFirst.CatalogVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
            };

            using var preview = await PostFormValues(client, "/jobs/soft-deleted/hard-delete", token, selection);
            var previewBody = await preview.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.Contains("Review permanent deletion", previewBody);
            Assert.Contains("DELETE 2 JOBS", previewBody);
            Assert.Contains(first.JobId, previewBody);
            Assert.Contains(second.JobId, previewBody);
            Assert.True(
                previewBody.IndexOf("job.bulk.alpha", StringComparison.Ordinal) <
                previewBody.IndexOf("job.bulk.zulu", StringComparison.Ordinal));

            using var execute = await PostFormValues(client, "/jobs/soft-deleted/hard-delete", token,
            [
                .. selection,
                new("execute", "true"),
                new("confirmation", "DELETE 2 JOBS"),
                new("reason", "web bulk purge")
            ]);
            var executeBody = await execute.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, execute.StatusCode);
            Assert.Contains("Bulk hard delete completed", executeBody);
            Assert.Contains("deleted 2 job row(s)", executeBody);
            Assert.Null(catalog.Get(first.JobId));
            Assert.Null(catalog.Get(second.JobId));
        }

        [Fact]
        public async Task Soft_deleted_management_bulk_hard_delete_rejects_stale_selection_without_partial_deletion()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var lifecycle = new SqliteJobLifecycleService(sqlite, catalog);
            var eligible = catalog.Create(Schedule("job.bulk.eligible", "EligibleFunction", isPaused: false));
            var changed = catalog.Create(Schedule("job.bulk.changed", "ChangedFunction", isPaused: false));
            var deletedEligible = lifecycle.SoftDelete(eligible.JobId, eligible.CatalogVersion, "web-test", "bulk");
            var deletedChanged = lifecycle.SoftDelete(changed.JobId, changed.CatalogVersion, "web-test", "bulk");
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var token = await ReadFormToken(client, "/jobs/soft-deleted");

            lifecycle.Restore(changed.JobId, deletedChanged.CatalogVersion, "web-test", "changed after selection");
            using var response = await PostFormValues(client, "/jobs/soft-deleted/hard-delete", token,
            [
                new("jobIds", eligible.JobId),
                new("expectedVersions", deletedEligible.CatalogVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("jobIds", changed.JobId),
                new("expectedVersions", deletedChanged.CatalogVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("execute", "true"),
                new("confirmation", "DELETE 2 JOBS"),
                new("reason", "must not partially purge")
            ]);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("No jobs were deleted", body);
            Assert.Contains("changed since it was selected", body);
            Assert.Contains("no longer soft-deleted", body);
            Assert.NotNull(catalog.Get(eligible.JobId));
            Assert.NotNull(catalog.Get(changed.JobId));
        }

        [Fact]
        public async Task Soft_deleted_management_bulk_hard_delete_requires_post_selection_csrf_and_exact_confirmation()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var created = catalog.Create(Schedule("job.bulk.safe", "SafeFunction", isPaused: false));
            var deleted = new SqliteJobLifecycleService(sqlite, catalog).SoftDelete(created.JobId, created.CatalogVersion, "web-test", "bulk");
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/jobs/soft-deleted/hard-delete")).StatusCode);
            using var noToken = await client.PostAsync(
                "/jobs/soft-deleted/hard-delete",
                new FormUrlEncodedContent(
                [
                    new KeyValuePair<string, string>("jobIds", created.JobId),
                    new("expectedVersions", deleted.CatalogVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
                ]));
            Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);

            var token = await ReadFormToken(client, "/jobs/soft-deleted");
            using var wrongConfirmation = await PostFormValues(client, "/jobs/soft-deleted/hard-delete", token,
            [
                new("jobIds", created.JobId),
                new("expectedVersions", deleted.CatalogVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("execute", "true"),
                new("confirmation", "DELETE SELECTED JOBS")
            ]);
            var body = await wrongConfirmation.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, wrongConfirmation.StatusCode);
            Assert.Contains("must exactly match &#x27;DELETE 1 JOB&#x27;", body);
            Assert.NotNull(catalog.Get(created.JobId));
        }

        [Fact]
        public async Task Import_page_rejects_invalid_array_without_persisting_valid_items()
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var token = await ReadFormToken(client, "/jobs/import");

            var response = await PostForm(client, "/jobs/import", token, new Dictionary<string, string>
            {
                ["importSource"] = "paste",
                ["scheduleJson"] = "[" + Schedule("job.valid", "ValidFunction", isPaused: false) + "," + Schedule("job.bad", "BadFunction", isPaused: false).Replace("\"outputTable\": \"Output\",", "\"unknownField\": true,", StringComparison.Ordinal) + "]"
            });
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("[1].unknownField", body);
            Assert.Contains("[1].outputTable", body);
            Assert.Contains("<form method=\"post\" action=\"/jobs/import\"", body);
            Assert.Null(new SqliteJobCatalogRepository(sqlite).Get(JobId("job.valid")));
            Assert.Null(new SqliteJobCatalogRepository(sqlite).Get(JobId("job.bad")));
        }

        [Fact]
        public async Task Import_page_rejects_a_missing_file_without_a_server_error()
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var token = await ReadFormToken(client, "/jobs/import");

            var response = await PostForm(client, "/jobs/import", token, new Dictionary<string, string>
            {
                ["importSource"] = "file"
            });
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Choose a JSON file to import.", body);
        }

        [Fact]
        public async Task Rerun_pages_preview_create_and_execute_slice_reset()
        {
            SeedOperationalData();
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var slice = await client.GetStringAsync($"/jobs/{JobId("job.web")}/slices?start=2026-01-01T00%3A00%3A00Z&end=2026-01-01T00%3A05%3A00Z");
            var previewPath = $"/jobs/{JobId("job.web")}/rerun?start=2026-01-01T00%3A00&end=2026-01-01T00%3A05&reason=web%20rerun";
            var preview = await client.GetStringAsync(previewPath);
            var createToken = await ReadFormToken(client, previewPath);

            Assert.Contains("Repair or rerun this slice", slice);
            Assert.Contains("Repair or rerun: job.web", preview);
            Assert.Contains("value=\"rerun\" checked", preview);
            Assert.Contains("Rerun whole slice", preview);
            Assert.Contains(".delete table Output records &lt;|", preview);
            Assert.Contains("StartTime &lt; datetime(2026-01-01T00:05:00.0000000Z)", preview);
            Assert.Contains("Create rerun batch", preview);

            var create = await PostForm(client, $"/jobs/{JobId("job.web")}/rerun", createToken, new Dictionary<string, string>
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
            Assert.Equal(DurableSliceStatus.Missing, state.Get(JobId("job.web"), At(0), At(5)).Status);

            var completedBatch = await client.GetStringAsync(rerunPath);
            var resetSlice = await client.GetStringAsync($"/jobs/{JobId("job.web")}/slices?start=2026-01-01T00%3A00%3A00Z&end=2026-01-01T00%3A05%3A00Z");
            Assert.Contains("Archived previous local details", completedBatch);
            Assert.Contains("attempt-s0", completedBatch);
            Assert.Contains("No attempts recorded.", resetSlice);
        }

        [Fact]
        public async Task Failed_chunked_slice_defaults_to_repair_and_queues_all_terminal_chunks()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var record = catalog.Create(Schedule("job.web.chunk-repair", "ChunkRepairFunction", isPaused: false, chunks: 3));
            var slice = new SliceRange(record.JobId, At(0), At(5));
            var chunks = new SqliteChunkStateRepository(sqlite);
            foreach (var child in chunks.EnsureWindow(slice, 3, "test"))
            {
                chunks.MarkQueued($"queued-{child.ChunkId}", child.Execution, actor: "test");
                var lease = chunks.AcquireLease($"lease-{child.ChunkId}", child.Execution, $"worker-{child.ChunkId}", TimeSpan.FromMinutes(5), At(10))!;
                if (child.ChunkId == 0)
                {
                    chunks.CompleteLease("complete-0", child.Execution, "worker-0", lease.LeaseToken!, At(11));
                }
                else
                {
                    chunks.DeadLetterLease($"dead-{child.ChunkId}", child.Execution, $"worker-{child.ChunkId}", lease.LeaseToken!, At(11), $"chunk {child.ChunkId} failed", "Permanent");
                }
            }

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var slicePath = $"/jobs/{record.JobId}/slices?start=2026-01-01T00%3A00%3A00Z&end=2026-01-01T00%3A05%3A00Z";
            var combinedPath = $"/jobs/{record.JobId}/rerun?start=2026-01-01T00%3A00&end=2026-01-01T00%3A05";
            var sliceHtml = await client.GetStringAsync(slicePath);
            var previewHtml = await client.GetStringAsync(combinedPath);
            var token = await ReadFormToken(client, combinedPath);
            var preview = new SqliteRepairService(
                sqlite,
                catalog,
                new SqliteSliceStateRepository(sqlite),
                new SqliteWorkQueueRepository(sqlite),
                SystemClock.Instance,
                chunks).Preview(new RepairPlanRequest(
                    record.JobId,
                    slice.StartUtc,
                    slice.EndUtc,
                    "local-web",
                    "Repair failed chunks",
                    Scope: RepairSliceScope.FailedAndDeadLetteredOnly));

            Assert.Contains("Repair or rerun this slice", sliceHtml, StringComparison.Ordinal);
            Assert.Contains("Repair or rerun: job.web.chunk-repair", previewHtml, StringComparison.Ordinal);
            Assert.Contains("value=\"repair\" checked", previewHtml, StringComparison.Ordinal);
            Assert.Contains("Repair failed chunks", previewHtml, StringComparison.Ordinal);
            Assert.Contains("Chunk 1/3", previewHtml, StringComparison.Ordinal);
            Assert.Contains("Chunk 2/3", previewHtml, StringComparison.Ordinal);
            Assert.Contains("No Kusto cleanup is required", previewHtml, StringComparison.Ordinal);
            Assert.Contains("Rerun whole slice", previewHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("Suggested Kusto cleanup", previewHtml, StringComparison.Ordinal);

            var response = await PostForm(client, $"/jobs/{record.JobId}/rerun", token, new Dictionary<string, string>
            {
                ["mode"] = "repair",
                ["start"] = "2026-01-01T00:00",
                ["end"] = "2026-01-01T00:05",
                ["requestedBy"] = "web-test",
                ["reason"] = "Repair failed chunks",
                ["expectedSliceCount"] = preview.Repairable.ToString(),
                ["expectedExecutionCount"] = preview.RepairableExecutions.ToString(),
                ["previewToken"] = preview.PreviewToken
            });

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith($"/jobs/{record.JobId}/slices?", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
            var repairedHtml = await client.GetStringAsync(response.Headers.Location!.OriginalString);
            Assert.Contains("queued chunk ID(s) 1, 2", repairedHtml, StringComparison.Ordinal);
            var repairRows = new SqliteWorkQueueRepository(sqlite).List(record.JobId)
                .Where(item => item.IdempotencyKey.StartsWith("repair|", StringComparison.Ordinal))
                .OrderBy(item => item.ChunkId)
                .ToArray();
            Assert.Equal([1, 2], repairRows.Select(item => item.ChunkId).ToArray());
            Assert.Equal(DurableSliceStatus.Completed, chunks.Get(SliceExecutionUnit.Chunk(slice, 0, 3))!.Status);
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
            while (DateTimeOffset.UtcNow < deadline && state.Get(JobId("job.runner"), At(0), At(5))?.Status != DurableSliceStatus.Completed)
            {
                await Task.Delay(50);
            }

            Assert.Equal(DurableSliceStatus.Completed, state.Get(JobId("job.runner"), At(0), At(5)).Status);

            var history = await client.GetStringAsync($"/jobs/{JobId("job.runner")}/history?from=2026-01-01T00%3A00%3A00Z&to=2026-01-01T00%3A05%3A00Z");
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

            var health = await client.GetStringAsync("/api/v1/system/status");
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
                    () => state.Get(JobId("job.fast.refill"), At(5), At(10)).Status == DurableSliceStatus.Completed,
                    TimeSpan.FromSeconds(5));

                Assert.Equal(DurableSliceStatus.Running, state.Get(JobId("job.slow.blocking"), At(0), At(5)).Status);
                Assert.Equal(DurableSliceStatus.Completed, state.Get(JobId("job.fast.refill"), At(0), At(5)).Status);
                Assert.Equal(DurableSliceStatus.Completed, state.Get(JobId("job.fast.refill"), At(5), At(10)).Status);
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
            Assert.Equal(DurableSliceStatus.Running, state.Get(JobId("job.parallel"), At(0), At(5)).Status);
            Assert.Equal(DurableSliceStatus.Running, state.Get(JobId("job.parallel"), At(5), At(10)).Status);

            var runningHistory = await client.GetStringAsync($"/jobs/{JobId("job.parallel")}/history?from=2026-01-01T00%3A00%3A00Z&to=2026-01-01T00%3A10%3A00Z");
            Assert.Equal(2, Regex.Matches(runningHistory, "class=\"cell running\"").Count);

            executor.Release();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline &&
                   (state.Get(JobId("job.parallel"), At(0), At(5)).Status != DurableSliceStatus.Completed ||
                    state.Get(JobId("job.parallel"), At(5), At(10)).Status != DurableSliceStatus.Completed))
            {
                await Task.Delay(50);
            }

            Assert.Equal(DurableSliceStatus.Completed, state.Get(JobId("job.parallel"), At(0), At(5)).Status);
            Assert.Equal(DurableSliceStatus.Completed, state.Get(JobId("job.parallel"), At(5), At(10)).Status);
        }

        [Fact]
        public async Task Unbounded_worker_pool_starts_all_32_chunks_when_job_parallelism_allows_it()
        {
            new SqliteJobCatalogRepository(sqlite).Create(
                Schedule("job.chunks.unbounded32", "ChunkFunction", isPaused: false, maxParallelism: 32, chunks: 32));
            var executor = new BlockingSliceOutputExecutor(expectedStarts: 32);
            using var runFactory = CreateFactory(
                enableScheduler: true,
                tickInterval: "00:00:00.050",
                configureServices: services =>
                {
                    services.AddSingleton(executor);
                    services.AddScoped<ILocalSliceOutputExecutor>(sp => sp.GetRequiredService<BlockingSliceOutputExecutor>());
                });
            using var client = runFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var slice = new SliceRange(JobId("job.chunks.unbounded32"), At(0), At(5));

            try
            {
                await client.GetStringAsync("/");
                await executor.WaitForStartsAsync(TimeSpan.FromSeconds(15));
                await Task.Delay(250);

                Assert.Equal(32, executor.StartedCount);
                Assert.Equal(
                    32,
                    new SqliteChunkStateRepository(sqlite).List(slice)
                        .Count(chunk => chunk.Status == DurableSliceStatus.Running));
                var work = new SqliteWorkQueueRepository(sqlite).List(JobId("job.chunks.unbounded32"));
                Assert.Equal(32, work.Count(item => item.State == DurableWorkQueueState.Leased));
                Assert.DoesNotContain(work, item => item.State == DurableWorkQueueState.Queued);

                using var health = JsonDocument.Parse(await client.GetStringAsync("/api/v1/system/status"));
                var workerPool = health.RootElement.GetProperty("workerPool");
                Assert.Equal(JsonValueKind.Null, workerPool.GetProperty("maxConcurrency").ValueKind);
                Assert.Equal("Unbounded", workerPool.GetProperty("maxConcurrencyDisplay").GetString());
                Assert.Equal("Default", workerPool.GetProperty("maxConcurrencySource").GetString());
                Assert.Equal(100, workerPool.GetProperty("maxDispatchStartsPerCycle").GetInt32());
                Assert.Equal(32, workerPool.GetProperty("activeWorkerCount").GetInt32());
                Assert.Equal(32, workerPool.GetProperty("starts").GetInt64());
            }
            finally
            {
                executor.Release();
            }

            await WaitUntilAsync(
                () => new SqliteSliceStateRepository(sqlite)
                    .Get(JobId("job.chunks.unbounded32"), At(0), At(5)).Status == DurableSliceStatus.Completed,
                TimeSpan.FromSeconds(15));
        }

        [Fact]
        public async Task Configured_global_worker_cap_limits_chunk_concurrency_below_job_parallelism()
        {
            new SqliteJobCatalogRepository(sqlite).Create(
                Schedule("job.chunks.global8", "ChunkFunction", isPaused: false, maxParallelism: 32, chunks: 32));
            var executor = new BlockingSliceOutputExecutor(expectedStarts: 8);
            using var runFactory = CreateFactory(
                enableScheduler: true,
                tickInterval: "00:00:00.050",
                workerPoolMaxConcurrency: 8,
                configureServices: services =>
                {
                    services.AddSingleton(executor);
                    services.AddScoped<ILocalSliceOutputExecutor>(sp => sp.GetRequiredService<BlockingSliceOutputExecutor>());
                });
            using var client = runFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var slice = new SliceRange(JobId("job.chunks.global8"), At(0), At(5));

            try
            {
                await client.GetStringAsync("/");
                await executor.WaitForStartsAsync(TimeSpan.FromSeconds(10));
                await Task.Delay(250);

                Assert.Equal(8, executor.StartedCount);
                var chunks = new SqliteChunkStateRepository(sqlite).List(slice);
                Assert.Equal(8, chunks.Count(chunk => chunk.Status == DurableSliceStatus.Running));
                Assert.Equal(24, chunks.Count(chunk => chunk.Status == DurableSliceStatus.Queued));
                var work = new SqliteWorkQueueRepository(sqlite).List(JobId("job.chunks.global8"));
                Assert.Equal(8, work.Count(item => item.State == DurableWorkQueueState.Leased));
                Assert.Equal(24, work.Count(item => item.State == DurableWorkQueueState.Queued));

                using var health = JsonDocument.Parse(await client.GetStringAsync("/api/v1/system/status"));
                var workerPool = health.RootElement.GetProperty("workerPool");
                Assert.Equal(8, workerPool.GetProperty("maxConcurrency").GetInt32());
                Assert.Equal("KoLite:WorkerPool:MaxConcurrency", workerPool.GetProperty("maxConcurrencySource").GetString());
                Assert.Equal(8, workerPool.GetProperty("activeWorkerCount").GetInt32());
                Assert.Equal(0, workerPool.GetProperty("availableSlots").GetInt32());
            }
            finally
            {
                executor.Release();
            }

            await WaitUntilAsync(
                () => new SqliteSliceStateRepository(sqlite)
                    .Get(JobId("job.chunks.global8"), At(0), At(5)).Status == DurableSliceStatus.Completed,
                TimeSpan.FromSeconds(15));
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
                state.Get(JobId("job.global.one"), At(0), At(5)).Status,
                state.Get(JobId("job.global.one"), At(5), At(10)).Status,
                state.Get(JobId("job.global.two"), At(0), At(5)).Status,
                state.Get(JobId("job.global.two"), At(5), At(10)).Status
            }.Count(status => status == DurableSliceStatus.Running);
            Assert.Equal(2, executor.StartedCount);
            Assert.Equal(2, runningCount);

            var workerIds = StartedWorkerIds();
            Assert.Equal(2, workerIds.Count);
            Assert.Equal(2, workerIds.Distinct(StringComparer.Ordinal).Count());
            Assert.All(workerIds, workerId => Assert.StartsWith("local-web-worker-", workerId, StringComparison.Ordinal));

            var health = await client.GetStringAsync("/api/v1/system/status");
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
                    state.Get(JobId("job.global.one"), At(0), At(5)).Status,
                    state.Get(JobId("job.global.one"), At(5), At(10)).Status,
                    state.Get(JobId("job.global.two"), At(0), At(5)).Status,
                    state.Get(JobId("job.global.two"), At(5), At(10)).Status
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
            using var drain = await client.PostAsJsonAsync("/control/v1/shutdown/drain", new { reason = "active-worker-test" });
            drain.EnsureSuccessStatusCode();

            await Task.Delay(250);
            Assert.Equal(1, executor.StartedCount);
            Assert.False(executor.CancellationWasRequestedBeforeRelease);

            executor.Release();
            await WaitUntilAsync(
                () => state.Get(JobId("job.drain"), At(0), At(5)).Status == DurableSliceStatus.Completed,
                TimeSpan.FromSeconds(5));

            Assert.Equal(1, executor.StartedCount);
            Assert.False(executor.CancellationWasRequestedBeforeRelease);
            Assert.NotEqual(DurableSliceStatus.Completed, state.Get(JobId("job.drain"), At(5), At(10)).Status);
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
            using var drain = await client.PostAsJsonAsync("/control/v1/shutdown/drain", new { reason = "retry-test" });
            drain.EnsureSuccessStatusCode();

            executor.Release();
            await WaitUntilAsync(
                () => state.Get(JobId("job.drain.retry"), At(0), At(5)).Status == DurableSliceStatus.Failed,
                TimeSpan.FromSeconds(5));
            await Task.Delay(250);

            Assert.Equal(1, executor.StartedCount);
            var item = Assert.Single(queue.List(JobId("job.drain.retry")));
            Assert.Equal(DurableWorkQueueState.Queued, item.State);
            Assert.Equal(1, item.Attempts);
        }

        [Fact]
        public async Task Background_scheduler_does_not_start_paused_queued_work()
        {
            new SqliteJobCatalogRepository(sqlite).Create(Schedule("job.concurrent.paused", "PausedFunction", isPaused: true));
            var state = new SqliteSliceStateRepository(sqlite);
            state.Append("paused-concurrent-queued", JobId("job.concurrent.paused"), At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
            var queue = new SqliteWorkQueueRepository(sqlite);
            var item = queue.Enqueue(JobId("job.concurrent.paused"), At(0), At(5), "paused-concurrent-work", At(0));
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
            var token = await ReadFormToken(client, "/jobs/new");

            var response = await PostForm(client, "/jobs/new", token, new Dictionary<string, string>
            {
                ["scheduleJson"] = Schedule("job.bad", "BadFunction", isPaused: false).Replace("\"outputTable\": \"Output\"", "\"unknownField\": true,\n  \"outputTable\": \"Output\"", StringComparison.Ordinal)
            });
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("Schedule JSON is invalid", body);
            Assert.Contains("unknownField", body);
            Assert.Contains("<form method=\"post\" action=\"/jobs/new\">", body);
            Assert.Contains("<textarea name=\"scheduleJson\"", body);
            Assert.Null(new SqliteJobCatalogRepository(sqlite).Get(JobId("job.bad")));
        }

        [Fact]
        public async Task Malformed_schedule_json_is_rejected_with_html_form_response()
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var token = await ReadFormToken(client, "/jobs/new");

            var response = await PostForm(client, "/jobs/new", token, new Dictionary<string, string>
            {
                ["scheduleJson"] = "{ bad json"
            });
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("Schedule JSON is invalid", body);
            Assert.Contains("<form method=\"post\" action=\"/jobs/new\">", body);
            Assert.Contains("<textarea name=\"scheduleJson\"", body);
        }

        [Fact]
        public async Task Catalog_mutations_require_post_and_csrf_token()
        {
            new SqliteJobCatalogRepository(sqlite).Create(Schedule("job.safe", "SafeFunction", isPaused: false));
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/catalog/create")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/catalog/{JobId("job.safe")}/update")).StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync($"/jobs/{JobId("job.safe")}/actions/resume")).StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync($"/jobs/{JobId("job.safe")}/actions/pause")).StatusCode);

            var noToken = await client.PostAsync($"/jobs/{JobId("job.safe")}/actions/pause", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["expectedVersion"] = "1"
            }));
            Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
            Assert.True(new SqliteJobCatalogRepository(sqlite).Get(JobId("job.safe"))?.IsEnabled);
        }

        [Fact]
        public async Task Canonical_create_and_edit_pages_self_post()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var createToken = await ReadFormToken(client, "/jobs/new");
            var create = await PostForm(client, "/jobs/new", createToken, new Dictionary<string, string>
            {
                ["scheduleJson"] = Schedule("job.alias.create", "AliasCreateFunction", isPaused: false)
            });
            Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
            Assert.NotNull(catalog.Get(JobId("job.alias.create")));

            catalog.Create(Schedule("job.alias.update", "AliasUpdateFunction", isPaused: false));
            var editToken = await ReadFormToken(client, $"/jobs/{JobId("job.alias.update")}/edit");
            var update = await PostForm(client, $"/jobs/{JobId("job.alias.update")}/edit", editToken, new Dictionary<string, string>
            {
                ["expectedVersion"] = "1",
                ["scheduleJson"] = Schedule("job.alias.update", "AliasUpdateFunctionV2", isPaused: false)
            });
            Assert.Equal(HttpStatusCode.Redirect, update.StatusCode);
            Assert.Equal("AliasUpdateFunctionV2", catalog.Get(JobId("job.alias.update"))?.QueryRef);
        }

        public void Dispose()
        {
            factory.Dispose();
            TestCleanup.DeleteDirectoryBestEffort(testDirectory);
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

        [Fact]
        public async Task Slice_detail_recovers_an_orphaned_lease_for_an_enabled_job()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var state = new SqliteSliceStateRepository(sqlite);
            var queue = new SqliteWorkQueueRepository(sqlite);
            catalog.Create(Schedule("job.orphan.web", "OrphanWebFunction", isPaused: false));
            // Running slice plus a Leased queue row whose lock is far in the past (orphaned vs real now).
            state.AcquireLease("orphan-web-lease", JobId("job.orphan.web"), At(0), At(5), "stale-worker", TimeSpan.FromMinutes(5), At(10));
            queue.Enqueue(JobId("job.orphan.web"), At(0), At(5), "normal|orphan-web", At(0));
            Assert.NotNull(queue.Claim("default", "stale-worker", TimeSpan.FromMinutes(5), At(10)));

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var slicePath = $"/jobs/{JobId("job.orphan.web")}/slices?start=2026-01-01T00%3A00%3A00Z&end=2026-01-01T00%3A05%3A00Z";

            var token = await ReadFormToken(client, slicePath);
            var html = await client.GetStringAsync(slicePath);
            Assert.Contains("Orphaned lease", html, StringComparison.Ordinal);
            Assert.Contains("Recover expired lease (re-queue)", html, StringComparison.Ordinal);

            using var recover = await PostForm(client, $"/jobs/{JobId("job.orphan.web")}/slices?handler=Recover", token, new Dictionary<string, string>
            {
                ["start"] = "2026-01-01T00:00:00Z",
                ["end"] = "2026-01-01T00:05:00Z"
            });

            Assert.Equal(HttpStatusCode.OK, recover.StatusCode);
            var recoverHtml = await recover.Content.ReadAsStringAsync();
            Assert.Contains("re-queued", recoverHtml, StringComparison.Ordinal);
            var item = Assert.Single(queue.List(JobId("job.orphan.web")));
            Assert.Equal(DurableWorkQueueState.Queued, item.State);
            Assert.Equal(DurableSliceStatus.Queued, state.Get(JobId("job.orphan.web"), At(0), At(5)).Status);
        }

        [Fact]
        public async Task Slice_detail_warns_without_recover_button_for_a_paused_job()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var state = new SqliteSliceStateRepository(sqlite);
            var queue = new SqliteWorkQueueRepository(sqlite);
            var created = catalog.Create(Schedule("job.orphan.paused.web", "OrphanPausedFunction", isPaused: false));
            state.AcquireLease("orphan-paused-lease", JobId("job.orphan.paused.web"), At(0), At(5), "stale-worker", TimeSpan.FromMinutes(5), At(10));
            queue.Enqueue(JobId("job.orphan.paused.web"), At(0), At(5), "normal|orphan-paused", At(0));
            Assert.NotNull(queue.Claim("default", "stale-worker", TimeSpan.FromMinutes(5), At(10)));
            catalog.SetEnabled(JobId("job.orphan.paused.web"), enabled: false, expectedVersion: created.CatalogVersion);

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var html = await client.GetStringAsync($"/jobs/{JobId("job.orphan.paused.web")}/slices?start=2026-01-01T00%3A00%3A00Z&end=2026-01-01T00%3A05%3A00Z");

            Assert.Contains("Orphaned lease", html, StringComparison.Ordinal);
            Assert.Contains("paused or deleted", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Recover expired lease (re-queue)", html, StringComparison.Ordinal);
        }

        [Fact]
        public void Job_details_grid_marks_an_orphaned_expired_lease_as_stalled()
        {
            var clock = new ManualClock(At(20));
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var state = new SqliteSliceStateRepository(sqlite);
            var queue = new SqliteWorkQueueRepository(sqlite);
            var readModels = new SqliteOperationalReadModelRepository(sqlite);
            catalog.Create(Schedule("job.stalled", "StalledFunction", isPaused: false));
            state.AcquireLease("stalled-lease", JobId("job.stalled"), At(0), At(5), "stale-worker", TimeSpan.FromMinutes(5), At(10));
            queue.Enqueue(JobId("job.stalled"), At(0), At(5), "normal|stalled", At(0));
            Assert.NotNull(queue.Claim("default", "stale-worker", TimeSpan.FromMinutes(5), At(10)));

            var query = new JobDetailsPageQuery(catalog, readModels, queue, new LifecycleReadModel(new SqliteLifecycleReadModelRepository(sqlite)), new OperationalDetailsReadModel(new SqliteOperationalReadModelRepository(sqlite)), clock);
            var data = query.Get(JobId("job.stalled"));

            Assert.NotNull(data);
            var cell = data!.SliceHistory.SelectMany(r => r.Cells).Single(c => c.SliceStartUtc == At(0));
            Assert.Equal("Stalled", cell.State);
            Assert.Equal("stalled", cell.CssClass);
            Assert.Equal("Stalled (orphaned lease)", cell.StatusLabel);
        }

        [Fact]
        public async Task History_legend_shows_the_stalled_key_when_a_slice_is_stalled()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var state = new SqliteSliceStateRepository(sqlite);
            var queue = new SqliteWorkQueueRepository(sqlite);
            catalog.Create(Schedule("job.legend.stalled", "LegendStalledFunction", isPaused: false));
            // A Leased queue row whose lock is far in the past renders the slice as Stalled (orphaned lease).
            state.AcquireLease("legend-stalled-lease", JobId("job.legend.stalled"), At(0), At(5), "stale-worker", TimeSpan.FromMinutes(5), At(10));
            queue.Enqueue(JobId("job.legend.stalled"), At(0), At(5), "normal|legend-stalled", At(0));
            Assert.NotNull(queue.Claim("default", "stale-worker", TimeSpan.FromMinutes(5), At(10)));

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var html = await client.GetStringAsync($"/jobs/{JobId("job.legend.stalled")}/history?from=2026-01-01T00%3A00%3A00Z&to=2026-01-01T00%3A05%3A00Z");

            // The conditional legend key is rendered only when a slice in the window is stalled.
            Assert.Contains("legend-color stalled", html, StringComparison.Ordinal);
            Assert.Contains("Stalled (orphaned lease)", html, StringComparison.Ordinal);
        }

        [Fact]
        public async Task History_legend_hides_the_stalled_key_when_no_slice_is_stalled()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var state = new SqliteSliceStateRepository(sqlite);
            catalog.Create(Schedule("job.legend.healthy", "LegendHealthyFunction", isPaused: false));
            state.Append("legend-healthy-s0", JobId("job.legend.healthy"), At(0), At(5), DurableSliceStatus.Completed, expectedVersion: 0);

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var html = await client.GetStringAsync($"/jobs/{JobId("job.legend.healthy")}/history?from=2026-01-01T00%3A00%3A00Z&to=2026-01-01T00%3A05%3A00Z");

            // The legend still renders its static keys, but the Stalled key is omitted when nothing is stalled.
            Assert.Contains("legend-color running", html, StringComparison.Ordinal);
            Assert.DoesNotContain("legend-color stalled", html, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Activity_page_renders_running_now_totals_and_throughput_chart()
        {
            var jobId = JobId("activity.web");
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var state = new SqliteSliceStateRepository(sqlite);
            var readModels = new SqliteOperationalReadModelRepository(sqlite);
            catalog.Create(Schedule("activity.web", "ActivityFunction", isPaused: false));

            var now = DateTimeOffset.UtcNow;
            var currentHour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
            state.Append("activity-a", jobId, At(0), At(5), DurableSliceStatus.Completed, expectedVersion: 0);
            state.Append("activity-b", jobId, At(5), At(10), DurableSliceStatus.Completed, expectedVersion: 0);
            state.Append("activity-c", jobId, At(10), At(15), DurableSliceStatus.Failed, expectedVersion: 0, reason: "boom");
            state.Append("activity-d", jobId, At(15), At(20), DurableSliceStatus.DeadLettered, expectedVersion: 0, reason: "dead");
            state.Append("activity-e", jobId, At(20), At(25), DurableSliceStatus.Running, expectedVersion: 0);
            state.Append("activity-f", jobId, At(25), At(30), DurableSliceStatus.Queued, expectedVersion: 0);
            readModels.RecordAttempt("activity-a-att", jobId, At(0), At(5), 1, "Succeeded", "worker", currentHour.AddMinutes(-6), currentHour.AddMinutes(-5));
            readModels.RecordAttempt("activity-b-att", jobId, At(5), At(10), 1, "Succeeded", "worker", currentHour.AddMinutes(-11), currentHour.AddMinutes(-10));
            readModels.RecordAttempt("activity-c-att", jobId, At(10), At(15), 1, "Failed", "worker", currentHour.AddMinutes(-16), currentHour.AddMinutes(-15));
            readModels.RecordAttempt("activity-d-att", jobId, At(15), At(20), 1, "DeadLettered", "worker", currentHour.AddMinutes(-21), currentHour.AddMinutes(-20));

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            using var response = await client.GetAsync("/activity");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await response.Content.ReadAsStringAsync();

            // Nav link is rendered on every page.
            Assert.Contains("href=\"/activity\"", page);
            // Running-now section surfaces the one Running slice and the queued count.
            Assert.Contains("Running now", page);
            Assert.Contains("activity.web", page);
            Assert.Contains("Logical slices running", page);
            Assert.Contains("Logical slices queued", page);
            Assert.Contains("Executions running", page);
            Assert.Contains("Executions queued/retry-pending", page);
            // Running-now table surfaces when each running slice started and its projected finish.
            Assert.Contains("<th>Progress</th>", page);
            Assert.Contains("<th>Running executions</th>", page);
            Assert.Contains("<th>Highest attempt</th>", page);
            Assert.Contains("<th>Started (UTC)</th>", page);
            Assert.Contains("<th>ETA (UTC)</th>", page);
            Assert.Contains("2026-01-01T00:20:00Z &ndash; 2026-01-01T00:25:00Z", page);
            // Processed execution totals: these unchunked slices each contribute one execution.
            Assert.Contains("Executions processed", page);
            Assert.Contains("Each execution unit contributes its latest succeeded or failed/dead-lettered outcome once.", page);
            Assert.Contains("Last day", page);
            Assert.Contains("Last 7 days", page);
            Assert.Contains("Last 30 days", page);
            Assert.Contains("All time", page);
            Assert.Contains("2 succeeded", page);
            Assert.Contains("2 failed", page);
            // Throughput chart hook + payload are present once there is data.
            Assert.Contains("Executions processed over time", page);
            Assert.Contains("aria-label=\"Executions processed over time\"", page);
            Assert.Contains("data-chartjs-activity=\"executions-processed-chart\"", page);
        }

        [Fact]
        public async Task Activity_page_lists_each_running_chunk_and_worker_in_one_logical_row()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var job = catalog.Create(Schedule("activity.web.chunks", "ActivityChunkFunction", isPaused: false, maxParallelism: 4, chunks: 4));
            var slice = new SliceRange(job.JobId, At(0), At(5));
            var chunks = new SqliteChunkStateRepository(sqlite);
            var readModels = new SqliteOperationalReadModelRepository(sqlite);
            var children = chunks.EnsureWindow(slice, 4, "test");
            foreach (var child in children)
            {
                chunks.MarkQueued($"queued-{child.ChunkId}", child.Execution, actor: "test");
                var worker = $"activity-worker-{child.ChunkId}";
                chunks.AcquireLease($"lease-{child.ChunkId}", child.Execution, worker, TimeSpan.FromMinutes(30), DateTimeOffset.UtcNow);
                readModels.RecordAttempt(
                    $"activity-chunk-{child.ChunkId}",
                    job.JobId,
                    slice.StartUtc,
                    slice.EndUtc,
                    1,
                    "Started",
                    worker,
                    DateTimeOffset.UtcNow.AddMinutes(-child.ChunkId - 1),
                    completedAtUtc: null,
                    chunkId: child.ChunkId,
                    totalChunks: child.TotalChunks);
            }

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var page = await client.GetStringAsync("/activity");

            Assert.Contains($"data-activity-job-id=\"{job.JobId}\"", page, StringComparison.Ordinal);
            Assert.Contains($"data-chunk-progress=\"{job.JobId}\">0/4 completed", page, StringComparison.Ordinal);
            Assert.Equal(4, Regex.Matches(page, "data-running-chunk-id=\"").Count);
            for (var chunkId = 0; chunkId < 4; chunkId++)
            {
                Assert.Contains($"data-running-chunk-id=\"{chunkId}\"", page, StringComparison.Ordinal);
                Assert.Contains($"Chunk {chunkId}/4", page, StringComparison.Ordinal);
                Assert.Contains($"activity-worker-{chunkId}", page, StringComparison.Ordinal);
            }
        }

        private void SeedOperationalData()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var state = new SqliteSliceStateRepository(sqlite);
            var queue = new SqliteWorkQueueRepository(sqlite);
            var readModels = new SqliteOperationalReadModelRepository(sqlite);

            catalog.Create(Schedule("job.web", "WebFunction", isPaused: false, folder: "Web Folder"));
            state.Append("s0", JobId("job.web"), At(0), At(5), DurableSliceStatus.Completed, expectedVersion: 0);
            state.Append("s1", JobId("job.web"), At(5), At(10), DurableSliceStatus.Failed, expectedVersion: 0, reason: "boom");
            state.Append("s2", JobId("job.web"), At(10), At(15), DurableSliceStatus.Queued, expectedVersion: 0);
            state.AcquireLease("s3", JobId("job.web"), At(15), At(20), "worker", TimeSpan.FromMinutes(5), At(16));
            state.Append("s5", JobId("job.web"), At(25), At(30), DurableSliceStatus.Failed, expectedVersion: 0, reason: "terminal boom");
            state.Append("s6", JobId("job.web"), At(30), At(35), DurableSliceStatus.Completed, expectedVersion: 0);
            state.Append("s7", JobId("job.web"), At(35), At(40), DurableSliceStatus.DependencyBlocked, expectedVersion: 0, reason: "upstream");
            queue.Enqueue(JobId("job.web"), At(5), At(10), "queue-s1", At(0));
            queue.Enqueue(JobId("job.web"), At(10), At(15), "queue-s2", At(0));
            readModels.RecordAttempt("attempt-s0", JobId("job.web"), At(0), At(5), 1, "Succeeded", "worker", At(0), At(1));
            readModels.RecordAttempt("attempt-s6-1", JobId("job.web"), At(30), At(35), 1, "FailedRetryable", "worker", At(30), At(31));
            readModels.RecordAttempt("attempt-s6-2", JobId("job.web"), At(30), At(35), 2, "Succeeded", "worker", At(32), At(33));

            catalog.Create(Schedule("job.hourly", "HourlyFunction", isPaused: false, queryWindowSize: "01:00:00"));
            state.Append("hourly-s0", JobId("job.hourly"), At(0), At(60), DurableSliceStatus.Completed, expectedVersion: 0);
            state.Append("hourly-s1", JobId("job.hourly"), At(24 * 60), At(25 * 60), DurableSliceStatus.Completed, expectedVersion: 0);

            catalog.Create(Schedule("job.multihour", "MultiHourFunction", isPaused: false, queryWindowSize: "06:00:00"));
            state.Append("multihour-s0", JobId("job.multihour"), At(0), At(6 * 60), DurableSliceStatus.Completed, expectedVersion: 0);
            state.Append("multihour-s1", JobId("job.multihour"), At(24 * 60), At(30 * 60), DurableSliceStatus.Failed, expectedVersion: 0, reason: "multi-hour boom");

            catalog.Create(Schedule("job.paused", "PausedFunction", isPaused: true));
            state.Append("paused-s0", JobId("job.paused"), At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
            queue.Enqueue(JobId("job.paused"), At(0), At(5), "paused-queue-s0", At(0));
        }

        [Fact]
        public async Task Throttling_advisor_surfaces_ranks_and_applies_a_recommendation()
        {
            var jobId = SeedThrottledJob("job.throttled", maxParallelism: 8, durationMinutes: 12);
            using var client = factory.CreateClient();

            var page = await client.GetStringAsync("/throttling");
            Assert.Contains("job.throttled", page);
            Assert.Contains("Reduce to 4", page); // keep-up floor = ceil(12/5 * 1.5) = 4
            Assert.Contains("failed because of throttling", page); // a slice dead-lettered on throttling

            var dashboard = await client.GetStringAsync("/");
            Assert.Contains("ingestion throttling", dashboard);

            // Guardrail: a reduction below the keep-up floor (4) is rejected and the job is unchanged.
            var rejectToken = await ReadFormToken(client, "/throttling");
            using var rejected = await PostFormValues(client, "/throttling/apply", rejectToken, new[]
            {
                new KeyValuePair<string, string>("jobId", jobId),
                new KeyValuePair<string, string>("expectedVersion", "1"),
                new KeyValuePair<string, string>("newMaxParallelism", "2")
            });
            rejected.EnsureSuccessStatusCode();
            Assert.Equal(8, new SqliteJobCatalogRepository(sqlite).Get(jobId)!.Definition.MaxParallelism);

            // Applying the recommended floor succeeds and reduces maxParallelism.
            var applyToken = await ReadFormToken(client, "/throttling");
            using var applied = await PostFormValues(client, "/throttling/apply", applyToken, new[]
            {
                new KeyValuePair<string, string>("jobId", jobId),
                new KeyValuePair<string, string>("expectedVersion", "1"),
                new KeyValuePair<string, string>("newMaxParallelism", "4")
            });
            applied.EnsureSuccessStatusCode();
            Assert.Equal(4, new SqliteJobCatalogRepository(sqlite).Get(jobId)!.Definition.MaxParallelism);
        }

        [Fact]
        public async Task Throttling_advisor_redirects_stale_recommendations_with_guidance()
        {
            var jobId = SeedThrottledJob("job.throttled.stale", maxParallelism: 8, durationMinutes: 12);
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var rendered = catalog.Get(jobId)!;
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var token = await ReadFormToken(client, "/throttling");
            var current = catalog.Update(jobId, rendered.ScheduleJson, rendered.CatalogVersion, actor: "concurrent-test");

            using var response = await PostFormValues(client, "/throttling/apply", token, new[]
            {
                new KeyValuePair<string, string>("jobId", jobId),
                new KeyValuePair<string, string>("expectedVersion", rendered.CatalogVersion.ToString()),
                new KeyValuePair<string, string>("newMaxParallelism", "4")
            });

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/throttling", response.Headers.Location?.OriginalString);
            Assert.Equal(8, catalog.Get(jobId)!.Definition.MaxParallelism);
            Assert.Equal(current.CatalogVersion, catalog.Get(jobId)!.CatalogVersion);

            var page = await client.GetStringAsync(response.Headers.Location!.OriginalString);
            Assert.Contains("This job changed after the page loaded", page);
            Assert.Contains("your request was not applied", page);
        }

        private string SeedThrottledJob(string activityId, int maxParallelism, int durationMinutes)
        {
            const string cluster = "https://kolite-example.invalid";
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var state = new SqliteSliceStateRepository(sqlite);
            var readModels = new SqliteOperationalReadModelRepository(sqlite);
            var throttle = new SqliteIngestionThrottleRepository(sqlite);

            var now = DateTimeOffset.UtcNow;
            // A recent startFrom keeps the job at its real-time frontier (not backfilling), so the
            // keep-up floor governs the recommendation rather than a catch-up floor.
            var jobId = catalog.Create(ThrottleSchedule(activityId, cluster, maxParallelism, startFrom: now.AddMinutes(-10))).JobId;

            // Six successful slice durations within the last hour give a stable p75 duration estimate
            // (deliberately outside the recent rate window).
            for (var i = 0; i < 6; i++)
            {
                var sliceStart = new DateTimeOffset(2026, 6, 23, 0, 0, 0, TimeSpan.Zero).AddMinutes(i * 5);
                var sliceEnd = sliceStart.AddMinutes(5);
                state.Append($"{jobId}-st-{i}", jobId, sliceStart, sliceEnd, DurableSliceStatus.Completed, expectedVersion: 0);
                var startedAt = now.AddMinutes(-40 + i);
                readModels.RecordAttempt($"{jobId}-att-{i}", jobId, sliceStart, sliceEnd, 1, "Succeeded", "worker", startedAt, startedAt.AddMinutes(durationMinutes));
            }

            // Three distinct throttled slices within the window; the last dead-lettered on throttling,
            // which forces the advisory to surface and lists the lost slice.
            foreach (var minute in new[] { 1, 2, 3 })
            {
                var sliceStart = new DateTimeOffset(2026, 6, 23, 6, 0, 0, TimeSpan.Zero).AddMinutes(minute * 5);
                var sliceEnd = sliceStart.AddMinutes(5);
                var observedAt = now.AddMinutes(-minute);
                var terminal = minute == 3;
                state.Append($"{jobId}-thr-st-{minute}", jobId, sliceStart, sliceEnd, terminal ? DurableSliceStatus.DeadLettered : DurableSliceStatus.Running, expectedVersion: 0, reason: terminal ? "throttled out" : null);
                readModels.RecordAttempt($"{jobId}-thr-att-{minute}", jobId, sliceStart, sliceEnd, 2, terminal ? "DeadLettered" : "FailedRetryable", "worker", observedAt.AddMinutes(-1), observedAt, "KustoRequestThrottledException", "Origin: 'CapacityPolicy/Ingestion'");
                throttle.Record(new IngestionThrottleObservation(jobId, cluster, sliceStart, sliceEnd, Attempt: 2, ReportedCapacity: 18, observedAt, Terminal: terminal));
            }

            return jobId;
        }

        private static string ThrottleSchedule(string activityId, string cluster, int maxParallelism, DateTimeOffset startFrom) => $$"""
            {
              "id": "{{JobId(activityId)}}",
              "activityId": "{{activityId}}",
              "functionName": "ThrottleFn",
              "outputTable": "Output",
              "queryWindowSize": "00:05:00",
              "delayFromUtcNow": "00:00:00",
              "maxParallelism": {{maxParallelism}},
              "queryTimeout": "00:01:00",
              "isPaused": false,
              "startFrom": "{{startFrom.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ")}}",
              "target": { "clusterUri": "{{cluster}}", "database": "DemoDb" }
            }
            """;

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

        private static async Task<HttpResponseMessage> PostFormValues(HttpClient client, string path, FormToken token, IEnumerable<KeyValuePair<string, string>> values)
        {
            var fields = new List<KeyValuePair<string, string>>(values)
            {
                new("__RequestVerificationToken", token.Value)
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new FormUrlEncodedContent(fields)
            };
            if (!string.IsNullOrEmpty(token.Cookie)) request.Headers.Add("Cookie", token.Cookie);
            return await client.SendAsync(request);
        }

        private static async Task<HttpResponseMessage> PostFormAjax(HttpClient client, string path, FormToken token, Dictionary<string, string> values)
        {
            values["__RequestVerificationToken"] = token.Value;
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new FormUrlEncodedContent(values)
            };
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
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

        private static string[] DashboardJobOrder(string html) =>
            Regex.Matches(html, "data-dashboard-job-row=\"[^\"]*\" data-dashboard-job-id=\"(?<id>[^\"]+)\"", RegexOptions.CultureInvariant)
                .Select(match => match.Groups["id"].Value)
                .ToArray();

        private static string[] DashboardSuccessChartSeries(string html)
        {
            var match = Regex.Match(
                html,
                "<script type=\"application/json\" id=\"success-rate-by-function-chart-data\" class=\"success-chart-payload\">(?<json>.*?)</script>",
                RegexOptions.Singleline | RegexOptions.CultureInvariant);
            Assert.True(match.Success, "Expected dashboard success-rate chart payload.");
            using var document = JsonDocument.Parse(match.Groups["json"].Value);
            return document.RootElement.GetProperty("series")
                .EnumerateArray()
                .Select(series => series.GetProperty("jobId").GetString() ?? string.Empty)
                .ToArray();
        }

        [Fact]
        public void Job_details_show_catch_up_estimate_when_far_behind()
        {
            // ManualClock 20h after startFrom; 1h windows, delay 0 => 20 eligible slices.
            var clock = new ManualClock(At(20 * 60));
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var state = new SqliteSliceStateRepository(sqlite);
            var readModels = new SqliteOperationalReadModelRepository(sqlite);
            catalog.Create(Schedule("job.catchup.behind", "CatchUpFunction", isPaused: false, queryWindowSize: "01:00:00"));
            BackdateDefinitionEvents(JobId("job.catchup.behind"), At(0));

            // 4 completed slices => backlog 16 of 20 eligible (16h). One success each at
            // 17:00..18:00 => 3 completions/hour => R = 3 data-h/wall-h (1h window).
            var completions = new[] { At(17 * 60), At((17 * 60) + 20), At((17 * 60) + 40), At(18 * 60) };
            for (var i = 0; i < 4; i++)
            {
                state.Append($"behind-complete-{i}", JobId("job.catchup.behind"), At(i * 60), At((i + 1) * 60), DurableSliceStatus.Completed, expectedVersion: 0);
                readModels.RecordAttempt($"behind-attempt-{i}", JobId("job.catchup.behind"), At(i * 60), At((i + 1) * 60), 1, "Succeeded", "worker", completions[i].AddMinutes(-5), completions[i]);
            }

            var query = new JobDetailsPageQuery(catalog, readModels, new SqliteWorkQueueRepository(sqlite), new LifecycleReadModel(new SqliteLifecycleReadModelRepository(sqlite)), new OperationalDetailsReadModel(new SqliteOperationalReadModelRepository(sqlite)), clock);
            var data = query.Get(JobId("job.catchup.behind"));

            Assert.NotNull(data);
            Assert.Equal(CatchUpStatus.CatchingUp, data!.CatchUp.Status);
            Assert.True(data.CatchUp.ShouldDisplay);
            Assert.Equal(16, data.CatchUp.BacklogSlices);
            Assert.Equal(3.0, data.CatchUp.RealTimeMultiple!.Value, 6);
            Assert.Equal(TimeSpan.FromHours(8), data.CatchUp.ProjectedCatchUp);
            Assert.Equal(At(20 * 60).AddHours(8), data.CatchUp.EtaUtc);
        }

        [Fact]
        public void Job_details_hide_catch_up_estimate_when_caught_up()
        {
            var clock = new ManualClock(At(20 * 60));
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var state = new SqliteSliceStateRepository(sqlite);
            var readModels = new SqliteOperationalReadModelRepository(sqlite);
            catalog.Create(Schedule("job.catchup.current", "CatchUpFunction", isPaused: false, queryWindowSize: "01:00:00"));
            BackdateDefinitionEvents(JobId("job.catchup.current"), At(0));

            // All 20 eligible 1h slices complete => no backlog.
            for (var i = 0; i < 20; i++)
            {
                state.Append($"current-complete-{i}", JobId("job.catchup.current"), At(i * 60), At((i + 1) * 60), DurableSliceStatus.Completed, expectedVersion: 0);
            }

            for (var i = 0; i <= 12; i++)
            {
                var completedAt = At(14 * 60 + (i * 30));
                readModels.RecordAttempt($"current-attempt-{i}", JobId("job.catchup.current"), At(i * 60), At((i + 1) * 60), 1, "Succeeded", "worker", completedAt.AddMinutes(-5), completedAt);
            }

            var query = new JobDetailsPageQuery(catalog, readModels, new SqliteWorkQueueRepository(sqlite), new LifecycleReadModel(new SqliteLifecycleReadModelRepository(sqlite)), new OperationalDetailsReadModel(new SqliteOperationalReadModelRepository(sqlite)), clock);
            var data = query.Get(JobId("job.catchup.current"));

            Assert.NotNull(data);
            Assert.Equal(CatchUpStatus.CaughtUp, data!.CatchUp.Status);
            Assert.False(data.CatchUp.ShouldDisplay);
        }

        [Fact]
        public async Task Job_details_page_renders_catch_up_card_when_behind()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var state = new SqliteSliceStateRepository(sqlite);
            var readModels = new SqliteOperationalReadModelRepository(sqlite);
            catalog.Create(Schedule("job.catchup.page", "CatchUpFunction", isPaused: false, queryWindowSize: "01:00:00"));
            BackdateDefinitionEvents(JobId("job.catchup.page"), At(0));
            var completions = new[] { At(17 * 60), At((17 * 60) + 20), At((17 * 60) + 40), At(18 * 60) };
            for (var i = 0; i < 4; i++)
            {
                state.Append($"page-complete-{i}", JobId("job.catchup.page"), At(i * 60), At((i + 1) * 60), DurableSliceStatus.Completed, expectedVersion: 0);
                readModels.RecordAttempt($"page-attempt-{i}", JobId("job.catchup.page"), At(i * 60), At((i + 1) * 60), 1, "Succeeded", "worker", completions[i].AddMinutes(-5), completions[i]);
            }

            using var runFactory = CreateFactory(enableScheduler: false, configureServices: services => services.AddSingleton<IClock>(new ManualClock(At(20 * 60))));
            using var client = runFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var html = await client.GetStringAsync($"/jobs/{JobId("job.catchup.page")}");

            Assert.Contains("catch-up-card", html);
            Assert.Contains("Catching up", html);
            Assert.Contains("Slices behind", html);
            Assert.Contains("Data behind", html);
            Assert.Contains("Processing rate", html);
            Assert.Contains("Estimated time remaining", html);
        }

        [Fact]
        public async Task Job_details_page_hides_catch_up_card_when_backlog_is_upstream_blocked()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var state = new SqliteSliceStateRepository(sqlite);
            var readModels = new SqliteOperationalReadModelRepository(sqlite);
            catalog.Create(Schedule("job.catchup.blocked", "CatchUpFunction", isPaused: false, queryWindowSize: "01:00:00"));
            BackdateDefinitionEvents(JobId("job.catchup.blocked"), At(0));

            // 4 completed slices with recent successes: without blocking this is a 16-slice backlog
            // projecting "Catching up". The remaining eligible slices are blocked on an upstream
            // dependency, so the actionable backlog drops below the floor and no card is shown.
            var completions = new[] { At(17 * 60), At((17 * 60) + 20), At((17 * 60) + 40), At(18 * 60) };
            for (var i = 0; i < 4; i++)
            {
                state.Append($"blocked-complete-{i}", JobId("job.catchup.blocked"), At(i * 60), At((i + 1) * 60), DurableSliceStatus.Completed, expectedVersion: 0);
                readModels.RecordAttempt($"blocked-attempt-{i}", JobId("job.catchup.blocked"), At(i * 60), At((i + 1) * 60), 1, "Succeeded", "worker", completions[i].AddMinutes(-5), completions[i]);
            }
            for (var i = 4; i < 20; i++)
            {
                state.Append($"blocked-dep-{i}", JobId("job.catchup.blocked"), At(i * 60), At((i + 1) * 60), DurableSliceStatus.DependencyBlocked, expectedVersion: 0, reason: "upstream");
            }

            using var runFactory = CreateFactory(enableScheduler: false, configureServices: services => services.AddSingleton<IClock>(new ManualClock(At(20 * 60))));
            using var client = runFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var html = await client.GetStringAsync($"/jobs/{JobId("job.catchup.blocked")}");

            Assert.DoesNotContain("catch-up-card", html);
        }

        [Fact]
        public async Task Job_details_page_renders_collecting_data_card_after_definition_change()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var state = new SqliteSliceStateRepository(sqlite);
            var readModels = new SqliteOperationalReadModelRepository(sqlite);
            catalog.Create(Schedule("job.catchup.collecting", "CatchUpFunction", isPaused: false, queryWindowSize: "01:00:00"));
            // Definition changed 1h before "now" (20h): the throughput window starts at the change,
            // so only completions since then are sampled.
            BackdateDefinitionEvents(JobId("job.catchup.collecting"), At(19 * 60));

            // 4 slices completed => backlog 16 of 20 eligible. Only two succeeded attempts fall after
            // the definition change (below the 3 required), so a rate cannot be projected yet.
            for (var i = 0; i < 4; i++)
            {
                state.Append($"collecting-complete-{i}", JobId("job.catchup.collecting"), At(i * 60), At((i + 1) * 60), DurableSliceStatus.Completed, expectedVersion: 0);
            }
            var completions = new[] { At((19 * 60) + 10), At((19 * 60) + 40) };
            for (var i = 0; i < completions.Length; i++)
            {
                readModels.RecordAttempt($"collecting-attempt-{i}", JobId("job.catchup.collecting"), At(i * 60), At((i + 1) * 60), 1, "Succeeded", "worker", completions[i].AddMinutes(-5), completions[i]);
            }

            var query = new JobDetailsPageQuery(catalog, readModels, new SqliteWorkQueueRepository(sqlite), new LifecycleReadModel(new SqliteLifecycleReadModelRepository(sqlite)), new OperationalDetailsReadModel(new SqliteOperationalReadModelRepository(sqlite)), new ManualClock(At(20 * 60)));
            var data = query.Get(JobId("job.catchup.collecting"));

            Assert.NotNull(data);
            Assert.Equal(CatchUpStatus.InsufficientData, data!.CatchUp.Status);
            Assert.True(data.CatchUp.ShouldDisplay);
            Assert.True(data.CatchUp.ThroughputWindowBoundedByDefinitionChange);
            Assert.Equal(2, data.CatchUp.ObservedThroughputSamples);
            Assert.Null(data.CatchUp.EtaUtc);

            using var runFactory = CreateFactory(enableScheduler: false, configureServices: services => services.AddSingleton<IClock>(new ManualClock(At(20 * 60))));
            using var client = runFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var html = await client.GetStringAsync($"/jobs/{JobId("job.catchup.collecting")}");

            Assert.Contains("catch-up-card", html);
            Assert.Contains("Catching up", html);
            Assert.Contains("Collecting data to estimate catch-up time", html);
            Assert.Contains("definition changed recently", html);
            // The two requirements are now a checklist under the message. This sample has 2 of 3
            // completions (count gate pending) spanning 30 min (>= the 10-minute span gate, met), so
            // one item renders met and the other pending. "Data collected" no longer sits in the top row.
            Assert.Contains("catch-up-checklist", html);
            Assert.Contains("Successful completions", html);
            Assert.Contains("2 of 3 completions", html);
            Assert.Contains("Time collected", html);
            Assert.Contains("catch-up-check-item met", html);
            Assert.Contains("catch-up-check-item pending", html);
            Assert.DoesNotContain("Data collected", html);
            Assert.DoesNotContain("Estimated caught up", html);
        }

        private void BackdateDefinitionEvents(string jobId, DateTimeOffset recordedAtUtc)
        {
            using var connection = sqlite.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE job_definition_events SET recorded_at_utc = $t WHERE job_id = $j;";
            command.Parameters.AddWithValue("$t", recordedAtUtc.UtcDateTime.ToString("o"));
            command.Parameters.AddWithValue("$j", jobId);
            command.ExecuteNonQuery();
        }

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
                        ["KoLite:Scheduler:Enabled"] = enableScheduler.ToString(),
                        ["KoLite:UpdateCheck:Enabled"] = "false"
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
                builder.ConfigureServices(services =>
                {
                    services.AddLogging(logging => logging.ClearProviders());
                    services.AddTestLocalRequestPolicy();
                });
                if (configureServices is not null)
                {
                    builder.ConfigureServices(configureServices);
                }
            });

        private static string JobId(string activityId)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(activityId));
            return new Guid(bytes).ToString("N");
        }

        private static string Schedule(string activityId, string functionName, bool isPaused, string outputTable = "Output", int maxParallelism = 1, string queryWindowSize = "00:05:00", string? folder = null, IReadOnlyList<string>? tags = null, string? endOn = null, string? healthPolicy = null, string? description = null, int? chunks = null)
        {
            var schedule = $$"""
            {
              "id": "{{JobId(activityId)}}",
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

            var metadata = new List<string>();
            if (description is not null)
            {
                metadata.Add($"  \"description\": {JsonSerializer.Serialize(description)},");
            }

            if (folder is not null)
            {
                metadata.Add($"  \"folder\": {JsonSerializer.Serialize(folder)},");
            }

            if (tags is { Count: > 0 })
            {
                metadata.Add($"  \"tags\": {JsonSerializer.Serialize(tags)},");
            }

            if (endOn is not null)
            {
                metadata.Add($"  \"endOn\": {JsonSerializer.Serialize(endOn)},");
            }

            if (healthPolicy is not null)
            {
                metadata.Add($"  \"healthPolicy\": {JsonSerializer.Serialize(healthPolicy)},");
            }

            if (chunks is not null)
            {
                metadata.Add($"  \"chunks\": {chunks.Value},");
            }

            return metadata.Count == 0
                ? schedule
                : schedule.Replace("  \"target\":", string.Join(Environment.NewLine, metadata) + "\n  \"target\":", StringComparison.Ordinal);
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
