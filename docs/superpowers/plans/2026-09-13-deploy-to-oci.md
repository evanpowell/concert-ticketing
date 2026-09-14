# Deployment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The ticketing API running on a public HTTPS URL, on Oracle Cloud Always Free, that stays working months from now without attention.

**Architecture:** One Ampere A1 VM (Arm, same architecture as the dev Mac) running three containers behind Caddy: the ASP.NET API, Oracle Database 23ai Free, and Caddy itself terminating TLS with automatic Let's Encrypt certificates. Infrastructure is Terraform. Deployment is GitHub Actions pushing an Arm image to GHCR and restarting the stack over SSH.

**Tech Stack:** Terraform, OCI Always Free (VM.Standard.A1.Flex), Docker Compose, Caddy 2, GitHub Actions, GHCR.

**Spec:** `docs/superpowers/specs/2026-09-12-concert-ticketing-design.md` §10

**Depends on:** `2026-09-13-database-api-core.md` (complete, tagged `api-core-complete`)

---

## Two things that make this different from a normal deploy

**1. The demo is publicly writable with no authentication.** Without a reset job,
the first bored visitor buys all 100 seats and the portfolio link permanently
displays a sold-out venue. The hourly reset is a functional requirement, not
housekeeping.

**2. Free-tier resources get reclaimed when idle.** A link on a resume is clicked
months after it is written. OCI stops idle Always Free compute, and an Always Free
Autonomous Database is deleted after 90 cumulative days stopped. The keep-alive is
what makes the link survive to be clicked.

Both are built in from the first deploy rather than added after an outage.

## Task ordering

Tasks 1–4 need no OCI account and are verifiable on the dev machine. Tasks 5
onward need credentials. If Ampere capacity blocks Task 6, Tasks 1–4 still stand
and the fallback in Task 6 Step 6 applies.

## File structure

```
Dockerfile                     multi-stage build for the API
.dockerignore
compose.prod.yaml              caddy + api + oracle
Caddyfile                      TLS + reverse proxy
.env.example                   committed; .env is not
infra/
  main.tf                      VCN, subnet, security list, A1 instance
  variables.tf
  outputs.tf
  cloud-init.yaml              installs Docker on first boot
  terraform.tfvars.example     committed; terraform.tfvars is not
.github/workflows/
  ci.yml                       test, build, push, deploy
  keepalive.yml                daily ping so nothing is reclaimed
api/Ticketing.Api/Data/
  MigrationRunner.cs           moved from the test project
api/Ticketing.Api/Domain/
  DemoResetService.cs          hourly reset of transactional data
```

---

## Task 1: Move the migration runner into the API

Deployment needs the schema applied to a fresh database with no human running
sqlplus. The runner already exists in the test project; moving it into the API
makes one implementation serve both, which is also why the tests keep passing
unchanged as proof the move was behaviour-preserving.

**Files:**
- Create: `api/Ticketing.Api/Data/MigrationRunner.cs`
- Delete: `api/Ticketing.Tests/MigrationRunner.cs`
- Modify: `api/Ticketing.Api/Program.cs`, `api/Ticketing.Tests/OracleFixture.cs`

- [ ] **Step 1: Move the file and change its namespace**

Create `api/Ticketing.Api/Data/MigrationRunner.cs` with the same body as the test
project's copy, but namespaced to the API and with the migrations directory
resolved from configuration rather than by walking up from the test binary:

```csharp
using Oracle.ManagedDataAccess.Client;

namespace Ticketing.Api.Data;

/// <summary>
/// Applies db/migrations/*.sql in filename order. Splits on ';', which is why the
/// migration files must not contain PL/SQL blocks. Idempotent only in the sense
/// that it is safe to run against an empty schema; re-running against a populated
/// one raises ORA-00955, which the caller treats as "already applied".
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
```

- [ ] **Step 2: Point the test fixture at the API's copy**

In `api/Ticketing.Tests/OracleFixture.cs`, replace the `MigrationRunner` usage:

```csharp
        await Ticketing.Api.Data.MigrationRunner.ApplyAsync(
            ConnectionString, FindMigrationsDirectory());
```

and move the directory-walking helper into the fixture itself, since it is a
test-only concern:

```csharp
    private static string FindMigrationsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "db", "migrations")))
            dir = dir.Parent;

        return dir is null
            ? throw new DirectoryNotFoundException("Could not locate db/migrations above the test binary.")
            : Path.Combine(dir.FullName, "db", "migrations");
    }
```

- [ ] **Step 3: Delete the test project's copy**

