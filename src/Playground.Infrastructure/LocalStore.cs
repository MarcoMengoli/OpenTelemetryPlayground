using Microsoft.Data.Sqlite;

namespace Playground.Infrastructure;

// Every host owns its own database. Business schemas/transactions arrive in their stories.
public static class LocalStore
{
    public static async Task InitializeAsync(string path, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var connection = Open(path);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS infrastructure_probe (id INTEGER PRIMARY KEY CHECK(id = 1), starts INTEGER NOT NULL);
            INSERT INTO infrastructure_probe(id, starts) VALUES(1, 1)
            ON CONFLICT(id) DO UPDATE SET starts = starts + 1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    public static SqliteConnection Open(string path) => new(new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 5 }.ToString());
    public static async Task<long> ReadStartsAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var connection = Open(path);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT starts FROM infrastructure_probe WHERE id = 1";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }
}
