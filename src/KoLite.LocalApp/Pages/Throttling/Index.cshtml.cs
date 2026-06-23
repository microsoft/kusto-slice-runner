using KoLite.Local.Core.Throttling;
using KoLite.Local.Sqlite.Throttling;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Throttling
{
    public sealed class IndexModel : PageModel
    {
        private readonly SqliteThrottleAdvisorReadModel advisor;

        public IndexModel(SqliteThrottleAdvisorReadModel advisor)
        {
            this.advisor = advisor;
        }

        public IReadOnlyList<ClusterThrottleAdvisory> Advisories { get; private set; } = Array.Empty<ClusterThrottleAdvisory>();
        public string? StatusMessage { get; private set; }
        public bool StatusIsError { get; private set; }

        public void OnGet()
        {
            Advisories = advisor.BuildAdvisories();
            StatusMessage = TempData[ApplyModel.StatusMessageKey] as string;
            StatusIsError = TempData[ApplyModel.StatusIsErrorKey] as bool? ?? false;
        }
    }
}
