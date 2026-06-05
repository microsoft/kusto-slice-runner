using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace KoLite.LocalApp.Updates
{
    public sealed record LocalUpdateCheckOptions(bool Enabled, TimeSpan Interval, string Repository, string Branch)
    {
        public const string DefaultRepository = "microsoft/kusto-slice-runner";
        public const string DefaultBranch = "main";
        public static TimeSpan DefaultInterval { get; } = TimeSpan.FromHours(1);

        public static LocalUpdateCheckOptions From(IConfiguration configuration)
        {
            var enabledText = configuration["KoLite:UpdateCheck:Enabled"];
            var enabled = string.IsNullOrWhiteSpace(enabledText) || bool.Parse(enabledText);

            var configuredInterval = configuration["KoLite:UpdateCheck:Interval"];
            var interval = string.IsNullOrWhiteSpace(configuredInterval)
                ? DefaultInterval
                : TimeSpan.Parse(configuredInterval, CultureInfo.InvariantCulture);
            if (interval <= TimeSpan.Zero) throw new InvalidOperationException("KoLite:UpdateCheck:Interval must be greater than zero.");

            var repository = configuration["KoLite:UpdateCheck:Repository"];
            if (string.IsNullOrWhiteSpace(repository)) repository = DefaultRepository;

            var branch = configuration["KoLite:UpdateCheck:Branch"];
            if (string.IsNullOrWhiteSpace(branch)) branch = DefaultBranch;

            return new LocalUpdateCheckOptions(enabled, interval, repository.Trim(), branch.Trim());
        }
    }
}
