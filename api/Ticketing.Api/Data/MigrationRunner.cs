using Oracle.ManagedDataAccess.Client;

namespace Ticketing.Api.Data;

/// <summary>
/// Applies db/migrations/V*.sql in filename order, recording each in a
/// SCHEMA_VERSION table so that already-applied migrations are skipped and newly
/// added ones reach databases that already exist.
///
/// Statements are split on ';', so migration files must not contain PL/SQL blocks.
///
/// There is no baseline handling for databases created before version tracking
/// existed: version tracking was adopted before anything was deployed, so every
/// database starts empty and records all migrations from the beginning.
/// </summary>
public static class MigrationRunner
{
    /// <summary>ORA-00955: name is already used by an existing object.</summary>
    private const int ObjectAlreadyExists = 955;

    public static async Task<IReadOnlyList<string>> ApplyAsync(
        string connectionString, string migrationsDirectory, ILogger? logger = null)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync();

        await EnsureVersionTableAsync(connection);
        var applied = await AppliedVersionsAsync(connection);
        var newlyApplied = new List<string>();

        foreach (var file in Directory.GetFiles(migrationsDirectory, "V*.sql").OrderBy(f => f))
        {
            var version = Path.GetFileNameWithoutExtension(file);
            if (applied.Contains(version))
                continue;

            foreach (var statement in SplitStatements(await File.ReadAllTextAsync(file)))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = statement;
                await command.ExecuteNonQueryAsync();
            }

            await RecordVersionAsync(connection, version);
            newlyApplied.Add(version);
            logger?.LogInformation("Applied migration {Version}", version);
        }

        if (newlyApplied.Count == 0)
            logger?.LogInformation("Schema is up to date; no migrations to apply.");

        return newlyApplied;
    }

    private static async Task EnsureVersionTableAsync(OracleConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE schema_version (
              version    VARCHAR2(100) PRIMARY KEY,
              applied_at TIMESTAMP WITH TIME ZONE DEFAULT SYSTIMESTAMP NOT NULL
            )
            """;
        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (OracleException ex) when (ex.Number == ObjectAlreadyExists)
        {
            // Already there. Oracle has no CREATE TABLE IF NOT EXISTS.
        }
    }

    private static async Task<HashSet<string>> AppliedVersionsAsync(OracleConnection connection)
    {
        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_version";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            applied.Add(reader.GetString(0));

        return applied;
    }

    private static async Task RecordVersionAsync(OracleConnection connection, string version)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO schema_version (version) VALUES (:version)";
        command.Parameters.Add(new OracleParameter("version", version));
        await command.ExecuteNonQueryAsync();
    }

    private static IEnumerable<string> SplitStatements(string sql)
    {
        var withoutComments = string.Join(
            '\n',
            sql.Split('\n').Where(line => !line.TrimStart().StartsWith("--")));

        return withoutComments
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0 && !s.Equals("COMMIT", StringComparison.OrdinalIgnoreCase));
    }
}
