using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Activity
{
    public sealed class IndexModel : PageModel
    {
        private readonly ActivityQuery query;

        public IndexModel(ActivityQuery query)
        {
            this.query = query;
        }

        public ActivityPageData Data { get; private set; } = null!;
        public string Range { get; private set; } = "1d";
        public IReadOnlyList<ChartRangeLink> RangeLinks => ChartRangeOptions.Links;

        public void OnGet(string? range)
        {
            Range = ChartRangeOptions.Normalize(range);
            Data = query.GetActivity(ChartRangeOptions.Parse(Range));
        }
    }
}
