using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Activity
{
    public sealed class IndexModel : PageModel
    {
        private readonly ActivityQuery query;
        private readonly PerformancePageQuery performanceQuery;

        public IndexModel(ActivityQuery query, PerformancePageQuery performanceQuery)
        {
            this.query = query;
            this.performanceQuery = performanceQuery;
        }

        public ActivityPageData? Data { get; private set; }
        public PerformancePageData? Performance { get; private set; }
        public bool IsPerformance { get; private set; }
        public string Range { get; private set; } = "1d";
        public IReadOnlyList<ChartRangeLink> RangeLinks => IsPerformance ? PerformanceRangeOptions.Links : ChartRangeOptions.Links;
        public string RefreshHref => Performance?.State.Href ?? "/activity?range=" + Range;

        public void OnGet(
            string? range,
            string? view,
            string? q,
            [FromQuery(Name = "tag")] string[]? tags,
            string? jobId,
            string? sort,
            string? dir)
        {
            IsPerformance = string.Equals(view, "performance", StringComparison.OrdinalIgnoreCase);
            if (IsPerformance)
            {
                Performance = performanceQuery.Get(range, q, tags, jobId, sort, dir);
                Range = Performance.State.Range;
                if (Performance.FilterError is not null)
                {
                    Response.StatusCode = StatusCodes.Status400BadRequest;
                }

                return;
            }

            Range = ChartRangeOptions.Normalize(range);
            Data = query.GetActivity(ChartRangeOptions.Parse(Range));
        }
    }
}
