using Microsoft.Data.Sqlite;

namespace KoLite.LocalApp.Tests
{
    internal static class TestCleanup
    {
        // Some LocalApp tests (notably the /status/shutdown/drain endpoint) trigger a real app
        // shutdown whose detached stop task briefly opens a SQLite connection (RecordLog) after the
        // HTTP response has been sent. Under parallel load that connection can still hold the
        // fixture's temporary web.db when Dispose() deletes its directory, which surfaces as
        // "The process cannot access the file 'web.db' because it is being used by another process."
        // Retry the delete for a short bounded window before falling back to best-effort cleanup of
        // the ephemeral GUID-named temp directory.
        public static void DeleteDirectoryWithRetry(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            for (var attempt = 0; attempt < 20; attempt++)
            {
                SqliteConnection.ClearAllPools();
                try
                {
                    Directory.Delete(path, recursive: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(100);
                }
            }

            // Final best-effort attempt; an ephemeral temp directory must never fail a test.
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
