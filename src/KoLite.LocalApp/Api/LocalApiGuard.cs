using System.Net;

namespace KoLite.LocalApp.Api
{
    // Local management API is intended for same-machine agents/tools only. The app already
    // binds to the loopback address by default; this guard is defense-in-depth in case the
    // bound address is widened via KoLite:Urls. Mirrors LocalShutdownDrainCoordinator.IsAllowedDrainRemote.
    public static class LocalApiGuard
    {
        public static bool IsLoopback(IPAddress? remoteIpAddress) =>
            remoteIpAddress is null || IPAddress.IsLoopback(remoteIpAddress);
    }
}
