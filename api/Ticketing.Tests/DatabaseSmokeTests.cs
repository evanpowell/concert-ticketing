using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Data;

namespace Ticketing.Tests;

[Collection(nameof(OracleCollection))]
public class DatabaseSmokeTests(OracleFixture oracle)
{
    [Fact]
    public async Task Can_connect_and_read_seeded_data()
    {
        var options = new DbContextOptionsBuilder<TicketingDbContext>()
            .UseOracle(oracle.ConnectionString)
            .Options;

        await using var db = new TicketingDbContext(options);

        Assert.Equal(3, await db.Shows.CountAsync());
        Assert.Equal(300, await db.ShowSeats.CountAsync());
    }

    // Proves the database-clock read works before HoldService depends on it.
    // EF's SqlQuery requires the projected column to be named "Value" -- without
    // that alias this throws, and the failure is confusing if you meet it for the
    // first time inside the booking transaction.
    [Fact]
    public async Task Can_read_the_database_clock()
    {
        var options = new DbContextOptionsBuilder<TicketingDbContext>()
            .UseOracle(oracle.ConnectionString)
            .Options;

        await using var db = new TicketingDbContext(options);

        var now = await db.Database.SqlQueryRaw<DateTimeOffset>(
            """SELECT SYSTIMESTAMP AS "Value" FROM dual""").SingleAsync();

        Assert.InRange(now, DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddMinutes(10));
    }
}
