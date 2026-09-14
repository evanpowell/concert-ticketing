using Testcontainers.Oracle;

namespace Ticketing.Tests;

public sealed class OracleFixture : IAsyncLifetime
{
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
        await MigrationRunner.ApplyAsync(ConnectionString, MigrationRunner.FindMigrationsDirectory());
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition(nameof(OracleCollection))]
public sealed class OracleCollection : ICollectionFixture<OracleFixture>;
