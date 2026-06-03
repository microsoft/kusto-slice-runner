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
        public IReadOnlyList<(string Key, string Label)> RangeLinks { get; } =
        [
            ("1h", "1 hour"),
            ("1d", "1 day"),
            ("7d", "7 days"),
            ("30d", "30 days")
        ];

        public void OnGet(string? range)
        {
            Range = NormalizeRange(range);
            Data = query.Get(ParseRange(Range));
        }

        private static string NormalizeRange(string? range) => range switch
        {
            "1h" or "1d" or "7d" or "30d" => range,
            _ => "1d"
        };

        private static TimeSpan ParseRange(string range) => range switch
        {
            "1h" => TimeSpan.FromHours(1),
            "7d" => TimeSpan.FromDays(7),
            "30d" => TimeSpan.FromDays(30),
            _ => TimeSpan.FromDays(1)
        };
    }
}
