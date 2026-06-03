namespace KoLite.Local.Core.FailureSummaries
{
    public sealed record FailureSummaryRunnerResult(bool Succeeded, string SummaryMarkdown, int ExitCode = 0, string? ErrorMessage = null)
    {
        public static FailureSummaryRunnerResult Success(string markdown) => new(true, markdown);
        public static FailureSummaryRunnerResult Failure(string errorMessage, int exitCode = 1) => new(false, string.Empty, exitCode, errorMessage);
    }

    public interface IFailureSummaryRunner
    {
        Task<FailureSummaryRunnerResult> RunAsync(string prompt, CancellationToken cancellationToken = default);
    }

    public sealed class DeterministicFailureSummaryRunner : IFailureSummaryRunner
    {
        public Task<FailureSummaryRunnerResult> RunAsync(string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(FailureSummaryRunnerResult.Success("### Failure summary\nDeterministic local summary from grouped failure evidence."));
    }
}
