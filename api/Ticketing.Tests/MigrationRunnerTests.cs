using Oracle.ManagedDataAccess.Client;
using Ticketing.Api.Data;

namespace Ticketing.Tests;

[Collection(nameof(OracleCollection))]
public class MigrationRunnerTests(OracleFixture oracle)
{
    /// <summary>
    /// The fixture database already has V001 and V002 applied. Adding a new
    /// migration file must apply it — otherwise no schema change could ever reach
    /// a database that already exists, which includes every deployed one.
    /// </summary>
    [Fact]
    public async Task A_new_migration_applies_to_an_already_migrated_database()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var probeTable = $"probe_{Guid.NewGuid():N}"[..16];

        try
        {
            foreach (var file in Directory.GetFiles(OracleFixture.MigrationsDirectory, "V*.sql"))
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));

            await File.WriteAllTextAsync(
                Path.Combine(directory, "V900__probe.sql"),
                $"CREATE TABLE {probeTable} (id NUMBER(10) PRIMARY KEY)");

            await MigrationRunner.ApplyAsync(oracle.ConnectionString, directory);

            Assert.True(await TableExistsAsync(probeTable), $"{probeTable} was not created");
        }
        finally
        {
            await DropTableIfExistsAsync(probeTable);
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Re-running must not duplicate seed data.</summary>
    [Fact]
    public async Task Re_running_migrations_does_not_reapply_them()
    {
        var before = await ScalarAsync("SELECT COUNT(*) FROM venue");

        await MigrationRunner.ApplyAsync(oracle.ConnectionString, OracleFixture.MigrationsDirectory);

        var after = await ScalarAsync("SELECT COUNT(*) FROM venue");
        Assert.Equal(before, after);
    }

    private async Task<decimal> ScalarAsync(string sql)
    {
        await using var connection = new OracleConnection(oracle.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToDecimal(await command.ExecuteScalarAsync());
    }

    private async Task<bool> TableExistsAsync(string table) =>
        await ScalarAsync($"SELECT COUNT(*) FROM user_tables WHERE table_name = '{table.ToUpperInvariant()}'") > 0;

    private async Task DropTableIfExistsAsync(string table)
    {
        if (!await TableExistsAsync(table)) return;
        await using var connection = new OracleConnection(oracle.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP TABLE {table}";
        await command.ExecuteNonQueryAsync();
    }
}
