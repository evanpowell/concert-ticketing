using Oracle.ManagedDataAccess.Client;

namespace Ticketing.Api.Data;

/// <summary>
/// Applies db/migrations/*.sql in filename order. Splits on ';', which is why the
/// migration files must not contain PL/SQL blocks. Safe to run against an empty
/// schema; against a populated one Oracle raises ORA-00955, which is treated as
/// "already applied" rather than an error.
/// </summary>
public static class MigrationRunner
{
    /// <summary>ORA-00955: name is already used by an existing object.</summary>
    private const int ObjectAlreadyExists = 955;

    public static async Task<bool> ApplyAsync(
        string connectionString, string migrationsDirectory, ILogger? logger = null)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync();

        foreach (var file in Directory.GetFiles(migrationsDirectory, "V*.sql").OrderBy(f => f))
        {
            foreach (var statement in SplitStatements(await File.ReadAllTextAsync(file)))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = statement;
                try
                {
                    await command.ExecuteNonQueryAsync();
                }
                catch (OracleException ex) when (ex.Number == ObjectAlreadyExists)
                {
                    logger?.LogInformation("Schema already present; skipping migrations.");
                    return false;
                }
            }
        }

        logger?.LogInformation("Migrations applied.");
        return true;
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
