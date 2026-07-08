using KoLite.Local.Core.Time;
using KoLite.LocalApp.FailureAnalysis;

namespace KoLite.LocalApp.Tests
{
    public sealed class FailureAnalysisRunRegistryTests
    {
        private static readonly DateTimeOffset BaseTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        [Fact]
        public void TryStart_ReturnsTrue_WithRunningRun_MatchingJobId()
        {
            var clock = new ManualClock(BaseTime);
            var registry = new FailureAnalysisRunRegistry(clock);

            var result = registry.TryStart("job1", out var run);

            Assert.True(result);
            Assert.NotEmpty(run.RunId);
            Assert.Equal("job1", run.JobId);
            Assert.Equal(FailureAnalysisStatus.Running, run.Status);
            Assert.Null(run.Markdown);
            Assert.Null(run.Error);
            Assert.Equal(BaseTime, run.StartedAtUtc);
            Assert.Equal(BaseTime, run.UpdatedAtUtc);
        }

        [Fact]
        public void TryStart_SecondCall_SameJob_WhileRunning_ReturnsFalse_SameRunId()
        {
            var clock = new ManualClock(BaseTime);
            var registry = new FailureAnalysisRunRegistry(clock);
            registry.TryStart("job1", out var first);

            var result = registry.TryStart("job1", out var second);

            Assert.False(result);
            Assert.Equal(first.RunId, second.RunId);
        }

        [Fact]
        public void Complete_UpdatesStatusAndMarkdown_AndAllowsNewStartForSameJob()
        {
            var clock = new ManualClock(BaseTime);
            var registry = new FailureAnalysisRunRegistry(clock);
            registry.TryStart("job1", out var run);

            registry.Complete(run.RunId, "## md");

            var fetched = registry.Get(run.RunId);
            Assert.NotNull(fetched);
            Assert.Equal(FailureAnalysisStatus.Completed, fetched.Status);
            Assert.Equal("## md", fetched.Markdown);

            // Previous run is no longer Running, so a new TryStart should succeed.
            var started = registry.TryStart("job1", out var newRun);
            Assert.True(started);
            Assert.NotEqual(run.RunId, newRun.RunId);
        }

        [Fact]
        public void Fail_UpdatesStatusAndError()
        {
            var clock = new ManualClock(BaseTime);
            var registry = new FailureAnalysisRunRegistry(clock);
            registry.TryStart("job1", out var run);

            var failed = registry.Fail(run.RunId, "boom");

            Assert.NotNull(failed);
            Assert.Equal(FailureAnalysisStatus.Failed, failed.Status);
            Assert.Equal("boom", failed.Error);
        }

        [Fact]
        public void GetForJob_ReturnsRun_ForCorrectJob_NullForMismatch()
        {
            var clock = new ManualClock(BaseTime);
            var registry = new FailureAnalysisRunRegistry(clock);
            registry.TryStart("job1", out var run);

            var found = registry.GetForJob("job1", run.RunId);
            var notFound = registry.GetForJob("other-job", run.RunId);

            Assert.NotNull(found);
            Assert.Equal(run.RunId, found.RunId);
            Assert.Null(notFound);
        }

        [Fact]
        public void Get_ReturnsNull_AfterTtlExpiry()
        {
            var clock = new ManualClock(BaseTime);
            var registry = new FailureAnalysisRunRegistry(clock);
            registry.TryStart("job1", out var run);
            registry.Complete(run.RunId, "## done");

            clock.Advance(TimeSpan.FromMinutes(31));

            Assert.Null(registry.Get(run.RunId));
        }

        [Fact]
        public void Complete_Fail_Get_WithUnknownRunId_ReturnNull_NoThrow()
        {
            var clock = new ManualClock(BaseTime);
            var registry = new FailureAnalysisRunRegistry(clock);

            Assert.Null(registry.Complete("unknown", "md"));
            Assert.Null(registry.Fail("unknown", "err"));
            Assert.Null(registry.Get("unknown"));
        }
    }
}
