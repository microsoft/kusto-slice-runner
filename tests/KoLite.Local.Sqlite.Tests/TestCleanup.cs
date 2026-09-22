// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Tests
{
    internal static class TestCleanup
    {
        public static void DeleteDirectoryWithRetry(string path)
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
                catch (Exception ex) when (attempt < 19 && ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(50);
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
