// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Schema;

namespace KoLite.Local.Sqlite.Tests
{
    public sealed class TestCleanupTests
    {
        [Fact]
        public void DeleteDirectoryWithRetry_clears_multiple_target_pools_without_disrupting_an_unrelated_database()
        {
            var root = Path.Combine(AppContext.BaseDirectory, "cleanup-tests", Guid.NewGuid().ToString("N"));
            var targetDirectory = Path.Combine(root, "target");
            var unrelatedDirectory = Path.Combine(root, "unrelated");
            Directory.CreateDirectory(targetDirectory);
            Directory.CreateDirectory(unrelatedDirectory);

            try
            {
                new KoLiteSqliteSchema(Factory(Path.Combine(targetDirectory, "first.db"))).EnsureSchema();
                new KoLiteSqliteSchema(Factory(Path.Combine(targetDirectory, "second.db"))).EnsureSchema();
                var unrelatedFactory = Factory(Path.Combine(unrelatedDirectory, "unrelated.db"));
                new KoLiteSqliteSchema(unrelatedFactory).EnsureSchema();

                using (var unrelatedConnection = unrelatedFactory.OpenConnection())
                using (var createMarker = unrelatedConnection.CreateCommand())
                {
                    createMarker.CommandText = "CREATE TEMP TABLE pool_marker(value INTEGER); INSERT INTO pool_marker(value) VALUES (42);";
                    createMarker.ExecuteNonQuery();
                }

                TestCleanup.DeleteDirectoryWithRetry(targetDirectory);

                Assert.False(Directory.Exists(targetDirectory));
                using var reusedConnection = unrelatedFactory.OpenConnection();
                using var readMarker = reusedConnection.CreateCommand();
                readMarker.CommandText = "SELECT value FROM pool_marker;";
                Assert.Equal(42L, Convert.ToInt64(readMarker.ExecuteScalar()));
            }
            finally
            {
                TestCleanup.DeleteDirectoryWithRetry(targetDirectory);
                TestCleanup.DeleteDirectoryWithRetry(unrelatedDirectory);
                if (Directory.Exists(root))
                {
                    Directory.Delete(root);
                }
            }
        }

        private static KoLiteSqliteConnectionFactory Factory(string databasePath) =>
            new(new KoLiteSqliteConnectionOptions(databasePath) { BusyTimeoutMilliseconds = 10_000 });
    }
}