```bash
rm /Users/evan/other/ticketing/api/Ticketing.Tests/MigrationRunner.cs
```

- [ ] **Step 4: Apply migrations at startup when configured**

In `api/Ticketing.Api/Program.cs`, after `var app = builder.Build();`:

```csharp
// In containers the database starts empty, so the app applies its own schema.
// Off by default: local development applies migrations explicitly, and a
// production database should never be migrated by accident.
if (app.Configuration.GetValue<bool>("Ticketing:ApplyMigrationsOnStartup"))
{
    var connectionString = app.Configuration.GetConnectionString("Ticketing")!;
    var migrationsDirectory = app.Configuration["Ticketing:MigrationsDirectory"] ?? "/app/migrations";
    await MigrationRunner.ApplyAsync(connectionString, migrationsDirectory, app.Logger);
}
```

- [ ] **Step 5: Run the full suite — it must pass unchanged**

```bash
cd /Users/evan/other/ticketing/api && dotnet test
```

Expected: PASS, 23 tests. The tests are the evidence that moving the runner
changed no behaviour. If they fail, the move was not behaviour-preserving — fix
that before continuing rather than adjusting the tests.

- [ ] **Step 6: Commit**

```bash
cd /Users/evan/other/ticketing
git add -A
git commit -m "refactor: move migration runner into the API for container startup"
```

---

## Task 2: Containerize the API

**Files:**
- Create: `Dockerfile`, `.dockerignore`

- [ ] **Step 1: Write the .dockerignore**

Create `.dockerignore`:

```
**/bin/
**/obj/
.git/
docs/
node_modules/
.env
```

Without this, `bin/` and `obj/` from the host are copied into the build context and
can shadow the container's own build output in confusing ways.

- [ ] **Step 2: Write the Dockerfile**

Create `Dockerfile`:

```dockerfile
# Build stage. Restore before copying source so dependency layers cache.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY api/Ticketing.Api/Ticketing.Api.csproj ./Ticketing.Api/
RUN dotnet restore ./Ticketing.Api/Ticketing.Api.csproj

COPY api/Ticketing.Api/ ./Ticketing.Api/
RUN dotnet publish ./Ticketing.Api/Ticketing.Api.csproj -c Release -o /app --no-restore

# Runtime stage.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app ./
# The schema travels with the image so a fresh database can be migrated on boot.
COPY db/migrations/ ./migrations/

# The aspnet image defines APP_UID for a non-root user; run as it.
USER $APP_UID

EXPOSE 8080
ENTRYPOINT ["dotnet", "Ticketing.Api.dll"]
```

- [ ] **Step 3: Build the image**

```bash
cd /Users/evan/other/ticketing
docker build -t ticketing-api:local .
```

Expected: `naming to docker.io/library/ticketing-api:local done`. First build pulls
the .NET SDK image and takes a few minutes.

- [ ] **Step 4: Verify the image runs against the existing local Oracle**

The dev Oracle from `compose.yaml` is on the host, so the container reaches it via
`host.docker.internal`:

```bash
docker run --rm -d --name ticketing-api-test -p 5100:8080 \
  -e "ConnectionStrings__Ticketing=User Id=ticketing;Password=ticketing;Data Source=host.docker.internal:1521/FREEPDB1" \
  ticketing-api:local
```

- [ ] **Step 5: Confirm it serves**

```bash
curl -s http://localhost:5100/health
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:5100/
```

Expected: `{"status":"ok","database":true}` and `200`.

- [ ] **Step 6: Stop the test container**

```bash
docker stop ticketing-api-test
```

- [ ] **Step 7: Commit**

```bash
git add Dockerfile .dockerignore
git commit -m "feat: containerize the API"
```

---

## Task 3: The production compose stack

**Files:**
- Create: `compose.prod.yaml`, `Caddyfile`, `.env.example`

- [ ] **Step 1: Write the Caddyfile**

Create `Caddyfile`:

```
{$DOMAIN} {
	encode gzip
	reverse_proxy api:8080
}
```

Caddy obtains and renews Let's Encrypt certificates automatically for `$DOMAIN`,
with no certbot and no cron. That is the entire TLS configuration.

- [ ] **Step 2: Write the production compose file**

Create `compose.prod.yaml`:

