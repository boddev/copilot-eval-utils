using EvalToolkit.EvalGen.Sources;
using Microsoft.Data.Sqlite;

namespace EvalToolkit.EvalGen.Tests.Sources;

public sealed class DatabaseSourceTests : IDisposable
{
    private readonly string _dbPath;

    public DatabaseSourceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"evalgen-db-{Guid.NewGuid():N}.sqlite");
        SeedDatabase(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static void SeedDatabase(string path)
    {
        var connStr = new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString;
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, age INTEGER);
                INSERT INTO users (id, name, age) VALUES (1, 'Ada', 36), (2, 'Bob', 41), (3, 'Cleo', 28);
                CREATE TABLE posts (id INTEGER PRIMARY KEY, title TEXT);
                INSERT INTO posts (id, title) VALUES (10, 'hello'), (11, 'world');
                """;
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task DiscoversTablesAndReturnsAllRows()
    {
        var source = new DatabaseSource(new DatabaseSourceOptions
        {
            Type = DatabaseType.Sqlite,
            ConnectionString = _dbPath,
        });

        var result = await source.FetchAsync();
        Assert.Equal(5, result.Records.Count);
        Assert.All(result.Records, r => Assert.True(r.ContainsKey("_source_table")));
        Assert.Contains(result.Records, r => (string?)r["_source_table"] == "users");
        Assert.Contains(result.Records, r => (string?)r["_source_table"] == "posts");
    }

    [Fact]
    public async Task RespectsExplicitTableList()
    {
        var source = new DatabaseSource(new DatabaseSourceOptions
        {
            Type = DatabaseType.Sqlite,
            ConnectionString = _dbPath,
            Tables = new[] { "users" },
        });

        var result = await source.FetchAsync();
        Assert.Equal(3, result.Records.Count);
        Assert.All(result.Records, r => Assert.Equal("users", (string?)r["_source_table"]));
    }

    [Fact]
    public async Task CapsRowsPerTable()
    {
        var source = new DatabaseSource(new DatabaseSourceOptions
        {
            Type = DatabaseType.Sqlite,
            ConnectionString = _dbPath,
            MaxRowsPerTable = 1,
        });

        var result = await source.FetchAsync();
        // 1 from users + 1 from posts
        Assert.Equal(2, result.Records.Count);
    }

    [Fact]
    public async Task ThrowsForUnsupportedDatabaseType()
    {
        var source = new DatabaseSource(new DatabaseSourceOptions
        {
            Type = DatabaseType.PostgreSql,
            ConnectionString = "Server=x",
        });

        await Assert.ThrowsAsync<NotSupportedException>(() => source.FetchAsync());
    }

    // Round-2: SQLite identifier escaping must round-trip table names that
    // contain double-quote characters. Regression for GPT-5.5 test-gap note.
    [Fact]
    public async Task EscapesDoubleQuotesInTableName()
    {
        var weirdDb = Path.Combine(Path.GetTempPath(), $"evalgen-quoted-{Guid.NewGuid():N}.sqlite");
        var connStr = new SqliteConnectionStringBuilder { DataSource = weirdDb }.ConnectionString;
        try
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE \"weird\"\"name\" (id INTEGER); INSERT INTO \"weird\"\"name\" VALUES (1), (2);";
                cmd.ExecuteNonQuery();
            }

            var source = new DatabaseSource(new DatabaseSourceOptions
            {
                Type = DatabaseType.Sqlite,
                ConnectionString = weirdDb,
                Tables = new[] { "weird\"name" },
            });

            var result = await source.FetchAsync();
            Assert.Equal(2, result.Records.Count);
            Assert.All(result.Records, r => Assert.Equal("weird\"name", (string?)r["_source_table"]));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(weirdDb)) File.Delete(weirdDb);
        }
    }
}
