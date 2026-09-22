// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Connections
{
    public interface IKoLiteSqliteConnectionFactory
    {
        SqliteConnection OpenConnection();
    }
}