```yaml
services:
  caddy:
    image: caddy:2-alpine
    restart: unless-stopped
    ports:
      - "80:80"
      - "443:443"
    environment:
      DOMAIN: ${DOMAIN}
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - caddy-data:/data
      - caddy-config:/config
    depends_on:
      - api

  api:
    image: ${API_IMAGE:-ghcr.io/evanpowell/ticketing-api:latest}
    restart: unless-stopped
    environment:
      ConnectionStrings__Ticketing: "User Id=ticketing;Password=${ORACLE_APP_PASSWORD};Data Source=oracle:1521/FREEPDB1"
      Ticketing__ApplyMigrationsOnStartup: "true"
      Ticketing__DemoResetEnabled: "true"
      ASPNETCORE_ENVIRONMENT: Production
    depends_on:
      oracle:
        condition: service_healthy

  oracle:
    image: gvenzl/oracle-free:23-slim-faststart
    restart: unless-stopped
    environment:
      ORACLE_PASSWORD: ${ORACLE_SYS_PASSWORD}
      APP_USER: ticketing
      APP_USER_PASSWORD: ${ORACLE_APP_PASSWORD}
    volumes:
      - oracle-data:/opt/oracle/oradata
    healthcheck:
      test: ["CMD", "healthcheck.sh"]
      interval: 10s
      timeout: 5s
      retries: 30
      start_period: 30s

volumes:
  caddy-data:
  caddy-config:
  oracle-data:
```

Oracle publishes no ports: it is reachable only from the `api` container on the
compose network, never from the internet.

- [ ] **Step 3: Write the env template**

Create `.env.example`:

```bash
# Copy to .env on the server and fill in. .env is gitignored.
DOMAIN=ticketing.example.com
ORACLE_SYS_PASSWORD=change-me-a-long-random-string
ORACLE_APP_PASSWORD=change-me-another-long-random-string
API_IMAGE=ghcr.io/evanpowell/ticketing-api:latest
```

- [ ] **Step 4: Confirm .env is ignored**

```bash
cd /Users/evan/other/ticketing
grep -q '^\.env$' .gitignore && echo ".env is ignored" || echo "PROBLEM: add .env to .gitignore"
```

Expected: `.env is ignored`.

- [ ] **Step 5: Validate the compose file parses**

```bash
DOMAIN=localhost ORACLE_SYS_PASSWORD=x ORACLE_APP_PASSWORD=y \
  docker compose -f compose.prod.yaml config > /dev/null && echo "compose.prod.yaml is valid"
```

Expected: `compose.prod.yaml is valid`.

- [ ] **Step 6: Commit**

```bash
git add compose.prod.yaml Caddyfile .env.example
git commit -m "feat: production compose stack with Caddy TLS"
```

---

## Task 4: The demo reset job

Without this the public demo destroys itself. It belongs before deployment, not after.

**Files:**
- Create: `api/Ticketing.Api/Domain/DemoResetService.cs`
- Modify: `api/Ticketing.Api/Program.cs`
- Test: `api/Ticketing.Tests/DemoResetTests.cs`

- [ ] **Step 1: Write the failing test**

Create `api/Ticketing.Tests/DemoResetTests.cs`:

```csharp
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Domain;

namespace Ticketing.Tests;

[Collection(nameof(OracleCollection))]
public class DemoResetTests(OracleFixture oracle)
{
    [Fact]
    public async Task Reset_clears_transactional_data_but_keeps_reference_data()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();

        var (showId, seatId) = await TestData.PickAvailableSeatAsync(oracle.ConnectionString);
        var created = await client.PostAsJsonAsync(
            $"/api/shows/{showId}/holds", new { seatIds = new[] { seatId }, email = "buyer@example.com" });
        var hold = await created.Content.ReadFromJsonAsync<HoldViewDto>();
        await client.PostAsync($"/api/holds/{hold!.HoldId}/confirm", null);

        await using var db = TestData.NewContext(oracle.ConnectionString);
        await DemoResetService.ResetAsync(db, CancellationToken.None);

        // Transactional data is gone.
        Assert.Equal(0, await db.Tickets.CountAsync());
        Assert.Equal(0, await db.Orders.CountAsync());
        Assert.Equal(0, await db.SeatHolds.CountAsync());

        // Every seat is available again.
        Assert.Equal(0, await db.ShowSeats.CountAsync(ss => ss.Status != "AVAILABLE"));
        Assert.Equal(0, await db.ShowSeats.CountAsync(ss => ss.HoldId != null));

        // Reference data is untouched.
        Assert.Equal(3, await db.Shows.CountAsync());
        Assert.Equal(100, await db.Seats.CountAsync());
        Assert.Equal(300, await db.ShowSeats.CountAsync());
    }

    private record HoldViewDto(int HoldId, DateTimeOffset ExpiresAt, int[] ShowSeatIds);
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
cd /Users/evan/other/ticketing/api && dotnet test --filter DemoResetTests
```

