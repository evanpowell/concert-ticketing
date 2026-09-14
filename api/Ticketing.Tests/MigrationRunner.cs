using Oracle.ManagedDataAccess.Client;

namespace Ticketing.Tests;

/// <summary>
/// Applies db/migrations/*.sql in filename order. Splits on ';', which is why
/// the migration files must not contain PL/SQL blocks.
/// </summary>
public static class MigrationRunner
{
    public static async Task ApplyAsync(string connectionString, string migrationsDirectory)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync();

        foreach (var file in Directory.GetFiles(migrationsDirectory, "V*.sql").OrderBy(f => f))
        {
            foreach (var statement in SplitStatements(await File.ReadAllTextAsync(file)))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = statement;
                await command.ExecuteNonQueryAsync();
            }
        }
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

    public static string FindMigrationsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "db", "migrations")))
            dir = dir.Parent;

        return dir is null
            ? throw new DirectoryNotFoundException("Could not locate db/migrations above the test binary.")
            : Path.Combine(dir.FullName, "db", "migrations");
    }
}
