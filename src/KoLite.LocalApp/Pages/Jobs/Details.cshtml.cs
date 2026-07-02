using KoLite.Local.Sqlite.Catalog;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Jobs
{
    public sealed class DetailsModel : PageModel
    {
        private readonly JobDetailsPageQuery query;
        private readonly JobChartQuery chartQuery;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly DependencyGraphQuery dependencyGraphQuery;
        private readonly DashboardPageQuery dashboard;

        public DetailsModel(JobDetailsPageQuery query, JobChartQuery chartQuery, SqliteJobCatalogRepository catalog, DependencyGraphQuery dependencyGraphQuery, DashboardPageQuery dashboard)
        {
            this.query = query;
            this.chartQuery = chartQuery;
            this.catalog = catalog;
            this.dependencyGraphQuery = dependencyGraphQuery;
            this.dashboard = dashboard;
        }

        public JobDetailsPageData? Data { get; private set; }

        // The same JobListItem the dashboard renders, so the details header shows the identical
        // recent-health + completeness status without duplicating the derivation.
        public JobListItem? Status { get; private set; }
        public ScheduleEditorViewModel? Editor { get; private set; }
        public JobDetailsCharts? Charts { get; private set; }
        public DependencyGraphViewModel DependencyGraph { get; private set; } = DependencyGraphViewModel.Empty;
        public string Range { get; private set; } = "1d";
        public IReadOnlyList<ChartRangeLink> RangeLinks => ChartRangeOptions.Links;

        public IActionResult OnGet(string jobId, string? range)
        {
            Range = ChartRangeOptions.Normalize(range);
            Data = query.Get(jobId);
            if (Data is null)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return Page();
            }

            Status = dashboard.GetJob(jobId);
            Charts = chartQuery.GetJobDetailsCharts(jobId, ChartRangeOptions.Parse(Range));
            DependencyGraph = dependencyGraphQuery.Build(new[] { Data.Job.JobId });
            Editor = new ScheduleEditorViewModel(
                $"/catalog/{Uri.EscapeDataString(Data.Job.JobId)}/update",
                ScheduleFormInput.FromDefinition(Data.Definition),
                AppFormatting.PrettyJson(Data.Job.ScheduleJson),
                Data.Job.CatalogVersion,
                true,
                "Save job",
                Data.HasStarted,
                ScheduleEditorViewModel.BuildOptions(catalog, Data.Job.JobId));
            return Page();
        }
    }
}