Expected: FAIL — compile error, `DemoResetService` does not exist.

- [ ] **Step 3: Write the service**

Create `api/Ticketing.Api/Domain/DemoResetService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Data;

namespace Ticketing.Api.Domain;

/// <summary>
/// The public demo has no authentication, so anyone can buy every seat. Without a
/// periodic reset the deployed link would permanently show a sold-out venue and
/// demonstrate nothing. Deletes transactional data only; venues, seats and shows
/// are reference data and are never touched.
/// </summary>
public sealed class DemoResetService(IServiceProvider services, ILogger<DemoResetService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    public static async Task ResetAsync(TicketingDbContext db, CancellationToken ct)
    {
        // Order matters: TICKET references CUSTOMER_ORDER and SHOW_SEAT;
        // CUSTOMER_ORDER references SEAT_HOLD; SHOW_SEAT references SEAT_HOLD.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM ticket", ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM customer_order", ct);
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE show_seat SET status = 'AVAILABLE', hold_id = NULL, expires_at = NULL", ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM seat_hold", ct);
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
                await ResetAsync(db, stoppingToken);
                logger.LogInformation("Demo data reset");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Demo reset failed; will retry next interval");
            }
        }
    }
}
```

`show_seat` must be cleared of `hold_id` *before* `seat_hold` rows are deleted, or
the foreign key blocks the delete.

- [ ] **Step 4: Register it, gated by configuration**

In `api/Ticketing.Api/Program.cs`, alongside the other hosted services:

```csharp
// Only the public demo resets itself. Never enable this anywhere real.
if (builder.Configuration.GetValue<bool>("Ticketing:DemoResetEnabled"))
    builder.Services.AddHostedService<DemoResetService>();
```

- [ ] **Step 5: Run the test**

```bash
cd /Users/evan/other/ticketing/api && dotnet test --filter DemoResetTests
```

Expected: PASS.

- [ ] **Step 6: Run the full suite**

```bash
cd /Users/evan/other/ticketing/api && dotnet test
```

Expected: PASS, 24 tests.

- [ ] **Step 7: Commit**

```bash
cd /Users/evan/other/ticketing
git add -A
git commit -m "feat: hourly demo reset so the public demo does not destroy itself"
```

---

## Task 5: OCI credentials

Everything from here needs an OCI account. Signup requires a credit card (Always
Free is not charged) and the **home region is permanent** — Always Free resources
exist only there, so choose before clicking.

- [ ] **Step 1: Install the OCI CLI**

```bash
brew install oci-cli
oci --version
```

- [ ] **Step 2: Generate an API signing key**

In the OCI console: Profile → My profile → API keys → Add API key → Generate API
key pair → download the private key, then copy the configuration snippet shown.

```bash
mkdir -p ~/.oci
mv ~/Downloads/*.pem ~/.oci/oci_api_key.pem
chmod 600 ~/.oci/oci_api_key.pem
```

- [ ] **Step 3: Write ~/.oci/config**

Paste the snippet the console gave you, with `key_file` pointing at the saved key:

```ini
[DEFAULT]
user=ocid1.user.oc1..aaaa...
fingerprint=aa:bb:cc:...
tenancy=ocid1.tenancy.oc1..aaaa...
region=us-sanjose-1
key_file=~/.oci/oci_api_key.pem
```

```bash
chmod 600 ~/.oci/config
```

- [ ] **Step 4: Verify authentication works**

```bash
oci iam region list --output table | head -5
```

Expected: a table of regions. `NotAuthenticated` means the fingerprint, key, or
OCIDs do not match — recheck the console snippet rather than guessing.

- [ ] **Step 5: Record your tenancy OCID and home region**

```bash
grep -E '^(tenancy|region)=' ~/.oci/config
```

Keep both; Task 6 needs them. In a fresh tenancy the root compartment OCID is the
tenancy OCID, which is what `compartment_ocid` should be set to.

---

## Task 6: Provision the VM with Terraform

**Files:**
- Create: `infra/main.tf`, `infra/variables.tf`, `infra/outputs.tf`, `infra/cloud-init.yaml`, `infra/terraform.tfvars.example`
- Modify: `.gitignore`

- [ ] **Step 1: Keep Terraform state and secrets out of git**

Append to `.gitignore`:

```
infra/.terraform/
infra/*.tfstate
infra/*.tfstate.*
infra/terraform.tfvars
*.pem
```

Terraform state contains resource details and must not be committed.

