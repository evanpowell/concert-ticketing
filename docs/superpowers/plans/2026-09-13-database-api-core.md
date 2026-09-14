# Database + API Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A working ASP.NET Core API over Oracle that sells concert seats, with an automated test proving that concurrent requests for the same seat produce exactly one winner.

**Architecture:** Minimal APIs over EF Core 10 with the Oracle provider. Schema lives in versioned `.sql` scripts, not EF Migrations. The booking path opens an explicit transaction, takes a pessimistic row lock with `SELECT ... FOR UPDATE NOWAIT`, and is the only place raw SQL appears. Every integration test runs against a real Oracle instance in Testcontainers — none of the concurrency behaviour is observable against an in-memory provider.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, EF Core 10 + `Oracle.EntityFrameworkCore`, Oracle Database 23ai Free (`gvenzl/oracle-free`), xUnit, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-09-12-concert-ticketing-design.md`

---

## Background for the implementer

Four things about this stack are non-obvious and cause most of the early confusion:

1. **Oracle identifiers are UPPERCASE.** Unquoted `create table venue` produces a table named `VENUE`. EF Core quotes identifiers by default, so it would look for `"Venues"` and fail. Every entity therefore needs explicit `ToTable`/`HasColumnName` mapping. This is verbose but not optional.

2. **A schema is a user.** There is no `CREATE SCHEMA` step — connecting as user `ticketing` puts you in schema `TICKETING`. The container creates that user for us via the `APP_USER` environment variable.

3. **`FOR UPDATE NOWAIT` fails loudly on purpose.** When another transaction holds the lock, Oracle raises `ORA-00054` immediately rather than waiting. That is the desired behaviour — we catch it and return 409. Code that "handles" it by retrying would defeat the design.

4. **CLR `string` is Unicode; your columns are not.** EF maps `string` to
   NVARCHAR2 by default and emits `N'...'` literals. The schema uses `VARCHAR2`.
   Oracle requires every branch of a `CASE` to share a character set, so a
   projection mixing `N'AVAILABLE'` with a `VARCHAR2` column fails with
   **ORA-12704: character set mismatch**. Worse, in a `WHERE` clause it does not
   fail — it silently converts and stops the index being used. The fix is one
   convention in the DbContext (`AreUnicode(false)`), applied model-wide.
   *(Hit for real on 2026-09-13 in Task 5.)*

5. **Time comes from the database, not the API process.** Hold expiry is compared against `SYSTIMESTAMP` read inside the transaction. If the API used its own clock, clock skew between API and database would produce wrong claimability decisions. This costs one extra round trip and buys correctness.

## File structure

```
db/
  migrations/
    V001__schema.sql               tables, constraints, indexes
    V002__seed.sql                 one venue, 3 shows, 100 seats/show
