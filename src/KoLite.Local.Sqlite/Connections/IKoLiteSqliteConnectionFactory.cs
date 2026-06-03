using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Connections
{
    public interface IKoLiteSqliteConnectionFactory
    {
        SqliteConnection OpenConnection();
    }
}
