using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KoLite.LocalApp
{
    // Defense-in-depth: refuse to start a second app instance against the same SQLite database.
    // Two processes writing one ko-lite.db multiply write contention and can invalidate each other's
    // in-flight reads (SQLITE_ABORT) or, in the worst case, corrupt the file. A named mutex keyed by
    // the normalized database path is released automatically when the owning process exits (even on a
    // crash), so a normal stop-then-start restart simply re-acquires it. Override with
    // KoLite:AllowMultipleInstances=true for the rare intentional case (always use distinct databases).
    internal static class SingleInstanceGuard
    {
        private static Mutex? held;

        public static void Enforce(string databasePath, IConfiguration configuration, ILogger logger)
        {
            if (IsMultipleInstancesAllowed(configuration))
            {
                logger.LogWarning("KoLite:AllowMultipleInstances is set; the single-instance guard is disabled. Ensure each instance uses a distinct SQLite database.");
                return;
            }

            Mutex mutex;
            try
            {
                mutex = new Mutex(initiallyOwned: false, BuildMutexName(databasePath));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException or PlatformNotSupportedException)
            {
                // If the OS refuses the named mutex (permissions/platform), do not block startup on a
                // best-effort safeguard.
                logger.LogWarning(ex, "Could not create the single-instance guard; continuing without it.");
                return;
            }

            bool acquired;
            try
            {
                acquired = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                // The previous owner exited without releasing it (e.g. a crash). We now own it.
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                throw new InvalidOperationException(
                    $"Another KO Lite instance is already running against '{databasePath}'. " +
                    "Refusing to start a second instance that shares the same SQLite database. " +
                    "Stop the other instance first, or set KoLite:AllowMultipleInstances=true (with a distinct database) to override.");
            }

            // Hold the mutex for the life of the process; the OS releases it automatically on exit.
            held = mutex;
        }

        private static bool IsMultipleInstancesAllowed(IConfiguration configuration)
        {
            var value = configuration["KoLite:AllowMultipleInstances"];
            return !string.IsNullOrWhiteSpace(value) && bool.TryParse(value, out var allow) && allow;
        }

        private static string BuildMutexName(string databasePath)
        {
            var normalized = Path.GetFullPath(databasePath).ToLowerInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
            // Session-local scope (default namespace) is enough for the interactive single-user local
            // app and avoids the elevated rights sometimes required for Global\ kernel objects.
            return $"KoLite-single-instance-{hash}";
        }
    }
}