- [ ] **Step 2: Write the variables**

Create `infra/variables.tf`:

```hcl
variable "tenancy_ocid" { type = string }
variable "user_ocid" { type = string }
variable "fingerprint" { type = string }
variable "private_key_path" {
  type    = string
  default = "~/.oci/oci_api_key.pem"
}
variable "region" { type = string }

variable "compartment_ocid" {
  type        = string
  description = "Root compartment equals the tenancy OCID in a fresh account."
}

variable "ssh_public_key_path" {
  type    = string
  default = "~/.ssh/id_ed25519.pub"
}

variable "availability_domain" {
  type        = string
  default     = ""
  description = "Leave empty to use the first AD. Set it to retry a different AD when capacity is exhausted."
}

variable "instance_ocpus" {
  type    = number
  default = 2
}

variable "instance_memory_gbs" {
  type    = number
  default = 12
}
```

- [ ] **Step 3: Write the infrastructure**

Create `infra/main.tf`:

```hcl
terraform {
  required_version = ">= 1.5"
  required_providers {
    oci = {
      source  = "oracle/oci"
      version = "~> 6.0"
    }
  }
}

provider "oci" {
  tenancy_ocid     = var.tenancy_ocid
  user_ocid        = var.user_ocid
  fingerprint      = var.fingerprint
  private_key_path = var.private_key_path
  region           = var.region
}

data "oci_identity_availability_domains" "ads" {
  compartment_id = var.tenancy_ocid
}

data "oci_core_images" "ubuntu" {
  compartment_id           = var.compartment_ocid
  operating_system         = "Canonical Ubuntu"
  operating_system_version = "24.04"
  shape                    = "VM.Standard.A1.Flex"
  sort_by                  = "TIMECREATED"
  sort_order               = "DESC"
}

resource "oci_core_vcn" "main" {
  compartment_id = var.compartment_ocid
  cidr_blocks    = ["10.0.0.0/16"]
  display_name   = "ticketing-vcn"
  dns_label      = "ticketing"
}

resource "oci_core_internet_gateway" "igw" {
  compartment_id = var.compartment_ocid
  vcn_id         = oci_core_vcn.main.id
  display_name   = "ticketing-igw"
  enabled        = true
}

resource "oci_core_route_table" "public" {
  compartment_id = var.compartment_ocid
  vcn_id         = oci_core_vcn.main.id
  display_name   = "ticketing-public-rt"

  route_rules {
    destination       = "0.0.0.0/0"
    network_entity_id = oci_core_internet_gateway.igw.id
  }
}

resource "oci_core_security_list" "public" {
  compartment_id = var.compartment_ocid
  vcn_id         = oci_core_vcn.main.id
  display_name   = "ticketing-public-sl"

  egress_security_rules {
    destination = "0.0.0.0/0"
    protocol    = "all"
  }

  ingress_security_rules {
    protocol = "6" # TCP
    source   = "0.0.0.0/0"
    tcp_options {
      min = 22
      max = 22
    }
  }

  ingress_security_rules {
    protocol = "6"
    source   = "0.0.0.0/0"
    tcp_options {
      min = 80
      max = 80
    }
  }

  ingress_security_rules {
    protocol = "6"
    source   = "0.0.0.0/0"
    tcp_options {
      min = 443
      max = 443
    }
  }
}

resource "oci_core_subnet" "public" {
  compartment_id             = var.compartment_ocid
  vcn_id                     = oci_core_vcn.main.id
  cidr_block                 = "10.0.1.0/24"
  display_name               = "ticketing-public-subnet"
  dns_label                  = "public"
  route_table_id             = oci_core_route_table.public.id
  security_list_ids          = [oci_core_security_list.public.id]
  prohibit_public_ip_on_vnic = false
}

resource "oci_core_instance" "app" {
  compartment_id = var.compartment_ocid
  display_name   = "ticketing-app"
  shape          = "VM.Standard.A1.Flex"

  availability_domain = var.availability_domain != "" ? var.availability_domain : data.oci_identity_availability_domains.ads.availability_domains[0].name

  shape_config {
    ocpus         = var.instance_ocpus
    memory_in_gbs = var.instance_memory_gbs
  }

  source_details {
    source_type             = "image"
    source_id               = data.oci_core_images.ubuntu.images[0].id
    boot_volume_size_in_gbs = 50
  }

  create_vnic_details {
    subnet_id        = oci_core_subnet.public.id
    assign_public_ip = true
  }

  metadata = {
    ssh_authorized_keys = file(var.ssh_public_key_path)
    user_data           = base64encode(file("${path.module}/cloud-init.yaml"))
  }
}
```

