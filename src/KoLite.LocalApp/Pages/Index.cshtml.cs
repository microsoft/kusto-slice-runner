using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages
{
    public sealed class IndexModel : PageModel
    {
        private readonly DashboardPageQuery query;

        public IndexModel(DashboardPageQuery query)
        {
            this.query = query;
        }

        public DashboardPageData Data { get; private set; } = null!;
        public string Range { get; private set; } = "1d";
        public IReadOnlyList<ChartRangeLink> RangeLinks => ChartRangeOptions.Links;

        public void OnGet(string? range)
        {
            Range = ChartRangeOptions.Normalize(range);
            Data = query.Get(ChartRangeOptions.Parse(Range));
        }
    }
}
