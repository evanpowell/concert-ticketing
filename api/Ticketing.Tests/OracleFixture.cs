using Testcontainers.Oracle;
using Ticketing.Api.Data;

namespace Ticketing.Tests;

public sealed class OracleFixture : IAsyncLifetime
{
    // Testcontainers 4.15: the parameterless OracleBuilder() is obsolete; the image
    // goes in the constructor. WithUsername/WithPassword are honoured by oracle-free.
    private readonly OracleContainer _container = new OracleBuilder("gvenzl/oracle-free:23-slim-faststart")
        .WithUsername("ticketing")
        .WithPassword("ticketing")
        .Build();

    public string ConnectionString { get; private set; } = "";

    // Verified 2026-09-13: dotnet new xunit on .NET 10 gives xUnit v2 (2.9.3),
    // whose IAsyncLifetime returns Task (v3 would return ValueTask).
    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        await MigrationRunner.ApplyAsync(ConnectionString, FindMigrationsDirectory());
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Test-only concern: locate db/migrations by walking up from the test binary.</summary>
    private static string FindMigrationsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "db", "migrations")))
            dir = dir.Parent;

        return dir is null
            ? throw new DirectoryNotFoundException("Could not locate db/migrations above the test binary.")
            : Path.Combine(dir.FullName, "db", "migrations");
    }
}

[CollectionDefinition(nameof(OracleCollection))]
public sealed class OracleCollection : ICollectionFixture<OracleFixture>;
