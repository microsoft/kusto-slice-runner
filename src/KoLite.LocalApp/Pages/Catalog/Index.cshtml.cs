using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Catalog
{
    public sealed class IndexModel : PageModel
    {
        private readonly DashboardPageQuery query;

        public IndexModel(DashboardPageQuery query)
        {
            this.query = query;
        }

        public DashboardPageData Data { get; private set; } = null!;

        public void OnGet() => Data = query.Get(TimeSpan.FromDays(1));
    }
}
