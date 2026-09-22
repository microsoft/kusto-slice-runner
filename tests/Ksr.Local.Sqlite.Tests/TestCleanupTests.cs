// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Sqlite.Connections;
using Ksr.Local.Sqlite.Schema;

namespace Ksr.Local.Sqlite.Tests
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
                new KsrSqliteSchema(Factory(Path.Combine(targetDirectory, "first.db"))).EnsureSchema();
                new KsrSqliteSchema(Factory(Path.Combine(targetDirectory, "second.db"))).EnsureSchema();
                var unrelatedFactory = Factory(Path.Combine(unrelatedDirectory, "unrelated.db"));
                new KsrSqliteSchema(unrelatedFactory).EnsureSchema();

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

        private static KsrSqliteConnectionFactory Factory(string databasePath) =>
            new(new KsrSqliteConnectionOptions(databasePath) { BusyTimeoutMilliseconds = 10_000 });
    }
}
