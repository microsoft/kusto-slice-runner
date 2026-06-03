using System.Net;

namespace KoLite.LocalApp.Tests
{
    public sealed class LocalShutdownDrainCoordinatorTests
    {
        [Fact]
        public async Task Drain_request_is_idempotent_and_waits_for_dispatcher_drain()
        {
            var coordinator = new LocalShutdownDrainCoordinator();

            coordinator.RecordWorkerStarted(At(0));
            var first = coordinator.RequestDrain(At(1), "first");
            var second = coordinator.RequestDrain(At(2), "second");

            Assert.Equal("DrainRequested", first.Mode);
            Assert.Equal("DrainRequested", second.Mode);
            Assert.Equal("first", second.Reason);
            Assert.Equal(1, second.ActiveWorkerCount);

            coordinator.RecordWorkerCompleted(At(3));
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.WaitForDrainedAsync(timeout.Token));
            }

            coordinator.NotifyWorkerDispatcherDrained(At(4));

            await coordinator.WaitForDrainedAsync();
            Assert.Equal("Drained", coordinator.GetSnapshot().Mode);
        }

        [Fact]
        public void Drain_remote_policy_allows_loopback_and_rejects_non_loopback()
        {
            Assert.True(LocalShutdownDrainCoordinator.IsAllowedDrainRemote(null));
            Assert.True(LocalShutdownDrainCoordinator.IsAllowedDrainRemote(IPAddress.Loopback));
            Assert.True(LocalShutdownDrainCoordinator.IsAllowedDrainRemote(IPAddress.IPv6Loopback));
            Assert.False(LocalShutdownDrainCoordinator.IsAllowedDrainRemote(IPAddress.Parse("10.1.2.3")));
        }

        private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);
    }
}
