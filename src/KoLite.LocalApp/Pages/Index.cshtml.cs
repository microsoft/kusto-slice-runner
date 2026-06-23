using KoLite.Local.Sqlite.Throttling;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages
{
    public sealed class IndexModel : PageModel
    {
        private readonly DashboardPageQuery query;
        private readonly SqliteThrottleAdvisorReadModel throttleAdvisor;

        public IndexModel(DashboardPageQuery query, SqliteThrottleAdvisorReadModel throttleAdvisor)
        {
            this.query = query;
            this.throttleAdvisor = throttleAdvisor;
        }

        public DashboardPageData Data { get; private set; } = null!;
        public string Range { get; private set; } = "1d";
        public DashboardSort Sort { get; private set; } = DashboardSort.Default;
        public IReadOnlyList<ChartRangeLink> RangeLinks => ChartRangeOptions.Links;
        public int ThrottledClusterCount { get; private set; }

        public string? BulkOperationSummary => TempData[Catalog.CatalogBulkOperations.TempDataKey] as string;

        public void OnGet(string? range, [FromQuery(Name = "tag")] string[]? tags, string? sort, string? dir)
        {
            Range = ChartRangeOptions.Normalize(range);
            Sort = DashboardSort.Parse(sort, dir);
            Data = query.Get(ChartRangeOptions.Parse(Range), tags, Sort);
            ThrottledClusterCount = throttleAdvisor.ListSustainedClusterUris().Count;
        }
    }
}