- [ ] **Step 4: Write cloud-init**

Create `infra/cloud-init.yaml`:

```yaml
#cloud-config
package_update: true
packages:
  - ca-certificates
  - curl
  - iptables-persistent

runcmd:
  - install -m 0755 -d /etc/apt/keyrings
  - curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
  - chmod a+r /etc/apt/keyrings/docker.asc
  - echo "deb [arch=arm64 signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu noble stable" > /etc/apt/sources.list.d/docker.list
  - apt-get update
  - apt-get install -y docker-ce docker-ce-cli containerd.io docker-compose-plugin
  - usermod -aG docker ubuntu
  # OCI's Ubuntu images ship restrictive iptables rules that silently drop 80/443
  # even when the VCN security list allows them. This is the single most common
  # reason a new OCI VM "has no firewall problem" and still times out.
  - iptables -I INPUT 6 -m state --state NEW -p tcp --dport 80 -j ACCEPT
  - iptables -I INPUT 6 -m state --state NEW -p tcp --dport 443 -j ACCEPT
  - netfilter-persistent save
  - mkdir -p /opt/ticketing && chown ubuntu:ubuntu /opt/ticketing
```

- [ ] **Step 5: Write outputs and the tfvars template**

Create `infra/outputs.tf`:

```hcl
output "public_ip" {
  value = oci_core_instance.app.public_ip
}

output "ssh_command" {
  value = "ssh ubuntu@${oci_core_instance.app.public_ip}"
}
```

Create `infra/terraform.tfvars.example`:

```hcl
tenancy_ocid     = "ocid1.tenancy.oc1..REPLACE"
user_ocid        = "ocid1.user.oc1..REPLACE"
fingerprint      = "aa:bb:cc:REPLACE"
region           = "us-sanjose-1"
compartment_ocid = "ocid1.tenancy.oc1..REPLACE" # same as tenancy_ocid in a fresh account
```

- [ ] **Step 6: Plan and apply**

```bash
cd /Users/evan/other/ticketing/infra
cp terraform.tfvars.example terraform.tfvars   # then fill it in
terraform init
terraform plan
terraform apply
```

Expected: `Apply complete!` and a `public_ip` output.

**If apply fails with `Out of host capacity`:** this is the expected Ampere
problem, not a mistake in the configuration. In order of effort:

1. Re-run `terraform apply`. Capacity frees up continuously; retries often
   succeed within hours.
2. Set `availability_domain` in `terraform.tfvars` to a different AD from
   `oci iam availability-domain list` and retry. Some regions have three.
3. Reduce to `instance_ocpus = 1`, `instance_memory_gbs = 6`. Smaller shapes are
   easier to place, and this still runs the stack.
4. Upgrade the account to Pay-As-You-Go. Always Free tenancies are lowest
   priority for A1 capacity; PAYG accounts are placed far more readily and still
   cost nothing while using only Always Free shapes. Set a budget alert at the
   lowest threshold first.

- [ ] **Step 7: Confirm SSH works**

```bash
cd /Users/evan/other/ticketing/infra
ssh -o StrictHostKeyChecking=accept-new ubuntu@$(terraform output -raw public_ip) 'docker --version && echo OK'
```

Expected: a Docker version and `OK`. Cloud-init takes 2–3 minutes after the
instance reports running; if Docker is missing, wait and retry before debugging.

- [ ] **Step 8: Commit**

```bash
cd /Users/evan/other/ticketing
git add infra/ .gitignore
git commit -m "feat: Terraform for the OCI Always Free VM"
```

---

## Task 7: First deploy

- [ ] **Step 1: Build and push the image manually for the first run**

```bash
cd /Users/evan/other/ticketing
echo $GITHUB_TOKEN | docker login ghcr.io -u evanpowell --password-stdin
docker build --platform linux/arm64 -t ghcr.io/evanpowell/ticketing-api:latest .
docker push ghcr.io/evanpowell/ticketing-api:latest
```

If you have no `GITHUB_TOKEN`, `gh auth token` prints one with the right scopes.

- [ ] **Step 2: Make the package public**

On github.com → your profile → Packages → `ticketing-api` → Package settings →
Change visibility → Public. Otherwise the VM needs registry credentials to pull.

- [ ] **Step 3: Copy the stack to the server**

```bash
cd /Users/evan/other/ticketing
IP=$(cd infra && terraform output -raw public_ip)
scp compose.prod.yaml Caddyfile ubuntu@$IP:/opt/ticketing/
```

