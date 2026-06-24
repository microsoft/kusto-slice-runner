using KoLite.Local.Core.Throttling;
using KoLite.Local.Sqlite.Throttling;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Throttling
{
    public sealed class IndexModel : PageModel
    {
        // Window of throttle history shown on the severity chart.
        private static readonly TimeSpan ChartRange = TimeSpan.FromHours(6);

        private readonly SqliteThrottleAdvisorReadModel advisor;
        private readonly ThrottleSeverityQuery severityQuery;
        private readonly ThrottleAdvisorOptions options;

        public IndexModel(SqliteThrottleAdvisorReadModel advisor, ThrottleSeverityQuery severityQuery, ThrottleAdvisorOptions options)
        {
            this.advisor = advisor;
            this.severityQuery = severityQuery;
            this.options = options;
        }

        public IReadOnlyList<ClusterThrottleAdvisory> Advisories { get; private set; } = Array.Empty<ClusterThrottleAdvisory>();
        public ThrottleSeverityChart? Severity { get; private set; }
        public TimeSpan HeadlineWindow => options.Window;
        public double RateThresholdPercent => options.RateThresholdPercent;
        public TimeSpan CleanPeriod => options.CleanPeriod;
        public string? StatusMessage { get; private set; }
        public bool StatusIsError { get; private set; }

        public void OnGet()
        {
            Advisories = advisor.BuildAdvisories();
            Severity = severityQuery.GetSeverity(ChartRange, options.Window);
            StatusMessage = TempData[ApplyModel.StatusMessageKey] as string;
            StatusIsError = TempData[ApplyModel.StatusIsErrorKey] as bool? ?? false;
        }
    }
}