api/
  Ticketing.sln
  Ticketing.Api/
    Program.cs                     composition root only
    appsettings.json
    appsettings.Development.json
    Domain/
      SeatStatus.cs                status string constants
      HoldStatus.cs                status string constants
      Entities.cs                  the 7 entity classes
      HoldOutcome.cs               result types for HoldService
      HoldService.cs               THE CORE: hold / confirm / release
      ExpiredHoldSweeper.cs        BackgroundService
    Data/
      TicketingDbContext.cs
      Configurations/              one file per entity mapping
    Endpoints/
      HealthEndpoints.cs
      ShowEndpoints.cs
      HoldEndpoints.cs
      Contracts.cs                 request/response DTOs
  Ticketing.Tests/
    OracleFixture.cs               shared container + migrations
    MigrationRunner.cs             applies db/migrations/*.sql
    ApiFactory.cs                  WebApplicationFactory wiring
    HealthTests.cs
    ShowQueryTests.cs
    HoldConcurrencyTests.cs        THE CENTREPIECE
    HoldLifecycleTests.cs
    SweeperTests.cs
```

`HoldService.cs` is the only file where the interesting logic lives. Endpoints translate HTTP to service calls and back; they contain no business rules.

---

## Task 1: Install .NET 10 and scaffold the solution

**Files:**
- Create: `api/Ticketing.sln`, `api/Ticketing.Api/`, `api/Ticketing.Tests/`

- [ ] **Step 1: Install the .NET SDK**

```bash
brew install --cask dotnet-sdk
```

- [ ] **Step 2: Verify the SDK is on PATH and is version 10**

```bash
dotnet --version
```

Expected: `10.0.401` or later. If `command not found`, open a new shell — the cask adds `/usr/local/share/dotnet` to PATH via a new symlink.

- [ ] **Step 3: Create the solution and projects**

```bash
cd /Users/evan/other/ticketing
mkdir -p api && cd api
dotnet new sln --name Ticketing
dotnet new web --name Ticketing.Api --output Ticketing.Api
dotnet new xunit --name Ticketing.Tests --output Ticketing.Tests
dotnet sln add Ticketing.Api/Ticketing.Api.csproj Ticketing.Tests/Ticketing.Tests.csproj
dotnet add Ticketing.Tests/Ticketing.Tests.csproj reference Ticketing.Api/Ticketing.Api.csproj
```

- [ ] **Step 4: Add the NuGet packages**

```bash
cd /Users/evan/other/ticketing/api
dotnet add Ticketing.Api package Oracle.EntityFrameworkCore
dotnet add Ticketing.Api package Microsoft.EntityFrameworkCore.Design
dotnet add Ticketing.Tests package Microsoft.AspNetCore.Mvc.Testing
dotnet add Ticketing.Tests package Testcontainers.Oracle
dotnet add Ticketing.Tests package Oracle.ManagedDataAccess.Core

# The test project must pin EF Core to the SAME version the API resolves.
# Oracle.EntityFrameworkCore only requires EF Core >= 10.0.0, so without these
# the test project picks 10.0.0 while the API uses 10.0.12, and the build fails
# with CS1705 ("uses a higher version than referenced assembly").
dotnet add Ticketing.Tests package Microsoft.EntityFrameworkCore --version 10.0.12
dotnet add Ticketing.Tests package Microsoft.EntityFrameworkCore.Relational --version 10.0.12
```

- [ ] **Step 5: Verify the solution builds**

```bash
cd /Users/evan/other/ticketing/api && dotnet build
```

Expected: `Build succeeded`. Warnings are acceptable; errors are not.

- [ ] **Step 6: Note the xUnit version**

```bash
grep -i 'xunit' Ticketing.Tests/Ticketing.Tests.csproj
```

**Verified on 2026-09-13:** this produces xUnit **v2 (2.9.3)**, whose `IAsyncLifetime` methods return `Task`. Task 4's fixture is written for that. If a future SDK gives you `xunit.v3` instead, change both `Task` returns to `ValueTask` in `OracleFixture.cs` — nothing else is affected.

- [ ] **Step 7: Commit**

```bash
cd /Users/evan/other/ticketing
git add api .gitignore
git commit -m "chore: scaffold API solution and test project"
```

---

## Task 2: Oracle running locally with schema and seed data

**Files:**
- Create: `compose.yaml`, `db/migrations/V001__schema.sql`, `db/migrations/V002__seed.sql`

- [ ] **Step 1: Write the compose file**

Create `compose.yaml`:

```yaml
services:
  oracle:
    image: gvenzl/oracle-free:23-slim-faststart
    ports:
      - "1521:1521"
    environment:
      ORACLE_PASSWORD: oracle
      APP_USER: ticketing
      APP_USER_PASSWORD: ticketing
    volumes:
      - oracle-data:/opt/oracle/oradata
    healthcheck:
      test: ["CMD", "healthcheck.sh"]
      interval: 10s
      timeout: 5s
      retries: 30
      start_period: 30s

volumes:
  oracle-data:
```

Note: migrations are deliberately **not** mounted into `/container-entrypoint-initdb.d`. Those scripts run only on first boot of an empty volume, which makes iterating on the schema painful and hides failures. We apply migrations explicitly instead — the same runner the tests use.

- [ ] **Step 2: Start Oracle and wait for healthy**

```bash
cd /Users/evan/other/ticketing
docker compose up -d
docker compose ps
```

Expected: eventually `STATUS` shows `(healthy)`. **First boot takes 1–3 minutes.** Watch progress with `docker compose logs -f oracle`; the line `DATABASE IS READY TO USE!` means it is done.

- [ ] **Step 3: Verify you can connect as the app user**

```bash
docker compose exec oracle sqlplus -S ticketing/ticketing@localhost:1521/FREEPDB1 <<'SQL'
SELECT user FROM dual;
EXIT;
SQL
```

Expected: `TICKETING`. If this fails with `ORA-01017`, the `APP_USER` variables did not take effect — destroy the volume with `docker compose down -v` and start again, since those variables apply only on first boot.

- [ ] **Step 4: Write the schema migration**

Create `db/migrations/V001__schema.sql`:

```sql
-- Concert ticketing schema.
-- One statement per semicolon. No PL/SQL blocks: the migration runner
-- splits on ';' and a block would be split in the middle.

CREATE TABLE venue (
  venue_id NUMBER(10) GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
  name     VARCHAR2(200) NOT NULL,
  city     VARCHAR2(100) NOT NULL
);

CREATE TABLE seat (
  seat_id     NUMBER(10) GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
  venue_id    NUMBER(10) NOT NULL REFERENCES venue(venue_id),
  section     VARCHAR2(20) NOT NULL,
  row_label   VARCHAR2(10) NOT NULL,
  seat_number NUMBER(4) NOT NULL,
  CONSTRAINT uq_seat_position UNIQUE (venue_id, section, row_label, seat_number)
);

CREATE TABLE show_event (
  show_id   NUMBER(10) GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
  venue_id  NUMBER(10) NOT NULL REFERENCES venue(venue_id),
  title     VARCHAR2(200) NOT NULL,
  artist    VARCHAR2(200) NOT NULL,
  starts_at TIMESTAMP WITH TIME ZONE NOT NULL
);

CREATE TABLE seat_hold (
  hold_id    NUMBER(10) GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
  show_id    NUMBER(10) NOT NULL REFERENCES show_event(show_id),
  email      VARCHAR2(320) NOT NULL,
  created_at TIMESTAMP WITH TIME ZONE NOT NULL,
  expires_at TIMESTAMP WITH TIME ZONE NOT NULL,
  status     VARCHAR2(10) NOT NULL,
  CONSTRAINT ck_hold_status CHECK (status IN ('ACTIVE','CONFIRMED','RELEASED','EXPIRED'))
);

CREATE TABLE show_seat (
  show_seat_id NUMBER(10) GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
  show_id      NUMBER(10) NOT NULL REFERENCES show_event(show_id),
  seat_id      NUMBER(10) NOT NULL REFERENCES seat(seat_id),
  status       VARCHAR2(10) NOT NULL,
  price_cents  NUMBER(10) NOT NULL,
  hold_id      NUMBER(10) REFERENCES seat_hold(hold_id),
  expires_at   TIMESTAMP WITH TIME ZONE,
  CONSTRAINT uq_show_seat UNIQUE (show_id, seat_id),
  CONSTRAINT ck_show_seat_status CHECK (status IN ('AVAILABLE','HELD','SOLD')),
  CONSTRAINT ck_held_consistency CHECK (
    (status = 'HELD' AND hold_id IS NOT NULL AND expires_at IS NOT NULL)
    OR (status <> 'HELD' AND hold_id IS NULL AND expires_at IS NULL)
  )
);

CREATE TABLE customer_order (
  order_id    NUMBER(10) GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
  hold_id     NUMBER(10) NOT NULL UNIQUE REFERENCES seat_hold(hold_id),
  email       VARCHAR2(320) NOT NULL,
  total_cents NUMBER(10) NOT NULL,
  created_at  TIMESTAMP WITH TIME ZONE NOT NULL
);

CREATE TABLE ticket (
  ticket_id    NUMBER(10) GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
  order_id     NUMBER(10) NOT NULL REFERENCES customer_order(order_id),
  show_seat_id NUMBER(10) NOT NULL REFERENCES show_seat(show_seat_id),
  price_cents  NUMBER(10) NOT NULL,
  CONSTRAINT uq_ticket_show_seat UNIQUE (show_seat_id)
);

CREATE INDEX ix_show_seat_show_status ON show_seat (show_id, status);

CREATE INDEX ix_show_seat_expiry ON show_seat (status, expires_at);
```

`ck_held_consistency` is worth understanding: it makes "HELD without a hold" and "available with a stale hold id" unrepresentable. The database enforces the invariant, so no code path can violate it.

`uq_ticket_show_seat` is the double-sell net from the spec.

- [ ] **Step 5: Write the seed migration**

Create `db/migrations/V002__seed.sql`:

```sql
-- Reference data. Pure SQL, no PL/SQL: seats are generated with CONNECT BY.

INSERT INTO venue (name, city) VALUES ('The Chapel', 'San Francisco');

INSERT INTO seat (venue_id, section, row_label, seat_number)
SELECT v.venue_id, s.section, r.row_label, n.seat_number
  FROM venue v
 CROSS JOIN (SELECT 'A' AS section FROM dual UNION ALL SELECT 'B' FROM dual) s
 CROSS JOIN (SELECT TO_CHAR(LEVEL) AS row_label FROM dual CONNECT BY LEVEL <= 5) r
 CROSS JOIN (SELECT LEVEL AS seat_number FROM dual CONNECT BY LEVEL <= 10) n
 WHERE v.name = 'The Chapel';

INSERT INTO show_event (venue_id, title, artist, starts_at)
SELECT venue_id, 'Winter Session', 'The Gloaming', TIMESTAMP '2026-11-14 20:00:00 -08:00' FROM venue WHERE name = 'The Chapel';

INSERT INTO show_event (venue_id, title, artist, starts_at)
SELECT venue_id, 'Late Set', 'Lankum', TIMESTAMP '2026-11-21 21:00:00 -08:00' FROM venue WHERE name = 'The Chapel';

INSERT INTO show_event (venue_id, title, artist, starts_at)
SELECT venue_id, 'Solstice Night', 'Caoimhin O Raghallaigh', TIMESTAMP '2026-12-19 20:30:00 -08:00' FROM venue WHERE name = 'The Chapel';

INSERT INTO show_seat (show_id, seat_id, status, price_cents)
SELECT e.show_id, s.seat_id, 'AVAILABLE',
       CASE s.section WHEN 'A' THEN 8500 ELSE 6000 END
  FROM show_event e
  JOIN seat s ON s.venue_id = e.venue_id;

COMMIT;
```

That produces 100 seats per venue and 300 `show_seat` rows across 3 shows.

- [ ] **Step 6: Apply the migrations manually and verify**

```bash
cd /Users/evan/other/ticketing
docker compose exec -T oracle sqlplus -S ticketing/ticketing@localhost:1521/FREEPDB1 < db/migrations/V001__schema.sql
docker compose exec -T oracle sqlplus -S ticketing/ticketing@localhost:1521/FREEPDB1 < db/migrations/V002__seed.sql
```

- [ ] **Step 7: Confirm the data landed**

```bash
docker compose exec -T oracle sqlplus -S ticketing/ticketing@localhost:1521/FREEPDB1 <<'SQL'
SET PAGESIZE 50
SELECT table_name FROM user_tables ORDER BY table_name;
SELECT COUNT(*) AS seats FROM seat;
SELECT COUNT(*) AS show_seats FROM show_seat;
EXIT;
SQL
```

Expected: 7 tables (`CUSTOMER_ORDER`, `SEAT`, `SEAT_HOLD`, `SHOW_EVENT`, `SHOW_SEAT`, `TICKET`, `VENUE`), `SEATS` = 100, `SHOW_SEATS` = 300.

- [ ] **Step 8: Commit**

```bash
git add compose.yaml db/
git commit -m "feat: Oracle schema and seed data"
```

---

## Task 3: Health endpoint

Start with the endpoint that has no database dependency, to prove the HTTP test harness works before adding Oracle to the picture.

**Files:**
- Create: `api/Ticketing.Api/Endpoints/HealthEndpoints.cs`
- Modify: `api/Ticketing.Api/Program.cs`
- Test: `api/Ticketing.Tests/HealthTests.cs`

- [ ] **Step 1: Write the failing test**

Create `api/Ticketing.Tests/HealthTests.cs`:

```csharp
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ticketing.Tests;

public class HealthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Health_returns_ok()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd /Users/evan/other/ticketing/api && dotnet test --filter Health_returns_ok
```

Expected: FAIL. Either a compile error that `Program` is inaccessible, or a 404.

- [ ] **Step 3: Write the endpoint**

Create `api/Ticketing.Api/Endpoints/HealthEndpoints.cs`:

```csharp
namespace Ticketing.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
    }
}
```

- [ ] **Step 4: Wire it up and expose Program to tests**

Replace the contents of `api/Ticketing.Api/Program.cs`:

```csharp
using Ticketing.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapHealthEndpoints();

app.Run();

// Exposes the implicit Program class to WebApplicationFactory in the test project.
public partial class Program;
```

- [ ] **Step 5: Run the test to verify it passes**

```bash
cd /Users/evan/other/ticketing/api && dotnet test --filter Health_returns_ok
```

Expected: PASS, 1 test.

- [ ] **Step 6: Commit**

```bash
cd /Users/evan/other/ticketing
git add api/
git commit -m "feat: health endpoint"
```

---

## Task 4: EF Core wired to Oracle, verified by a real container

This task builds the test infrastructure every later task depends on. It is the longest one; take it in order.

**Files:**
- Create: `api/Ticketing.Api/Domain/SeatStatus.cs`, `Domain/HoldStatus.cs`, `Domain/Entities.cs`
- Create: `api/Ticketing.Api/Data/TicketingDbContext.cs`
- Create: `api/Ticketing.Tests/MigrationRunner.cs`, `OracleFixture.cs`, `ApiFactory.cs`
- Modify: `api/Ticketing.Api/Program.cs`, `appsettings.Development.json`

- [ ] **Step 1: Write the status constants**

Create `api/Ticketing.Api/Domain/SeatStatus.cs`:

```csharp
namespace Ticketing.Api.Domain;

public static class SeatStatus
{
    public const string Available = "AVAILABLE";
    public const string Held = "HELD";
    public const string Sold = "SOLD";
}
```

Create `api/Ticketing.Api/Domain/HoldStatus.cs`:

```csharp
namespace Ticketing.Api.Domain;

public static class HoldStatus
{
    public const string Active = "ACTIVE";
    public const string Confirmed = "CONFIRMED";
    public const string Released = "RELEASED";
    public const string Expired = "EXPIRED";
}
```

These are string constants rather than a C# enum because the database `CHECK` constraints are the source of truth for the allowed values, and matching them exactly avoids a mapping layer that could drift.

- [ ] **Step 2: Write the entities**

Create `api/Ticketing.Api/Domain/Entities.cs`:

```csharp
namespace Ticketing.Api.Domain;

public class Venue
{
    public int VenueId { get; set; }
    public string Name { get; set; } = "";
    public string City { get; set; } = "";
}

public class Seat
{
    public int SeatId { get; set; }
    public int VenueId { get; set; }
    public string Section { get; set; } = "";
    public string RowLabel { get; set; } = "";
    public int SeatNumber { get; set; }
}

public class ShowEvent
{
    public int ShowId { get; set; }
    public int VenueId { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public DateTimeOffset StartsAt { get; set; }
    public Venue Venue { get; set; } = null!;
}

public class ShowSeat
{
    public int ShowSeatId { get; set; }
    public int ShowId { get; set; }
    public int SeatId { get; set; }
    public string Status { get; set; } = SeatStatus.Available;
    public int PriceCents { get; set; }
    public int? HoldId { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public Seat Seat { get; set; } = null!;
}

public class SeatHold
{
    public int HoldId { get; set; }
    public int ShowId { get; set; }
    public string Email { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Status { get; set; } = HoldStatus.Active;
}

public class CustomerOrder
{
    public int OrderId { get; set; }
    public int HoldId { get; set; }
    public string Email { get; set; } = "";
    public int TotalCents { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class Ticket
{
    public int TicketId { get; set; }
    public int OrderId { get; set; }
    public int ShowSeatId { get; set; }
    public int PriceCents { get; set; }
}
```

- [ ] **Step 3: Write the DbContext with explicit Oracle mappings**

Create `api/Ticketing.Api/Data/TicketingDbContext.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Data;

public class TicketingDbContext(DbContextOptions<TicketingDbContext> options) : DbContext(options)
{
    public DbSet<Venue> Venues => Set<Venue>();
    public DbSet<Seat> Seats => Set<Seat>();
    public DbSet<ShowEvent> Shows => Set<ShowEvent>();
    public DbSet<ShowSeat> ShowSeats => Set<ShowSeat>();
    public DbSet<SeatHold> SeatHolds => Set<SeatHold>();
    public DbSet<CustomerOrder> Orders => Set<CustomerOrder>();
    public DbSet<Ticket> Tickets => Set<Ticket>();

    /// <summary>
    /// Every string column in this schema is VARCHAR2 (database character set),
    /// not NVARCHAR2. EF maps CLR string to Unicode by default and emits N'...'
    /// literals, which Oracle rejects inside a CASE expression with ORA-12704,
    /// and which silently defeat index use in a WHERE clause.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<string>().AreUnicode(false);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Venue>(e =>
        {
            e.ToTable("VENUE");
            e.HasKey(x => x.VenueId);
            e.Property(x => x.VenueId).HasColumnName("VENUE_ID").ValueGeneratedOnAdd();
            e.Property(x => x.Name).HasColumnName("NAME");
            e.Property(x => x.City).HasColumnName("CITY");
        });

        b.Entity<Seat>(e =>
        {
            e.ToTable("SEAT");
            e.HasKey(x => x.SeatId);
            e.Property(x => x.SeatId).HasColumnName("SEAT_ID").ValueGeneratedOnAdd();
            e.Property(x => x.VenueId).HasColumnName("VENUE_ID");
            e.Property(x => x.Section).HasColumnName("SECTION");
            e.Property(x => x.RowLabel).HasColumnName("ROW_LABEL");
            e.Property(x => x.SeatNumber).HasColumnName("SEAT_NUMBER");
        });

        b.Entity<ShowEvent>(e =>
        {
            e.ToTable("SHOW_EVENT");
            e.HasKey(x => x.ShowId);
            e.Property(x => x.ShowId).HasColumnName("SHOW_ID").ValueGeneratedOnAdd();
            e.Property(x => x.VenueId).HasColumnName("VENUE_ID");
            e.Property(x => x.Title).HasColumnName("TITLE");
            e.Property(x => x.Artist).HasColumnName("ARTIST");
            e.Property(x => x.StartsAt).HasColumnName("STARTS_AT");
            e.HasOne(x => x.Venue).WithMany().HasForeignKey(x => x.VenueId);
        });

        b.Entity<ShowSeat>(e =>
        {
            e.ToTable("SHOW_SEAT");
            e.HasKey(x => x.ShowSeatId);
            e.Property(x => x.ShowSeatId).HasColumnName("SHOW_SEAT_ID").ValueGeneratedOnAdd();
            e.Property(x => x.ShowId).HasColumnName("SHOW_ID");
            e.Property(x => x.SeatId).HasColumnName("SEAT_ID");
            e.Property(x => x.Status).HasColumnName("STATUS");
            e.Property(x => x.PriceCents).HasColumnName("PRICE_CENTS");
            e.Property(x => x.HoldId).HasColumnName("HOLD_ID");
            e.Property(x => x.ExpiresAt).HasColumnName("EXPIRES_AT");
            e.HasOne(x => x.Seat).WithMany().HasForeignKey(x => x.SeatId);
        });

        b.Entity<SeatHold>(e =>
        {
            e.ToTable("SEAT_HOLD");
            e.HasKey(x => x.HoldId);
            e.Property(x => x.HoldId).HasColumnName("HOLD_ID").ValueGeneratedOnAdd();
            e.Property(x => x.ShowId).HasColumnName("SHOW_ID");
            e.Property(x => x.Email).HasColumnName("EMAIL");
            e.Property(x => x.CreatedAt).HasColumnName("CREATED_AT");
            e.Property(x => x.ExpiresAt).HasColumnName("EXPIRES_AT");
            e.Property(x => x.Status).HasColumnName("STATUS");
        });

        b.Entity<CustomerOrder>(e =>
        {
            e.ToTable("CUSTOMER_ORDER");
            e.HasKey(x => x.OrderId);
            e.Property(x => x.OrderId).HasColumnName("ORDER_ID").ValueGeneratedOnAdd();
            e.Property(x => x.HoldId).HasColumnName("HOLD_ID");
            e.Property(x => x.Email).HasColumnName("EMAIL");
            e.Property(x => x.TotalCents).HasColumnName("TOTAL_CENTS");
            e.Property(x => x.CreatedAt).HasColumnName("CREATED_AT");
        });

        b.Entity<Ticket>(e =>
        {
            e.ToTable("TICKET");
            e.HasKey(x => x.TicketId);
            e.Property(x => x.TicketId).HasColumnName("TICKET_ID").ValueGeneratedOnAdd();
            e.Property(x => x.OrderId).HasColumnName("ORDER_ID");
            e.Property(x => x.ShowSeatId).HasColumnName("SHOW_SEAT_ID");
            e.Property(x => x.PriceCents).HasColumnName("PRICE_CENTS");
        });
    }
}
```

- [ ] **Step 4: Register the DbContext**

Replace `api/Ticketing.Api/Program.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Data;
using Ticketing.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<TicketingDbContext>(options =>
    options.UseOracle(builder.Configuration.GetConnectionString("Ticketing")));

var app = builder.Build();

app.MapHealthEndpoints();

app.Run();

public partial class Program;
```

Add the connection string to `api/Ticketing.Api/appsettings.Development.json`:

```json
{
  "ConnectionStrings": {
    "Ticketing": "User Id=ticketing;Password=ticketing;Data Source=localhost:1521/FREEPDB1"
  }
}
```

- [ ] **Step 5: Write the migration runner used by tests**

Create `api/Ticketing.Tests/MigrationRunner.cs`:

```csharp
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
```

`COMMIT` is filtered out because ODP.NET autocommits each statement outside an explicit transaction; leaving it in raises `ORA-00900`.

- [ ] **Step 6: Write the Oracle fixture**

Create `api/Ticketing.Tests/OracleFixture.cs`. Oracle start-up is slow, so one container is shared by the whole suite:

```csharp
using Testcontainers.Oracle;

namespace Ticketing.Tests;

public sealed class OracleFixture : IAsyncLifetime
{
    // Testcontainers 4.15: the parameterless OracleBuilder() is obsolete; the image
    // goes in the constructor. Verified 2026-09-13 that WithUsername/WithPassword
    // are honoured by the oracle-free image.
    private readonly OracleContainer _container = new OracleBuilder("gvenzl/oracle-free:23-slim-faststart")
        .WithUsername("ticketing")
        .WithPassword("ticketing")
        .Build();

    public string ConnectionString { get; private set; } = "";

    // Verified 2026-09-13: `dotnet new xunit` on .NET 10 gives xUnit v2 (2.9.3),
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
```

- [ ] **Step 7: Write a connectivity smoke test**

This exists to isolate "can we reach Oracle at all" from every later failure. Create `api/Ticketing.Tests/DatabaseSmokeTests.cs`:

```csharp
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
    // EF's SqlQuery requires the projected column to be named "Value" — without
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
```

- [ ] **Step 8: Run the smoke test**

```bash
cd /Users/evan/other/ticketing/api && dotnet test --filter Can_connect_and_read_seeded_data
```

Expected: PASS. **This will take 1–3 minutes** while Testcontainers pulls and boots Oracle.

**Verified 2026-09-13:** this passes. `WithUsername`/`WithPassword` are honoured by the `oracle-free` image, and the container is genuinely fresh — if it had connected to the local compose instance instead, the migrations would have failed with `ORA-00955` (name already used by an existing object).

- [ ] **Step 9: Write the API factory that points the app at the test container**

Create `api/Ticketing.Tests/ApiFactory.cs`:

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Ticketing.Tests;

public sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Ticketing", connectionString);
        builder.UseEnvironment("Development");
    }
}
```

- [ ] **Step 10: Commit**

```bash
cd /Users/evan/other/ticketing
git add api/
git commit -m "feat: EF Core wired to Oracle with Testcontainers integration harness"
```

---

## Task 5: Read endpoints — shows and seat map

**Files:**
- Create: `api/Ticketing.Api/Endpoints/Contracts.cs`, `api/Ticketing.Api/Endpoints/ShowEndpoints.cs`
- Modify: `api/Ticketing.Api/Program.cs`
- Test: `api/Ticketing.Tests/ShowQueryTests.cs`

**Test isolation note.** Every test class shares one Oracle container, and xUnit
gives no ordering guarantee within a collection. A test that asserts "all seats are
AVAILABLE" would therefore pass or fail depending on whether a booking test ran
first. Read tests assert on things that do not change — seat counts, sections,
prices — and never on a specific seat's status.

- [ ] **Step 1: Write the failing tests**

Create `api/Ticketing.Tests/ShowQueryTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;

namespace Ticketing.Tests;

[Collection(nameof(OracleCollection))]
public class ShowQueryTests(OracleFixture oracle)
{
    [Fact]
    public async Task Lists_all_seeded_shows()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();

        var shows = await client.GetFromJsonAsync<List<ShowSummaryDto>>("/api/shows");

        Assert.NotNull(shows);
        Assert.Equal(3, shows!.Count);
        Assert.Contains(shows, s => s.Artist == "Lankum");
    }

    [Fact]
    public async Task Seat_map_returns_one_hundred_available_seats()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();

        var shows = await client.GetFromJsonAsync<List<ShowSummaryDto>>("/api/shows");
        var seats = await client.GetFromJsonAsync<List<SeatDto>>($"/api/shows/{shows![0].ShowId}/seats");

        Assert.NotNull(seats);
        Assert.Equal(100, seats!.Count);
        Assert.Contains(seats, s => s.Section == "A" && s.PriceCents == 8500);
        Assert.Contains(seats, s => s.Section == "B" && s.PriceCents == 6000);
        Assert.All(seats, s => Assert.Contains(s.Status, new[] { "AVAILABLE", "HELD", "SOLD" }));
    }

    [Fact]
    public async Task Show_detail_returns_the_requested_show()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();

        var shows = await client.GetFromJsonAsync<List<ShowSummaryDto>>("/api/shows");
        var show = await client.GetFromJsonAsync<ShowSummaryDto>($"/api/shows/{shows![0].ShowId}");

        Assert.NotNull(show);
        Assert.Equal(shows[0].ShowId, show!.ShowId);
        Assert.Equal(shows[0].Artist, show.Artist);
    }

    [Fact]
    public async Task Unknown_show_returns_404()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/shows/999999/seats");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public record ShowSummaryDto(int ShowId, string Title, string Artist, DateTimeOffset StartsAt, string VenueName);
    public record SeatDto(int ShowSeatId, string Section, string RowLabel, int SeatNumber, string Status, int PriceCents);
}
```

- [ ] **Step 2: Run to verify they fail**

```bash
cd /Users/evan/other/ticketing/api && dotnet test --filter ShowQueryTests
```

Expected: FAIL — 404 on `/api/shows`, since the endpoints do not exist.

- [ ] **Step 3: Write the contracts**

Create `api/Ticketing.Api/Endpoints/Contracts.cs`:

```csharp
namespace Ticketing.Api.Endpoints;

public record ShowSummary(int ShowId, string Title, string Artist, DateTimeOffset StartsAt, string VenueName);

public record SeatView(int ShowSeatId, string Section, string RowLabel, int SeatNumber, string Status, int PriceCents);

public record CreateHoldRequest(int[] SeatIds, string Email);

public record HoldView(int HoldId, DateTimeOffset ExpiresAt, int[] ShowSeatIds);

public record OrderView(int OrderId, string Email, int TotalCents, DateTimeOffset CreatedAt, TicketView[] Tickets);

public record TicketView(int TicketId, int ShowSeatId, string Section, string RowLabel, int SeatNumber, int PriceCents);
```

- [ ] **Step 4: Write the endpoints**

Create `api/Ticketing.Api/Endpoints/ShowEndpoints.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Endpoints;

public static class ShowEndpoints
{
    public static void MapShowEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/shows");

        group.MapGet("/", async (TicketingDbContext db, CancellationToken ct) =>
            await db.Shows
                .OrderBy(s => s.StartsAt)
                .Select(s => new ShowSummary(s.ShowId, s.Title, s.Artist, s.StartsAt, s.Venue.Name))
                .ToListAsync(ct));

        group.MapGet("/{showId:int}", async (int showId, TicketingDbContext db, CancellationToken ct) =>
        {
            var show = await db.Shows
                .Where(s => s.ShowId == showId)
                .Select(s => new ShowSummary(s.ShowId, s.Title, s.Artist, s.StartsAt, s.Venue.Name))
                .FirstOrDefaultAsync(ct);

            return show is null
                ? Results.Problem(statusCode: 404, title: "Show not found")
                : Results.Ok(show);
        });

        group.MapGet("/{showId:int}/seats", async (int showId, TicketingDbContext db, CancellationToken ct) =>
        {
            var showExists = await db.Shows.AnyAsync(s => s.ShowId == showId, ct);
            if (!showExists)
                return Results.Problem(statusCode: 404, title: "Show not found");

            var seats = await db.ShowSeats
                .Where(ss => ss.ShowId == showId)
                .OrderBy(ss => ss.Seat.Section).ThenBy(ss => ss.Seat.RowLabel).ThenBy(ss => ss.Seat.SeatNumber)
                .Select(ss => new SeatView(
                    ss.ShowSeatId,
                    ss.Seat.Section,
                    ss.Seat.RowLabel,
                    ss.Seat.SeatNumber,
                    // A hold that has expired but not yet been swept reads as available.
                    ss.Status == SeatStatus.Held && ss.ExpiresAt != null && ss.ExpiresAt <= DateTimeOffset.UtcNow
                        ? SeatStatus.Available
                        : ss.Status,
                    ss.PriceCents))
                .ToListAsync(ct);

            return Results.Ok(seats);
        });
    }
}
```

The expiry translation in the projection matters: without it the seat map would show seats as taken for up to 60 seconds after their hold expired, because the sweeper has not run yet. This is the *cosmetic* half of the two-way expiry rule from the spec — it affects display only, never a booking decision.

- [ ] **Step 5: Register the endpoints**

In `api/Ticketing.Api/Program.cs`, add below `app.MapHealthEndpoints();`:

```csharp
app.MapShowEndpoints();
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
cd /Users/evan/other/ticketing/api && dotnet test --filter ShowQueryTests
```

Expected: PASS, 4 tests.

- [ ] **Step 7: Commit**

```bash
cd /Users/evan/other/ticketing
git add api/
git commit -m "feat: shows list and seat map endpoints"
```

---

## Task 6: The hold transaction — the centrepiece

This is the task the whole project exists for. Write the concurrency test first; it is the specification.

**Files:**
- Create: `api/Ticketing.Api/Domain/HoldOutcome.cs`, `api/Ticketing.Api/Domain/HoldService.cs`, `api/Ticketing.Api/Endpoints/HoldEndpoints.cs`
- Modify: `api/Ticketing.Api/Program.cs`
- Test: `api/Ticketing.Tests/HoldConcurrencyTests.cs`

- [ ] **Step 1: Write the failing concurrency test**

Create `api/Ticketing.Tests/HoldConcurrencyTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;

namespace Ticketing.Tests;

[Collection(nameof(OracleCollection))]
public class HoldConcurrencyTests(OracleFixture oracle)
{
    private const int Attempts = 10;

    [Fact]
    public async Task Exactly_one_of_many_concurrent_holds_on_the_same_seat_wins()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);

        var (showId, seatId) = await TestData.PickAvailableSeatAsync(oracle.ConnectionString);

        // Release all racers at the same instant so they genuinely collide.
        var gate = new TaskCompletionSource();

        var racers = Enumerable.Range(0, Attempts).Select(async i =>
        {
            var client = factory.CreateClient();
            await gate.Task;
            return await client.PostAsJsonAsync(
                $"/api/shows/{showId}/holds",
                new { seatIds = new[] { seatId }, email = $"racer{i}@example.com" });
        }).ToArray();

        gate.SetResult();
        var responses = await Task.WhenAll(racers);

        var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var conflicted = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        Assert.Equal(1, created);
        Assert.Equal(Attempts - 1, conflicted);
    }
}
```

**Why both losing paths produce 409.** A loser can fail two different ways, and the test is correct under either:

- It reaches `FOR UPDATE NOWAIT` while the winner still holds the lock → `ORA-00054` → 409.
- It arrives after the winner committed, acquires the lock cleanly, and finds the seat already `HELD` → 409.

Which one happens depends on timing you do not control. Asserting only on the status code keeps the test deterministic.

- [ ] **Step 2: Write the test data helper**

Create `api/Ticketing.Tests/TestData.cs`:

```csharp
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Oracle.ManagedDataAccess.Client;
using Ticketing.Api.Data;

namespace Ticketing.Tests;

public static class TestData
{
    /// <summary>Returns a (showId, seatId) pair that is currently AVAILABLE.</summary>
    public static async Task<(int ShowId, int SeatId)> PickAvailableSeatAsync(string connectionString)
    {
        await using var db = NewContext(connectionString);

        var row = await db.ShowSeats
            .Where(ss => ss.Status == "AVAILABLE")
            .OrderBy(ss => ss.ShowSeatId)
            .Select(ss => new { ss.ShowId, ss.SeatId })
            .FirstAsync();

        return (row.ShowId, row.SeatId);
    }

    public static TicketingDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<TicketingDbContext>().UseOracle(connectionString).Options);

    /// <summary>Backdates a hold and its seats so expiry paths can be tested without waiting.</summary>
    public static async Task ExpireHoldAsync(string connectionString, int holdId)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE seat_hold SET expires_at = SYSTIMESTAMP - INTERVAL '1' MINUTE WHERE hold_id = :holdId
            """;
        command.Parameters.Add(new OracleParameter("holdId", holdId));
        await command.ExecuteNonQueryAsync();

        await using var seats = connection.CreateCommand();
        seats.CommandText = """
            UPDATE show_seat SET expires_at = SYSTIMESTAMP - INTERVAL '1' MINUTE WHERE hold_id = :holdId
            """;
        seats.Parameters.Add(new OracleParameter("holdId", holdId));
        await seats.ExecuteNonQueryAsync();
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

```bash
cd /Users/evan/other/ticketing/api && dotnet test --filter Exactly_one_of_many_concurrent_holds
```

Expected: FAIL — all 10 requests return 404, since `POST /api/shows/{id}/holds` does not exist.

- [ ] **Step 4: Write the outcome types**

Create `api/Ticketing.Api/Domain/HoldOutcome.cs`:

```csharp
namespace Ticketing.Api.Domain;

public abstract record HoldOutcome
{
    public sealed record Created(int HoldId, DateTimeOffset ExpiresAt, int[] ShowSeatIds) : HoldOutcome;
    public sealed record SeatUnavailable : HoldOutcome;
    public sealed record ShowOrSeatNotFound : HoldOutcome;
}

public abstract record ConfirmOutcome
{
    public sealed record Confirmed(int OrderId) : ConfirmOutcome;
    public sealed record HoldNotFound : ConfirmOutcome;
    public sealed record HoldNoLongerActive : ConfirmOutcome;
}

public abstract record ReleaseOutcome
{
    public sealed record Released : ReleaseOutcome;
    public sealed record HoldNotFound : ReleaseOutcome;
    public sealed record HoldNoLongerActive : ReleaseOutcome;
}
```

- [ ] **Step 5: Write HoldService with the locking query**

Create `api/Ticketing.Api/Domain/HoldService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Oracle.ManagedDataAccess.Client;
using Ticketing.Api.Data;

namespace Ticketing.Api.Domain;

public sealed class HoldService(TicketingDbContext db)
{
    public static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(15);

    /// <summary>ORA-00054: resource busy and acquire with NOWAIT specified.</summary>
    private const int OracleResourceBusy = 54;

    public async Task<HoldOutcome> CreateHoldAsync(
        int showId, IReadOnlyList<int> seatIds, string email, CancellationToken ct)
    {
        if (seatIds.Count == 0)
            return new HoldOutcome.ShowOrSeatNotFound();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var now = await CurrentDatabaseTimeAsync(ct);
            var rows = await LockShowSeatsAsync(showId, seatIds, ct);

            if (rows.Count != seatIds.Count)
                return new HoldOutcome.ShowOrSeatNotFound();

            if (rows.Any(row => !IsClaimable(row, now)))
                return new HoldOutcome.SeatUnavailable();

            var hold = new SeatHold
            {
                ShowId = showId,
                Email = email,
                CreatedAt = now,
                ExpiresAt = now + HoldDuration,
                Status = HoldStatus.Active,
            };
            db.SeatHolds.Add(hold);
            await db.SaveChangesAsync(ct);   // assigns hold.HoldId

            foreach (var row in rows)
            {
                row.Status = SeatStatus.Held;
                row.HoldId = hold.HoldId;
                row.ExpiresAt = hold.ExpiresAt;
            }
            await db.SaveChangesAsync(ct);

            await tx.CommitAsync(ct);
            return new HoldOutcome.Created(hold.HoldId, hold.ExpiresAt, rows.Select(r => r.ShowSeatId).ToArray());
        }
        catch (OracleException ex) when (ex.Number == OracleResourceBusy)
        {
            // Another transaction holds the lock. NOWAIT means we find out immediately
            // instead of queueing behind it, which is exactly what we want here.
            await tx.RollbackAsync(ct);
            return new HoldOutcome.SeatUnavailable();
        }
    }

    /// <summary>
    /// A seat is claimable when it is available, or when it is held by a hold that
    /// has already expired. This is the authoritative expiry check: it happens
    /// inside the lock and does not trust the background sweeper.
    /// </summary>
    private static bool IsClaimable(ShowSeat seat, DateTimeOffset now) =>
        seat.Status == SeatStatus.Available ||
        (seat.Status == SeatStatus.Held && seat.ExpiresAt is { } expiry && expiry <= now);

    /// <summary>
    /// Takes pessimistic row locks on the requested seats. This is the only raw SQL
    /// in the application: EF Core cannot express FOR UPDATE.
    /// </summary>
    private async Task<List<ShowSeat>> LockShowSeatsAsync(
        int showId, IReadOnlyList<int> seatIds, CancellationToken ct)
    {
        // Placeholders are generated from the *count* of ids, never from user input,
        // so this string interpolation cannot carry an injection.
        var placeholders = string.Join(", ", seatIds.Select((_, i) => $":seat{i}"));

        var parameters = new List<object> { new OracleParameter("showId", showId) };
        parameters.AddRange(seatIds.Select((id, i) => new OracleParameter($"seat{i}", id)));

        var sql = $"""
            SELECT * FROM show_seat
             WHERE show_id = :showId
               AND seat_id IN ({placeholders})
             ORDER BY show_seat_id
               FOR UPDATE NOWAIT
            """;

        return await db.ShowSeats.FromSqlRaw(sql, parameters.ToArray()).ToListAsync(ct);
    }

    /// <summary>
    /// Reads the clock from the database rather than the API process, so that clock
    /// skew between application servers cannot affect expiry decisions.
    /// </summary>
    private async Task<DateTimeOffset> CurrentDatabaseTimeAsync(CancellationToken ct) =>
        await db.Database.SqlQueryRaw<DateTimeOffset>(
            """SELECT SYSTIMESTAMP AS "Value" FROM dual""").SingleAsync(ct);
}
```

- [ ] **Step 6: Write the hold endpoint**

Create `api/Ticketing.Api/Endpoints/HoldEndpoints.cs`:

```csharp
using Ticketing.Api.Domain;

namespace Ticketing.Api.Endpoints;

public static class HoldEndpoints
{
    public static void MapHoldEndpoints(this WebApplication app)
    {
        app.MapPost("/api/shows/{showId:int}/holds", async (
            int showId, CreateHoldRequest request, HoldService holds, CancellationToken ct) =>
        {
            var outcome = await holds.CreateHoldAsync(showId, request.SeatIds, request.Email, ct);

            return outcome switch
            {
                HoldOutcome.Created c =>
                    Results.Created($"/api/holds/{c.HoldId}", new HoldView(c.HoldId, c.ExpiresAt, c.ShowSeatIds)),
                HoldOutcome.ShowOrSeatNotFound =>
                    Results.Problem(statusCode: 404, title: "Show or seat not found"),
                _ =>
                    Results.Problem(statusCode: 409, title: "One or more seats are no longer available"),
            };
        });
    }
}
```

- [ ] **Step 7: Register the service and endpoints**

In `api/Ticketing.Api/Program.cs`, add to the service registrations:

```csharp
builder.Services.AddScoped<HoldService>();
```

and below `app.MapShowEndpoints();`:

```csharp
app.MapHoldEndpoints();
```

Add `using Ticketing.Api.Domain;` at the top.

- [ ] **Step 8: Run the concurrency test**

```bash
cd /Users/evan/other/ticketing/api && dotnet test --filter Exactly_one_of_many_concurrent_holds
```

Expected: PASS. This is the moment the project's central claim becomes verified rather than asserted.

If `created` is greater than 1, the lock is not being taken — check that the raw SQL really contains `FOR UPDATE NOWAIT` and that `BeginTransactionAsync` wraps both the lock and the updates. If `created` is 0, every racer hit `ORA-00054`, which means the winner is not committing — check that `tx.CommitAsync` runs before the method returns.

- [ ] **Step 9: Run the whole suite to check for regressions**

```bash
cd /Users/evan/other/ticketing/api && dotnet test
```

Expected: all tests pass.

- [ ] **Step 10: Commit**

```bash
cd /Users/evan/other/ticketing
git add api/
git commit -m "feat: seat holds with pessimistic locking, proven by concurrency test"
```

---

## Task 7: Confirm a hold

**Files:**
- Modify: `api/Ticketing.Api/Domain/HoldService.cs`, `api/Ticketing.Api/Endpoints/HoldEndpoints.cs`
- Test: `api/Ticketing.Tests/HoldLifecycleTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `api/Ticketing.Tests/HoldLifecycleTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;

namespace Ticketing.Tests;

[Collection(nameof(OracleCollection))]
public class HoldLifecycleTests(OracleFixture oracle)
{
    [Fact]
    public async Task Confirming_a_hold_creates_an_order_and_marks_seats_sold()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();
        var hold = await CreateHoldAsync(client);

        var response = await client.PostAsync($"/api/holds/{hold.HoldId}/confirm", null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var db = TestData.NewContext(oracle.ConnectionString);
        var seat = await db.ShowSeats.SingleAsync(ss => ss.ShowSeatId == hold.ShowSeatIds[0]);
        Assert.Equal("SOLD", seat.Status);
        Assert.Null(seat.HoldId);
        Assert.Null(seat.ExpiresAt);
        Assert.Equal(1, await db.Tickets.CountAsync(t => t.ShowSeatId == seat.ShowSeatId));
    }

    [Fact]
    public async Task A_hold_cannot_be_confirmed_twice()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();
        var hold = await CreateHoldAsync(client);

        var first = await client.PostAsync($"/api/holds/{hold.HoldId}/confirm", null);
        var second = await client.PostAsync($"/api/holds/{hold.HoldId}/confirm", null);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, second.StatusCode);
    }

    [Fact]
    public async Task An_expired_hold_cannot_be_confirmed()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();
        var hold = await CreateHoldAsync(client);

        await TestData.ExpireHoldAsync(oracle.ConnectionString, hold.HoldId);

        var response = await client.PostAsync($"/api/holds/{hold.HoldId}/confirm", null);

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }

    [Fact]
    public async Task Seats_of_an_expired_hold_can_be_claimed_by_someone_else()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();
        var hold = await CreateHoldAsync(client);
        var (showId, seatId) = hold.Origin;

        await TestData.ExpireHoldAsync(oracle.ConnectionString, hold.HoldId);

        var response = await client.PostAsJsonAsync(
            $"/api/shows/{showId}/holds",
            new { seatIds = new[] { seatId }, email = "second@example.com" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private sealed record HoldUnderTest(int HoldId, int[] ShowSeatIds, (int ShowId, int SeatId) Origin);

    private async Task<HoldUnderTest> CreateHoldAsync(HttpClient client)
    {
        var (showId, seatId) = await TestData.PickAvailableSeatAsync(oracle.ConnectionString);

        var response = await client.PostAsJsonAsync(
            $"/api/shows/{showId}/holds",
            new { seatIds = new[] { seatId }, email = "buyer@example.com" });

        response.EnsureSuccessStatusCode();
        var view = await response.Content.ReadFromJsonAsync<HoldViewDto>();

        return new HoldUnderTest(view!.HoldId, view.ShowSeatIds, (showId, seatId));
    }

    private record HoldViewDto(int HoldId, DateTimeOffset ExpiresAt, int[] ShowSeatIds);
}
```

- [ ] **Step 2: Run to verify they fail**

```bash
cd /Users/evan/other/ticketing/api && dotnet test --filter HoldLifecycleTests
```

Expected: FAIL — 404 on the confirm route. (`Seats_of_an_expired_hold_can_be_claimed_by_someone_else` may already pass, since it exercises only Task 6 code. That is fine; it is here because it belongs to the lifecycle story.)

- [ ] **Step 3: Add ConfirmHoldAsync to HoldService**

Add to `api/Ticketing.Api/Domain/HoldService.cs`, inside the class:

```csharp
    public async Task<ConfirmOutcome> ConfirmHoldAsync(int holdId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var hold = await db.SeatHolds.FirstOrDefaultAsync(h => h.HoldId == holdId, ct);
            if (hold is null)
                return new ConfirmOutcome.HoldNotFound();

            var now = await CurrentDatabaseTimeAsync(ct);
            if (hold.Status != HoldStatus.Active || hold.ExpiresAt <= now)
                return new ConfirmOutcome.HoldNoLongerActive();

            var seats = await LockSeatsOfHoldAsync(holdId, ct);
            if (seats.Count == 0)
                return new ConfirmOutcome.HoldNoLongerActive();

            var order = new CustomerOrder
            {
                HoldId = hold.HoldId,
                Email = hold.Email,
                TotalCents = seats.Sum(s => s.PriceCents),
                CreatedAt = now,
            };
            db.Orders.Add(order);
            await db.SaveChangesAsync(ct);   // assigns order.OrderId

            foreach (var seat in seats)
            {
                db.Tickets.Add(new Ticket
                {
                    OrderId = order.OrderId,
                    ShowSeatId = seat.ShowSeatId,
                    PriceCents = seat.PriceCents,
                });

                seat.Status = SeatStatus.Sold;
                seat.HoldId = null;        // ck_held_consistency requires this
                seat.ExpiresAt = null;
            }

            hold.Status = HoldStatus.Confirmed;
            await db.SaveChangesAsync(ct);

            await tx.CommitAsync(ct);
            return new ConfirmOutcome.Confirmed(order.OrderId);
        }
        catch (OracleException ex) when (ex.Number == OracleResourceBusy)
        {
            await tx.RollbackAsync(ct);
            return new ConfirmOutcome.HoldNoLongerActive();
        }
    }

    private async Task<List<ShowSeat>> LockSeatsOfHoldAsync(int holdId, CancellationToken ct) =>
        await db.ShowSeats.FromSqlRaw(
            """
            SELECT * FROM show_seat
             WHERE hold_id = :holdId
             ORDER BY show_seat_id
               FOR UPDATE NOWAIT
            """,
            new OracleParameter("holdId", holdId)).ToListAsync(ct);
```

Note the ordering: seats are locked *before* any write, and `hold_id` is cleared when the seat becomes `SOLD` because `ck_held_consistency` forbids a non-`HELD` row from carrying a hold reference. The order's own `hold_id` preserves the link for auditing.

- [ ] **Step 4: Add the confirm endpoint**

In `api/Ticketing.Api/Endpoints/HoldEndpoints.cs`, inside `MapHoldEndpoints`:

```csharp
        app.MapPost("/api/holds/{holdId:int}/confirm", async (
            int holdId, HoldService holds, CancellationToken ct) =>
        {
            var outcome = await holds.ConfirmHoldAsync(holdId, ct);

            return outcome switch
            {
                ConfirmOutcome.Confirmed c =>
                    Results.Created($"/api/orders/{c.OrderId}", new { orderId = c.OrderId }),
                ConfirmOutcome.HoldNotFound =>
                    Results.Problem(statusCode: 404, title: "Hold not found"),
                _ =>
                    Results.Problem(statusCode: 410, title: "This hold has expired or was already used"),
            };
        });
```

- [ ] **Step 5: Run the tests**

```bash
cd /Users/evan/other/ticketing/api && dotnet test --filter HoldLifecycleTests
```

Expected: PASS, 4 tests.

- [ ] **Step 6: Commit**

```bash
cd /Users/evan/other/ticketing
git add api/
git commit -m "feat: confirm a hold into an order with tickets"
```

---

## Task 8: Release a hold

**Files:**
- Modify: `api/Ticketing.Api/Domain/HoldService.cs`, `api/Ticketing.Api/Endpoints/HoldEndpoints.cs`
- Test: `api/Ticketing.Tests/HoldReleaseTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `api/Ticketing.Tests/HoldReleaseTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;

namespace Ticketing.Tests;

[Collection(nameof(OracleCollection))]
public class HoldReleaseTests(OracleFixture oracle)
{
    [Fact]
    public async Task Releasing_a_hold_makes_its_seats_available_again()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();
        var (showId, seatId) = await TestData.PickAvailableSeatAsync(oracle.ConnectionString);

        var created = await client.PostAsJsonAsync(
            $"/api/shows/{showId}/holds", new { seatIds = new[] { seatId }, email = "buyer@example.com" });
        var hold = await created.Content.ReadFromJsonAsync<HoldViewDto>();

        var response = await client.DeleteAsync($"/api/holds/{hold!.HoldId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var db = TestData.NewContext(oracle.ConnectionString);
        var seat = await db.ShowSeats.SingleAsync(ss => ss.ShowSeatId == hold.ShowSeatIds[0]);
        Assert.Equal("AVAILABLE", seat.Status);
        Assert.Null(seat.HoldId);
        Assert.Null(seat.ExpiresAt);
    }

    [Fact]
    public async Task Releasing_a_confirmed_hold_returns_410()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();
        var (showId, seatId) = await TestData.PickAvailableSeatAsync(oracle.ConnectionString);

        var created = await client.PostAsJsonAsync(
            $"/api/shows/{showId}/holds", new { seatIds = new[] { seatId }, email = "buyer@example.com" });
        var hold = await created.Content.ReadFromJsonAsync<HoldViewDto>();
        await client.PostAsync($"/api/holds/{hold!.HoldId}/confirm", null);

        var response = await client.DeleteAsync($"/api/holds/{hold.HoldId}");

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }

    private record HoldViewDto(int HoldId, DateTimeOffset ExpiresAt, int[] ShowSeatIds);
}
```

- [ ] **Step 2: Run to verify they fail**

```bash
cd /Users/evan/other/ticketing/api && dotnet test --filter HoldReleaseTests
```

Expected: FAIL — 404, the DELETE route does not exist.

- [ ] **Step 3: Add ReleaseHoldAsync to HoldService**

Add inside the `HoldService` class:

```csharp
    public async Task<ReleaseOutcome> ReleaseHoldAsync(int holdId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var hold = await db.SeatHolds.FirstOrDefaultAsync(h => h.HoldId == holdId, ct);
            if (hold is null)
                return new ReleaseOutcome.HoldNotFound();

            if (hold.Status != HoldStatus.Active)
                return new ReleaseOutcome.HoldNoLongerActive();

            var seats = await LockSeatsOfHoldAsync(holdId, ct);
            foreach (var seat in seats)
            {
                seat.Status = SeatStatus.Available;
                seat.HoldId = null;
                seat.ExpiresAt = null;
            }

            hold.Status = HoldStatus.Released;
            await db.SaveChangesAsync(ct);

            await tx.CommitAsync(ct);
            return new ReleaseOutcome.Released();
        }
        catch (OracleException ex) when (ex.Number == OracleResourceBusy)
        {
            await tx.RollbackAsync(ct);
            return new ReleaseOutcome.HoldNoLongerActive();
        }
    }
```

- [ ] **Step 4: Add the release endpoint**

In `MapHoldEndpoints`:

```csharp
        app.MapDelete("/api/holds/{holdId:int}", async (
            int holdId, HoldService holds, CancellationToken ct) =>
        {
            var outcome = await holds.ReleaseHoldAsync(holdId, ct);

            return outcome switch
            {
                ReleaseOutcome.Released => Results.NoContent(),
                ReleaseOutcome.HoldNotFound => Results.Problem(statusCode: 404, title: "Hold not found"),
                _ => Results.Problem(statusCode: 410, title: "This hold is no longer active"),
            };
        });
```

- [ ] **Step 5: Run the tests**

```bash
cd /Users/evan/other/ticketing/api && dotnet test --filter HoldReleaseTests
```

Expected: PASS, 2 tests.

- [ ] **Step 6: Commit**

```bash
cd /Users/evan/other/ticketing
git add api/
git commit -m "feat: release a hold early"
```

---

## Task 9: The expired-hold sweeper

**Files:**
- Create: `api/Ticketing.Api/Domain/ExpiredHoldSweeper.cs`
- Modify: `api/Ticketing.Api/Program.cs`
- Test: `api/Ticketing.Tests/SweeperTests.cs`

- [ ] **Step 1: Write the failing test**

The sweep logic is tested directly rather than through the hosted service, so the test does not have to wait on a timer.

Create `api/Ticketing.Tests/SweeperTests.cs`:

```csharp
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Domain;

namespace Ticketing.Tests;

[Collection(nameof(OracleCollection))]
public class SweeperTests(OracleFixture oracle)
{
    [Fact]
    public async Task Sweep_releases_expired_holds_and_leaves_active_ones_alone()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();

        var expired = await CreateHoldAsync(client);
        var active = await CreateHoldAsync(client);
        await TestData.ExpireHoldAsync(oracle.ConnectionString, expired.HoldId);

        await using var db = TestData.NewContext(oracle.ConnectionString);
        var swept = await ExpiredHoldSweeper.SweepAsync(db, CancellationToken.None);

        Assert.True(swept >= 1);

        var expiredSeat = await db.ShowSeats.AsNoTracking()
            .SingleAsync(ss => ss.ShowSeatId == expired.ShowSeatIds[0]);
        Assert.Equal("AVAILABLE", expiredSeat.Status);
        Assert.Null(expiredSeat.HoldId);

        var activeSeat = await db.ShowSeats.AsNoTracking()
            .SingleAsync(ss => ss.ShowSeatId == active.ShowSeatIds[0]);
        Assert.Equal("HELD", activeSeat.Status);
        Assert.NotNull(activeSeat.HoldId);

        var expiredHold = await db.SeatHolds.AsNoTracking().SingleAsync(h => h.HoldId == expired.HoldId);
        Assert.Equal("EXPIRED", expiredHold.Status);
    }

    private async Task<HoldViewDto> CreateHoldAsync(HttpClient client)
    {
        var (showId, seatId) = await TestData.PickAvailableSeatAsync(oracle.ConnectionString);
        var response = await client.PostAsJsonAsync(
            $"/api/shows/{showId}/holds", new { seatIds = new[] { seatId }, email = "buyer@example.com" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<HoldViewDto>())!;
    }

    public record HoldViewDto(int HoldId, DateTimeOffset ExpiresAt, int[] ShowSeatIds);
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
cd /Users/evan/other/ticketing/api && dotnet test --filter SweeperTests
```

Expected: FAIL — compile error, `ExpiredHoldSweeper` does not exist.

- [ ] **Step 3: Write the sweeper**

Create `api/Ticketing.Api/Domain/ExpiredHoldSweeper.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Data;

namespace Ticketing.Api.Domain;

/// <summary>
/// Cosmetic cleanup only. Booking correctness does not depend on this running —
/// HoldService re-checks expiry inside its own transaction. This exists so the
/// seat map and any reporting do not show stale HELD rows for up to a minute.
/// </summary>
public sealed class ExpiredHoldSweeper(IServiceProvider services, ILogger<ExpiredHoldSweeper> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    public static async Task<int> SweepAsync(TicketingDbContext db, CancellationToken ct)
    {
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>(
            """SELECT SYSTIMESTAMP AS "Value" FROM dual""").SingleAsync(ct);

        var staleSeats = await db.ShowSeats
            .Where(ss => ss.Status == SeatStatus.Held && ss.ExpiresAt != null && ss.ExpiresAt <= now)
            .ToListAsync(ct);

        if (staleSeats.Count == 0)
            return 0;

        var holdIds = staleSeats.Where(s => s.HoldId != null).Select(s => s.HoldId!.Value).Distinct().ToList();

        foreach (var seat in staleSeats)
        {
            seat.Status = SeatStatus.Available;
            seat.HoldId = null;
            seat.ExpiresAt = null;
        }

        var holds = await db.SeatHolds
            .Where(h => holdIds.Contains(h.HoldId) && h.Status == HoldStatus.Active)
            .ToListAsync(ct);

        foreach (var hold in holds)
            hold.Status = HoldStatus.Expired;

        await db.SaveChangesAsync(ct);
        return staleSeats.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
                var swept = await SweepAsync(db, stoppingToken);

                if (swept > 0)
                    logger.LogInformation("Swept {Count} expired seat holds", swept);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed sweep is not fatal: correctness lives in HoldService.
                logger.LogError(ex, "Expired-hold sweep failed; will retry next interval");
            }
        }
    }
}
```

- [ ] **Step 4: Register the sweeper**

In `api/Ticketing.Api/Program.cs`, with the other service registrations:

```csharp
builder.Services.AddHostedService<ExpiredHoldSweeper>();
```

- [ ] **Step 5: Run the test**

```bash
cd /Users/evan/other/ticketing/api && dotnet test --filter SweeperTests
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
cd /Users/evan/other/ticketing
git add api/
git commit -m "feat: background sweeper for expired holds"
```

---

## Task 10: Order lookup and a database-backed health check

**Files:**
- Modify: `api/Ticketing.Api/Endpoints/ShowEndpoints.cs`, `Endpoints/HealthEndpoints.cs`, `Program.cs`
- Create: `api/Ticketing.Api/Endpoints/OrderEndpoints.cs`
- Test: `api/Ticketing.Tests/OrderTests.cs`

- [ ] **Step 1: Write the failing test**

Create `api/Ticketing.Tests/OrderTests.cs`:

```csharp
using System.Net.Http.Json;

namespace Ticketing.Tests;

[Collection(nameof(OracleCollection))]
public class OrderTests(OracleFixture oracle)
{
    [Fact]
    public async Task Order_lookup_returns_tickets_with_seat_detail()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();

        var (showId, seatId) = await TestData.PickAvailableSeatAsync(oracle.ConnectionString);
        var created = await client.PostAsJsonAsync(
            $"/api/shows/{showId}/holds", new { seatIds = new[] { seatId }, email = "buyer@example.com" });
        var hold = await created.Content.ReadFromJsonAsync<HoldViewDto>();

        var confirmed = await client.PostAsync($"/api/holds/{hold!.HoldId}/confirm", null);
        var order = await confirmed.Content.ReadFromJsonAsync<OrderIdDto>();

        var view = await client.GetFromJsonAsync<OrderViewDto>($"/api/orders/{order!.OrderId}");

        Assert.NotNull(view);
        Assert.Equal("buyer@example.com", view!.Email);
        Assert.Single(view.Tickets);
        Assert.True(view.TotalCents > 0);
        Assert.Equal(view.TotalCents, view.Tickets.Sum(t => t.PriceCents));
    }

    [Fact]
    public async Task Health_reports_database_connectivity()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();

        var health = await client.GetFromJsonAsync<HealthDto>("/health");

        Assert.Equal("ok", health!.Status);
        Assert.True(health.Database);
    }

    private record HoldViewDto(int HoldId, DateTimeOffset ExpiresAt, int[] ShowSeatIds);
    private record OrderIdDto(int OrderId);
    private record OrderViewDto(int OrderId, string Email, int TotalCents, DateTimeOffset CreatedAt, TicketDto[] Tickets);
    private record TicketDto(int TicketId, int ShowSeatId, string Section, string RowLabel, int SeatNumber, int PriceCents);
    private record HealthDto(string Status, bool Database);
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
cd /Users/evan/other/ticketing/api && dotnet test --filter OrderTests
```

Expected: FAIL — 404 on `/api/orders/{id}`, and `Database` missing from the health payload.

- [ ] **Step 3: Write the order endpoint**

Create `api/Ticketing.Api/Endpoints/OrderEndpoints.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Data;

namespace Ticketing.Api.Endpoints;

public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this WebApplication app)
    {
        app.MapGet("/api/orders/{orderId:int}", async (
            int orderId, TicketingDbContext db, CancellationToken ct) =>
        {
            var order = await db.Orders.FirstOrDefaultAsync(o => o.OrderId == orderId, ct);
            if (order is null)
                return Results.Problem(statusCode: 404, title: "Order not found");

            var tickets = await (
                from ticket in db.Tickets
                join showSeat in db.ShowSeats on ticket.ShowSeatId equals showSeat.ShowSeatId
                join seat in db.Seats on showSeat.SeatId equals seat.SeatId
                where ticket.OrderId == orderId
                orderby seat.Section, seat.RowLabel, seat.SeatNumber
                select new TicketView(
                    ticket.TicketId, ticket.ShowSeatId,
                    seat.Section, seat.RowLabel, seat.SeatNumber, ticket.PriceCents))
                .ToArrayAsync(ct);

            return Results.Ok(new OrderView(order.OrderId, order.Email, order.TotalCents, order.CreatedAt, tickets));
        });
    }
}
```

- [ ] **Step 4: Make health check the database**

Replace `api/Ticketing.Api/Endpoints/HealthEndpoints.cs`:

```csharp
using Ticketing.Api.Data;

namespace Ticketing.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health", async (TicketingDbContext db, CancellationToken ct) =>
        {
            var reachable = await db.Database.CanConnectAsync(ct);

            return reachable
                ? Results.Ok(new { status = "ok", database = true })
                : Results.Json(new { status = "degraded", database = false }, statusCode: 503);
        });
    }
}
```

The deployment's keep-alive job depends on this endpoint touching the database — a health check that only proves the web server is up would let the database go idle and be reclaimed.

- [ ] **Step 5: Delete the superseded health test**

`HealthTests.cs` from Task 3 uses a bare `WebApplicationFactory` with no connection
string. Now that `/health` probes the database, that test would get a 503. Its
replacement, `Health_reports_database_connectivity`, is in `OrderTests.cs` above.

```bash
rm /Users/evan/other/ticketing/api/Ticketing.Tests/HealthTests.cs
```

- [ ] **Step 6: Register the order endpoints**

In `Program.cs`, below `app.MapHoldEndpoints();`:

```csharp
app.MapOrderEndpoints();
```

- [ ] **Step 7: Run the tests**

```bash
cd /Users/evan/other/ticketing/api && dotnet test --filter OrderTests
```

Expected: PASS, 2 tests.

- [ ] **Step 8: Commit**

```bash
cd /Users/evan/other/ticketing
git add api/
git commit -m "feat: order lookup and database-backed health check"
```

---

## Task 11: Problem Details, rate limiting, and CORS for local Angular

**Files:**
- Modify: `api/Ticketing.Api/Program.cs`
- Test: `api/Ticketing.Tests/ErrorContractTests.cs`

- [ ] **Step 1: Write the failing test**

Create `api/Ticketing.Tests/ErrorContractTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;

namespace Ticketing.Tests;

[Collection(nameof(OracleCollection))]
public class ErrorContractTests(OracleFixture oracle)
{
    [Fact]
    public async Task Conflicts_are_returned_as_problem_details()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();

        var (showId, seatId) = await TestData.PickAvailableSeatAsync(oracle.ConnectionString);
        await client.PostAsJsonAsync(
            $"/api/shows/{showId}/holds", new { seatIds = new[] { seatId }, email = "first@example.com" });

        var second = await client.PostAsJsonAsync(
            $"/api/shows/{showId}/holds", new { seatIds = new[] { seatId }, email = "second@example.com" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);

        var problem = await second.Content.ReadFromJsonAsync<ProblemDto>();
        Assert.Equal(409, problem!.Status);
        Assert.False(string.IsNullOrWhiteSpace(problem.Title));
    }

    private record ProblemDto(string? Title, int Status, string? Detail);
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
cd /Users/evan/other/ticketing/api && dotnet test --filter ErrorContractTests
```

Expected: FAIL on the content-type assertion, because `AddProblemDetails` has not been called.

- [ ] **Step 3: Finalise Program.cs**

Replace `api/Ticketing.Api/Program.cs` with the complete composition root:

```csharp
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<TicketingDbContext>(options =>
    options.UseOracle(builder.Configuration.GetConnectionString("Ticketing")));

builder.Services.AddScoped<HoldService>();
builder.Services.AddHostedService<ExpiredHoldSweeper>();
builder.Services.AddProblemDetails();

// The public demo has no authentication, so writes are rate limited per client IP.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("writes", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
            }));
});

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy => policy
        .WithOrigins("http://localhost:4200")
        .AllowAnyHeader()
        .AllowAnyMethod()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseRateLimiter();

app.MapHealthEndpoints();
app.MapShowEndpoints();
app.MapHoldEndpoints();
app.MapOrderEndpoints();

app.Run();

public partial class Program;
```

CORS allows `localhost:4200` for Angular's dev server. In production the Angular build is served by this same application, so no CORS configuration applies there.

- [ ] **Step 4: Apply the rate limit policy to the write endpoints**

In `api/Ticketing.Api/Endpoints/HoldEndpoints.cs`, chain `.RequireRateLimiting("writes")` onto each of the three mappings. For example:

```csharp
        app.MapPost("/api/shows/{showId:int}/holds", async (
            int showId, CreateHoldRequest request, HoldService holds, CancellationToken ct) =>
        {
            var outcome = await holds.CreateHoldAsync(showId, request.SeatIds, request.Email, ct);

            return outcome switch
            {
                HoldOutcome.Created c =>
                    Results.Created($"/api/holds/{c.HoldId}", new HoldView(c.HoldId, c.ExpiresAt, c.ShowSeatIds)),
                HoldOutcome.ShowOrSeatNotFound =>
                    Results.Problem(statusCode: 404, title: "Show or seat not found"),
                _ =>
                    Results.Problem(statusCode: 409, title: "One or more seats are no longer available"),
            };
        }).RequireRateLimiting("writes");
```

Do the same for the confirm (`MapPost`) and release (`MapDelete`) mappings.

**Note:** the concurrency test in Task 6 fires 10 requests in one burst from one address. A limit of 30/minute leaves headroom, but if you lower it, that test will start failing with 429 — the limit and the test are coupled.

- [ ] **Step 5: Run the full suite**

```bash
cd /Users/evan/other/ticketing/api && dotnet test
```

Expected: all tests pass, including the concurrency test.

- [ ] **Step 6: Commit**

```bash
cd /Users/evan/other/ticketing
git add api/
git commit -m "feat: problem details, rate limiting, and dev CORS"
```

---

## Task 12: Verify the API end to end by hand

Tests pass, but nobody has seen the thing work. This task produces the evidence.

- [ ] **Step 1: Ensure Oracle is running with schema applied**

```bash
cd /Users/evan/other/ticketing
docker compose up -d
docker compose ps
```

Expected: `(healthy)`. If the volume was destroyed since Task 2, re-apply both migration files as in Task 2 Step 6.

- [ ] **Step 2: Run the API**

```bash
cd /Users/evan/other/ticketing/api/Ticketing.Api
dotnet run
```

Expected: `Now listening on: http://localhost:5xxx`. Note the port; it is used below as `PORT`.

- [ ] **Step 3: Walk the buyer flow with curl**

In a second terminal, substituting the real port:

```bash
PORT=5xxx
curl -s "http://localhost:$PORT/health"
curl -s "http://localhost:$PORT/api/shows"
curl -s "http://localhost:$PORT/api/shows/1/seats" | head -c 400
curl -s -X POST "http://localhost:$PORT/api/shows/1/holds" \
  -H 'Content-Type: application/json' \
  -d '{"seatIds":[1,2],"email":"evan@example.com"}'
```

Expected: health reports `"database":true`; three shows; 100 seats; the hold returns `201` with a `holdId` and an `expiresAt` roughly 15 minutes ahead.

- [ ] **Step 4: Prove the conflict path by hand**

```bash
curl -s -o /dev/null -w '%{http_code}\n' -X POST "http://localhost:$PORT/api/shows/1/holds" \
  -H 'Content-Type: application/json' \
  -d '{"seatIds":[1],"email":"someone-else@example.com"}'
```

Expected: `409`. Seat 1 is already held by the previous step.

- [ ] **Step 5: Confirm and read back the order**

```bash
HOLD_ID=<the holdId from step 3>
curl -s -X POST "http://localhost:$PORT/api/holds/$HOLD_ID/confirm"
curl -s "http://localhost:$PORT/api/orders/1"
```

Expected: the confirm returns `201` with an `orderId`; the order shows two tickets and a `totalCents` equal to the sum of the ticket prices.

- [ ] **Step 6: Commit any fixes and tag the milestone**

```bash
cd /Users/evan/other/ticketing
git add -A
git commit -m "chore: verified buyer flow end to end against local Oracle" --allow-empty
git tag api-core-complete
```

---

## Done when

- [ ] `dotnet test` passes with every test green, including `Exactly_one_of_many_concurrent_holds_on_the_same_seat_wins`
- [ ] `curl` can complete browse → hold → confirm → read order against a locally running Oracle
- [ ] A second hold attempt on a held seat returns `409` with a `application/problem+json` body
- [ ] `/health` reports `"database": true`

Plan 2 (Angular buyer flow) consumes exactly the endpoints listed in Task 5, 6, 7, 8 and 10.
