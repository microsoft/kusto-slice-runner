// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.Local.Sqlite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Tests
{
    public sealed class SqliteTransientRetryTests
    {
        [Fact]
        public void Retries_transient_error_then_returns_success()
        {
            var attempts = 0;
            var result = SqliteTransientRetry.Execute(() =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new SqliteException("query aborted", 4); // SQLITE_ABORT
                }

                return "ok";
            });

            Assert.Equal("ok", result);
            Assert.Equal(3, attempts);
        }

        [Fact]
        public void Gives_up_after_max_attempts_on_persistent_transient_error()
        {
            var attempts = 0;
            var ex = Assert.Throws<SqliteException>(() => SqliteTransientRetry.Execute(() =>
            {
                attempts++;
                throw new SqliteException("database is locked", 5); // SQLITE_BUSY
            }, maxAttempts: 3));

            Assert.Equal(5, ex.SqliteErrorCode);
            Assert.Equal(3, attempts);
        }

        [Fact]
        public void Does_not_retry_a_non_transient_sqlite_error()
        {
            var attempts = 0;
            Assert.Throws<SqliteException>(() => SqliteTransientRetry.Execute(() =>
            {
                attempts++;
                throw new SqliteException("constraint failed", 19); // SQLITE_CONSTRAINT
            }));

            Assert.Equal(1, attempts);
        }

        [Fact]
        public void Does_not_retry_a_non_sqlite_exception()
        {
            var attempts = 0;
            Assert.Throws<InvalidOperationException>(() => SqliteTransientRetry.Execute<int>(() =>
            {
                attempts++;
                throw new InvalidOperationException("boom");
            }));

            Assert.Equal(1, attempts);
        }
    }
}