- [ ] **Step 4: Create the server's .env**

```bash
IP=$(cd infra && terraform output -raw public_ip)
ssh ubuntu@$IP 'cat > /opt/ticketing/.env' <<EOF
DOMAIN=$IP.nip.io
ORACLE_SYS_PASSWORD=$(openssl rand -base64 24 | tr -d /=+)
ORACLE_APP_PASSWORD=$(openssl rand -base64 24 | tr -d /=+)
API_IMAGE=ghcr.io/evanpowell/ticketing-api:latest
EOF
```

`nip.io` resolves `<ip>.nip.io` to that IP, which gives Caddy a real hostname to
obtain a certificate for before a domain is purchased. Task 8 replaces it.

- [ ] **Step 5: Start the stack**

```bash
IP=$(cd infra && terraform output -raw public_ip)
ssh ubuntu@$IP 'cd /opt/ticketing && docker compose -f compose.prod.yaml up -d'
```

- [ ] **Step 6: Watch Oracle come up**

```bash
ssh ubuntu@$IP 'cd /opt/ticketing && docker compose -f compose.prod.yaml ps'
```

Expected: `oracle` reaches `(healthy)`, then `api` starts. First boot initialises
the database; allow a few minutes.

- [ ] **Step 7: Verify the live URL**

```bash
IP=$(cd infra && terraform output -raw public_ip)
curl -s https://$IP.nip.io/health
curl -s -o /dev/null -w '%{http_code}\n' https://$IP.nip.io/
```

Expected: `{"status":"ok","database":true}` and `200`. **The app is now live.**

If it times out, the cause is almost always the instance's own iptables rather
than the VCN. Check with `ssh ubuntu@$IP 'sudo iptables -L INPUT -n --line-numbers | head'`
and confirm the ACCEPT rules for 80 and 443 from cloud-init are present.

---

## Task 8: A real domain and TLS

- [ ] **Step 1: Buy a domain and point it at the VM**

Any registrar. Create an `A` record for the hostname you want (apex or a
subdomain such as `ticketing`) pointing at the Terraform `public_ip` output.

- [ ] **Step 2: Confirm DNS resolves before touching Caddy**

```bash
dig +short ticketing.example.com
```

Expected: the VM's public IP. Let's Encrypt validates over HTTP, so a certificate
request before DNS propagates fails and Caddy backs off — wait for this to be
correct first.

- [ ] **Step 3: Point the stack at the domain**

```bash
IP=$(cd infra && terraform output -raw public_ip)
ssh ubuntu@$IP "sed -i 's|^DOMAIN=.*|DOMAIN=ticketing.example.com|' /opt/ticketing/.env && cd /opt/ticketing && docker compose -f compose.prod.yaml up -d --force-recreate caddy"
```

- [ ] **Step 4: Verify the certificate**

```bash
curl -sI https://ticketing.example.com/health | head -3
echo | openssl s_client -connect ticketing.example.com:443 2>/dev/null | openssl x509 -noout -issuer -dates
```

Expected: `HTTP/2 200`, and an issuer of Let's Encrypt with valid dates.

---

## Task 9: CI/CD

**Files:**
- Create: `.github/workflows/ci.yml`

- [ ] **Step 1: Add the deploy secrets**

```bash
cd /Users/evan/other/ticketing
gh secret set SSH_PRIVATE_KEY < ~/.ssh/id_ed25519
gh secret set SERVER_IP --body "$(cd infra && terraform output -raw public_ip)"

# The health check and the keep-alive both read this. Set it to the real domain
# if Task 8 is done, otherwise the nip.io host from Task 7.
gh variable set PUBLIC_URL --body "https://ticketing.example.com"
```

- [ ] **Step 2: Write the workflow**

Create `.github/workflows/ci.yml`:

