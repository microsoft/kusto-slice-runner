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

        public DetailsModel(JobDetailsPageQuery query, JobChartQuery chartQuery, SqliteJobCatalogRepository catalog)
        {
            this.query = query;
            this.chartQuery = chartQuery;
            this.catalog = catalog;
        }

        public JobDetailsPageData? Data { get; private set; }
        public ScheduleEditorViewModel? Editor { get; private set; }
        public JobDetailsCharts? Charts { get; private set; }
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

            Charts = chartQuery.GetJobDetailsCharts(jobId, ChartRangeOptions.Parse(Range));
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
