// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;

namespace Ksr.Local.Sqlite.Connections
{
    public interface IKsrSqliteConnectionFactory
    {
        SqliteConnection OpenConnection();
    }
}
