// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;

namespace KoLite.LocalApp.Tests
{
    internal static class TestCleanup
    {
        // Some LocalApp tests (notably the /control/v1/shutdown/drain endpoint) trigger a real app
        // shutdown whose detached stop task briefly opens a SQLite connection (RecordLog) after the
        // HTTP response has been sent. Under parallel load that connection can still hold the
        // fixture's temporary web.db when Dispose() deletes its directory, which surfaces as
        // "The process cannot access the file 'web.db' because it is being used by another process."
        // Retry the delete for a short bounded window so pooled or briefly active connections can
        // release the fixture's ephemeral GUID-named temp directory.
        public static void DeleteDirectoryWithRetry(string path) => DeleteDirectory(path, bestEffort: false);

        public static void DeleteDirectoryBestEffort(string path) => DeleteDirectory(path, bestEffort: true);

        private static void DeleteDirectory(string path, bool bestEffort)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            var databasePaths = Directory.EnumerateFiles(path, "*.db", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFullPath)
                .ToArray();
            for (var attempt = 0; attempt < 20; attempt++)
            {
                ClearDatabasePools(databasePaths);
                try
                {
                    Directory.Delete(path, recursive: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt == 19 && !bestEffort)
                    {
                        throw;
                    }

                    Thread.Sleep(100);
                }
            }
        }

        private static void ClearDatabasePools(IEnumerable<string> databasePaths)
        {
            foreach (var databasePath in databasePaths)
            {
                var builder = new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadWriteCreate
                };
                using var poolKey = new SqliteConnection(builder.ToString());

                // ClearAllPools races unrelated fixtures that xUnit is still running in parallel.
                SqliteConnection.ClearPool(poolKey);
            }
        }
    }
}