```yaml
name: ci

on:
  push:
    branches: [main]
  pull_request:

jobs:
  test:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      # Testcontainers needs a Docker daemon; GitHub runners provide one.
      - run: dotnet test api/Ticketing.slnx --logger "console;verbosity=normal"

  deploy:
    needs: test
    if: github.ref == 'refs/heads/main'
    # Arm runner so the image is built natively for the A1 VM, no QEMU emulation.
    runs-on: ubuntu-24.04-arm
    permissions:
      contents: read
      packages: write
    steps:
      - uses: actions/checkout@v4

      - uses: docker/login-action@v3
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - uses: docker/build-push-action@v6
        with:
          context: .
          push: true
          tags: |
            ghcr.io/${{ github.repository_owner }}/ticketing-api:latest
            ghcr.io/${{ github.repository_owner }}/ticketing-api:${{ github.sha }}

      - name: Deploy over SSH
        env:
          SSH_PRIVATE_KEY: ${{ secrets.SSH_PRIVATE_KEY }}
          SERVER_IP: ${{ secrets.SERVER_IP }}
        run: |
          mkdir -p ~/.ssh
          echo "$SSH_PRIVATE_KEY" > ~/.ssh/deploy_key
          chmod 600 ~/.ssh/deploy_key
          ssh -i ~/.ssh/deploy_key -o StrictHostKeyChecking=accept-new ubuntu@"$SERVER_IP" \
            'cd /opt/ticketing && docker compose -f compose.prod.yaml pull api && docker compose -f compose.prod.yaml up -d api'

      - name: Verify the deployment is healthy
        run: |
          for i in $(seq 1 30); do
            code=$(curl -s -o /dev/null -w '%{http_code}' "${{ vars.PUBLIC_URL }}/health" || true)
            [ "$code" = "200" ] && echo "healthy" && exit 0
            sleep 5
          done
          echo "deployment did not become healthy"
          exit 1
```

The final step matters: without it a deploy that starts a broken container still
reports green.

- [ ] **Step 3: Push and watch it run**

```bash
git add .github/
git commit -m "ci: test, build arm64 image, deploy to OCI"
git push
gh run watch
```

Expected: both jobs succeed.

---

## Task 10: Keep-alive

**Files:**
- Create: `.github/workflows/keepalive.yml`

- [ ] **Step 1: Write the workflow**

Create `.github/workflows/keepalive.yml`:

```yaml
name: keepalive

# OCI reclaims idle Always Free resources. A resume link is clicked months after
# it is written, so something must generate activity. /health touches the
# database, so this keeps both the VM and Oracle from looking idle.
on:
  schedule:
    - cron: '17 6 * * *'
  workflow_dispatch:

jobs:
  ping:
    runs-on: ubuntu-latest
    steps:
      - name: Ping health
        run: |
          code=$(curl -s -o /dev/null -w '%{http_code}' "${{ vars.PUBLIC_URL }}/health")
          echo "health returned $code"
          test "$code" = "200"
```

- [ ] **Step 2: Confirm the URL variable is set**

Task 9 already set this; verify rather than duplicate:

```bash
gh variable get PUBLIC_URL
```

- [ ] **Step 3: Trigger it once to prove it works**

```bash
gh workflow run keepalive.yml
gh run watch
```

Expected: `health returned 200`.

- [ ] **Step 4: Add an external monitor as a second layer**

**GitHub disables scheduled workflows in repositories with no activity for 60
days.** That is precisely the scenario this keep-alive exists for, so it cannot be
the only mechanism. Add a free external monitor (UptimeRobot, Better Stack, or
similar) pinging `/health` every 5 minutes. It is independent of repository
activity and gives you an uptime figure worth putting in the README.

- [ ] **Step 5: Commit**

```bash
git add .github/workflows/keepalive.yml
git commit -m "ci: daily keep-alive so free-tier resources are not reclaimed"
```

---

## Task 11: README

The most-read file in a portfolio repository.

- [ ] **Step 1: Write it**

`README.md` must contain:

- The live link, and a screenshot of the API reference
- One paragraph on what the project is and why ticketing
- **The concurrency section**: `FOR UPDATE NOWAIT`, why NOWAIT rather than a
  blocking wait, the two-way expiry rule, and the unique constraint as the net
- The ORA-12704 finding — a specific, hard-won detail that reads as real experience
- An architecture diagram
- Local setup: `docker compose up`, noting Oracle reaches healthy in about 40s
- The deliberate exclusions from spec §3, each with its rationale
- "What I would change at scale"

- [ ] **Step 2: Verify every command in the README actually works**

Run them in order from a clean clone in a temporary directory. A README with a
command that does not work is worse than no README.

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs: README with live link and design rationale"
```

---

## Done when

- [ ] `https://<domain>/` serves the API reference over a valid Let's Encrypt certificate
- [ ] `https://<domain>/health` returns `{"status":"ok","database":true}`
- [ ] A hold can be created from the browser against live Oracle, and a second attempt on the same seat returns 409
- [ ] Pushing to `main` deploys automatically and the workflow fails if the deploy is unhealthy
- [ ] The keep-alive workflow and an external monitor both ping `/health`
- [ ] `terraform destroy` followed by `terraform apply` reproduces the environment
