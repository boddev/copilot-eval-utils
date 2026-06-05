using EvalToolkit.Core;
using Microsoft.Data.Sqlite;

namespace EvalToolkit.EvalGen.Sources;

/// <summary>
/// Port of <c>eval-gen/src/sources/db-source.ts</c>. Reads schema metadata
/// and samples rows from database tables.
/// <para>
/// The TS source called the <c>sqlite3</c> CLI via <c>child_process</c> (or
/// dynamically required <c>better-sqlite3</c>). This port uses
/// <c>Microsoft.Data.Sqlite</c>, the official managed ADO.NET provider, which
/// bundles SQLitePCLRaw so no native dependency is required at packaging
/// time. PostgreSQL / MSSQL remain explicitly unsupported — parity with the
/// TS error message guides users to export CSV/JSON.
/// </para>
/// </summary>
public sealed class DatabaseSource : IDataSource
{
    private readonly DatabaseSourceOptions _options;

    public DatabaseSource(DatabaseSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConnectionString);
        _options = options;
    }

    public Task<SourceResult> FetchAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _options.Type switch
        {
            DatabaseType.Sqlite => Task.FromResult(FetchSqlite(cancellationToken)),
            _ => throw new NotSupportedException(
                $"Database type \"{_options.Type.ToWireString()}\" is not yet implemented. " +
                "Currently supported: sqlite. Export your data as CSV/JSON and use --file instead."),
        };
    }

    private SourceResult FetchSqlite(CancellationToken cancellationToken)
    {
        var connStringBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = _options.ConnectionString,
            Mode = SqliteOpenMode.ReadOnly,
        };

        using var connection = new SqliteConnection(connStringBuilder.ConnectionString);
        connection.Open();

        var tables = _options.Tables is { Count: > 0 }
            ? _options.Tables
            : DiscoverTables(connection, cancellationToken);

        if (tables.Count == 0)
        {
            throw new InvalidOperationException("No tables found in database");
        }

        var allRecords = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var cmd = connection.CreateCommand();
            // SQLite identifier quoting: double-quote and escape inner double-quotes.
            cmd.CommandText = $"SELECT * FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\" LIMIT @max";
            cmd.Parameters.AddWithValue("@max", _options.MaxRowsPerTable);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    row[reader.GetName(i)] = value;
                }
                // Mirror TS: tag each record with originating table so the
                // pipeline can disambiguate downstream.
                row["_source_table"] = table;
                allRecords.Add(row);
            }
        }

        return new SourceResult(allRecords, InputFormat.Json, $"sqlite:{_options.ConnectionString}");
    }

    private static List<string> DiscoverTables(SqliteConnection connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        using var reader = cmd.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }
        return tables;
    }
}

/// <summary>
/// Supported database driver kinds. Only <see cref="Sqlite"/> is currently
/// implemented; the other values exist so the CLI shim can surface a clean
/// error message instead of crashing on string parsing.
/// </summary>
public enum DatabaseType
{
    Sqlite,
    PostgreSql,
    MsSql,
}

internal static class DatabaseTypeExtensions
{
    public static string ToWireString(this DatabaseType type) => type switch
    {
        DatabaseType.Sqlite => "sqlite",
        DatabaseType.PostgreSql => "postgresql",
        DatabaseType.MsSql => "mssql",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}

/// <summary>
/// Options for <see cref="DatabaseSource"/>. Mirrors the TS
/// <c>DatabaseSourceOptions</c> interface.
/// </summary>
public sealed class DatabaseSourceOptions
{
    public required DatabaseType Type { get; init; }
    public required string ConnectionString { get; init; }
    public IReadOnlyList<string>? Tables { get; init; }
    public int MaxRowsPerTable { get; init; } = 100;
}
