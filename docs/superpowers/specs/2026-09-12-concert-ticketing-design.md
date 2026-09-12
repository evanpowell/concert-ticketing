# Concert Ticketing — Design Spec

**Date:** 2026-09-12
**Status:** Approved for planning

## 1. Purpose

A working concert ticketing application built on Angular + ASP.NET Core + Oracle,
serving two goals at once:

1. **Learning.** All three layers of this stack are new ground. The code should
   favour conventional, mainstream idioms over clever ones, and the repository
   should explain its own decisions.
2. **Portfolio.** A public, deployed, linkable artifact that demonstrates working
   knowledge of the stack to someone reviewing a resume.

The second goal imposes a requirement the first does not: **a reviewer must be
able to see it running without building anything.** The application is therefore
designed to be deployed from the first commit, not retrofitted for deployment
later.

## 2. Why ticketing

Seat booking contains a genuine concurrency problem — two people selecting the
same seat at the same instant — that cannot be faked or hand-waved. Solving it
correctly requires real transactions, pessimistic locking, and a considered
failure mode. It is the strongest available demonstration of database competence
on this stack, and it is directly testable.

## 3. Scope

### In scope

The buyer flow, end to end:

browse shows → view seat map → select seats → hold for 15 minutes → confirm → view tickets

### Deliberate exclusions

Each is a decision, not an oversight, and each is documented in the README with
its rationale and rough cost to add:

| Excluded | Why |
|---|---|
| Authentication | Adds a whole subsystem without strengthening the concurrency story. Buyers are identified by email plus an opaque hold id. |
| Payment processing | A mocked payment step teaches nothing; a real one is out of proportion. Confirmation creates an order directly. |
| Box office / admin UI | Doubles the frontend surface. Shows and seating charts are created by seed scripts. |
| Email delivery | Operational overhead, no learning value here. |
| Seat map editor | The seating chart is fixed data. |

### Success criteria

1. A reviewer with a browser reaches a working demo in one click.
2. A reviewer with Docker reaches a working local instance with `docker compose up` and no further steps.
3. An automated test demonstrates that concurrent requests for the same seat produce exactly one winner.
4. The README explains the concurrency design well enough to discuss in an interview.

## 4. Stack

Versions verified available on 2026-09-12 for macOS arm64:

| Component | Choice | Notes |
|---|---|---|
| Frontend | Angular (standalone components + signals) | Node 25.2.1 / npm 11.6.2 present |
| API | ASP.NET Core 10, Minimal APIs | .NET SDK 10.0.401 via Homebrew cask `dotnet-sdk` |
| Data access | EF Core 10 + `Oracle.EntityFrameworkCore` 10.23.26301 | 10.x line confirmed on NuGet |
| Database | Oracle Database 23ai Free | `gvenzl/oracle-free`, native linux/arm64 manifest confirmed |
| Tests | xUnit + Testcontainers (Oracle); Playwright | Integration tests run against a real database |
| Deployment | OCI Always Free Ampere A1, Docker Compose, Caddy | A1 is Arm — same architecture as local |

### Data access rationale

EF Core handles the routine 90% (list shows, load a seat map). It cannot express
a pessimistic row lock, so the booking path drops to raw SQL in exactly one
place. That boundary is deliberate and is the most interesting thing in the
codebase.

**Schema is defined in versioned `.sql` scripts, not EF Migrations.** In Oracle
environments DDL is typically script-owned; scripts are also honest about
Oracle-specific DDL and avoid the least reliable corner of the provider.

## 5. Data model

Seats belong to **venues**, not to shows. The sellable unit is `SHOW_SEAT` — the
intersection of a show and a physical seat. That row is the unit of inventory and
the row that gets locked.

```
VENUE ──< SEAT ──────────┐
                         │        SHOW_SEAT is the unit of inventory —
SHOW_EVENT ──< SHOW_SEAT ┘        the row that gets locked.
                   │ │            status: AVAILABLE | HELD | SOLD
                   │ └──> SEAT_HOLD     via nullable hold_id, while HELD
                   │
                   └──── TICKET ──> CUSTOMER_ORDER
                         (unique per SHOW_SEAT: the double-sell net)
```

### Tables

- **VENUE** — `venue_id` PK, `name`, `city`
- **SEAT** — `seat_id` PK, `venue_id` FK, `section`, `row_label`, `seat_number`;
  unique on `(venue_id, section, row_label, seat_number)`
- **SHOW_EVENT** — `show_id` PK, `venue_id` FK, `title`, `artist`, `starts_at`
- **SHOW_SEAT** — `show_seat_id` PK, `show_id` FK, `seat_id` FK, `status`,
  `price_cents`, `hold_id` FK nullable, `expires_at` nullable;
  unique on `(show_id, seat_id)`
- **SEAT_HOLD** — `hold_id` PK, `show_id` FK, `email`, `created_at`, `expires_at`,
  `status` (`ACTIVE` | `CONFIRMED` | `RELEASED` | `EXPIRED`)
- **CUSTOMER_ORDER** — `order_id` PK, `hold_id` FK unique, `email`, `total_cents`, `created_at`
- **TICKET** — `ticket_id` PK, `order_id` FK, `show_seat_id` FK, `price_cents`;
  **unique on `show_seat_id`**

### Oracle-specific naming

`ORDER` and `SHOW` are avoided as table names (`ORDER` is reserved; `SHOW` collides
with SQL*Plus). Surrogate keys use `GENERATED BY DEFAULT AS IDENTITY`. Timestamps
are `TIMESTAMP WITH TIME ZONE`. Money is stored as integer cents in `NUMBER(10)` —
never floating point.

### A deliberate denormalization

`SHOW_SEAT.expires_at` duplicates `SEAT_HOLD.expires_at`. This is intentional: the
locked row is `SHOW_SEAT`, and keeping expiry on that row lets the claimability
decision be made without a join, inside the lock. The cost is that both rows must
be written in the same transaction. This trade-off is documented in the README.

### Indexes

- `SHOW_SEAT (show_id, status)` — seat map rendering
- `SHOW_SEAT (status, expires_at)` — the expiry sweeper

## 6. The booking algorithm

This is the core of the project.

### Hold expiry is evaluated two ways, on purpose

A naive design lets a background job flip expired holds back to `AVAILABLE` and
has everything else read `status`. That is racy: there is always a window where a
hold has expired but has not yet been swept.

- **Authoritative — inside the lock.** A seat is claimable when
  `status = 'AVAILABLE'` **or** (`status = 'HELD'` **and** `expires_at < SYSTIMESTAMP`).
  The booking transaction never trusts the sweeper.
- **Cosmetic — background sweeper.** Every 60 seconds, expired holds are flipped
  to `AVAILABLE` so the seat map and any reporting look correct.

Correctness lives in the transaction. The sweeper is a convenience.

### Creating a hold

`POST /api/shows/{id}/holds` with `{ seatIds[], email }`. All-or-nothing: either
every requested seat is held, or none is.

```sql
SELECT show_seat_id, status, expires_at, price_cents
  FROM show_seat
 WHERE show_id = :showId
   AND seat_id IN (:seatIds)
 ORDER BY show_seat_id
   FOR UPDATE NOWAIT;
```

1. Open a transaction (Oracle default isolation, READ COMMITTED).
2. Run the locking select above. `FOR UPDATE` takes pessimistic row locks.
3. If any row is not claimable by the rule above → roll back → **409 Conflict**.
4. Insert `SEAT_HOLD` with `expires_at = SYSTIMESTAMP + INTERVAL '15' MINUTE`.
5. Update each `SHOW_SEAT` to `HELD` with the new `hold_id` and `expires_at`.
6. Commit, return `{ holdId, expiresAt, seats[] }`.

### Why NOWAIT

`NOWAIT` causes a conflicting transaction to fail immediately with `ORA-00054`
instead of blocking. Two reasons:

1. **Responsiveness.** The user gets an answer in milliseconds rather than waiting
   on a lock held by a stranger.
2. **Deadlock becomes structurally impossible.** No transaction ever waits on a
   lock, so no cycle of waits can form. This matters because a multi-seat hold
   locks several rows at once.

`ORA-00054` is caught and mapped to **409 Conflict**.

### Confirming a hold

`POST /api/holds/{id}/confirm`:

1. Open a transaction; re-lock the hold's seats with `FOR UPDATE NOWAIT`.
2. Verify the hold is `ACTIVE` and `expires_at > SYSTIMESTAMP`; otherwise **410 Gone**.
3. Insert `CUSTOMER_ORDER`, insert one `TICKET` per seat.
4. Set each `SHOW_SEAT.status = 'SOLD'`, clear `expires_at`; set hold to `CONFIRMED`.
5. Commit, return `{ orderId }`.

### Releasing a hold

`DELETE /api/holds/{id}` locks the hold's seats, sets each back to `AVAILABLE`
with `hold_id` and `expires_at` cleared, and sets the hold to `RELEASED`. An
already-expired or already-confirmed hold returns **410 Gone**. Releasing is
idempotent in effect: the seats end up available either way.

### The constraint as a safety net

`TICKET` is unique on `show_seat_id`. If the locking logic were ever wrong, the
database still refuses to sell a seat twice. Application logic *should* prevent
double-selling; a constraint *guarantees* it.

### EF Core integration

The locking select runs through `DbSet.FromSql(...)` inside an explicit
`BeginTransactionAsync()`, which returns tracked entities. Subsequent mutations go
through normal change tracking and `SaveChangesAsync()` on the same transaction.
Raw SQL is confined to the lock statement.

## 7. API surface

Minimal APIs organised with `MapGroup`. Errors use Problem Details (RFC 9457).

```
GET    /api/shows                  upcoming shows
GET    /api/shows/{id}             show detail
GET    /api/shows/{id}/seats       seat map: seat, status, price
POST   /api/shows/{id}/holds       201 {holdId, expiresAt, seats} | 409 seat taken
DELETE /api/holds/{id}             release early → 204 | 410 already expired
POST   /api/holds/{id}/confirm     201 {orderId} | 410 hold expired
GET    /api/orders/{id}            confirmation detail
GET    /health                     liveness + database probe
```

Status codes are chosen, not defaulted. **409** means someone else took the seat;
**410** means the hold expired. These are different situations and the UI responds
to them differently.

## 8. Frontend

Angular standalone components with signals. No `NgModule` (legacy). No NgRx — a
signal-based service is correctly sized here, and the README says why.

Routes mirror the flow:

| Route | Purpose |
|---|---|
| `/shows` | Upcoming shows |
| `/shows/:id` | Seat map, seat selection, "Hold seats" |
| `/holds/:id` | Countdown timer, confirm or cancel |
| `/orders/:id` | Confirmation and tickets |

The seat map is a CSS grid of buttons, keyboard navigable, with ARIA labels
conveying section, row, seat, price, and availability.

**The seat map polls every 3 seconds**, so a seat visibly changes state when
someone else takes it. This is what makes the concurrency work observable in a
browser rather than only in a test. Server-sent events are noted in the README as
the next step.

## 9. Testing

### The centrepiece

Fire N concurrent hold requests for the **same seat** against a real Oracle
instance and assert that **exactly one succeeds and N−1 receive 409.** This test is
the project's thesis in executable form — it makes the central claim checkable
rather than merely asserted.

### Supporting integration tests

- An expired hold's seats are claimable by another buyer.
- A hold cannot be confirmed twice.
- A hold cannot be confirmed after expiry (410).
- The `TICKET` unique constraint rejects a double-sell when the service layer is bypassed.
- The sweeper releases expired holds and touches nothing else.

All integration tests run against Oracle in Testcontainers. One container is
shared across the suite via an xUnit collection fixture, because Oracle start-up
is slow.

### Frontend

Component tests for the seat map (selection, disabled states) and the countdown
timer. One Playwright end-to-end test covering browse → hold → confirm.

## 10. Deployment

Target: **OCI Always Free**, self-hosted Oracle first.

### Topology

A single Ampere A1 VM (2 OCPU, 12 GB RAM, Ubuntu) running three containers:

- **Caddy** — reverse proxy, automatic Let's Encrypt TLS
- **api** — ASP.NET Core, serving the built Angular app from `wwwroot`
- **oracle** — `gvenzl/oracle-free`, data on a named volume

Serving the frontend from the API yields one origin and therefore no CORS
configuration. A1 is Arm, so images built on the Mac run unmodified in production.

### Why self-hosted before Autonomous Database

Production matches local development exactly, there is no mTLS wallet
configuration, and no database idle-reclaim policy applies. Migrating to
Autonomous Database is a planned follow-up, valuable in its own right, and can be
done with a working fallback in place.

### CI/CD

GitHub Actions: run tests → build linux/arm64 images → push to GHCR → deploy over
SSH. Secrets live in GitHub Actions secrets and an `.env` file on the VM;
`.env.example` is committed, `.env` is not.

### Scheduled jobs

Both are functional requirements, not polish:

1. **Hourly demo reset.** The application is publicly writable with no
   authentication. Without a reset, the first bored visitor buys every seat and
   the demo permanently displays a sold-out venue. An hourly job deletes all
   `TICKET`, `CUSTOMER_ORDER` and `SEAT_HOLD` rows, then resets every `SHOW_SEAT`
   to `AVAILABLE` with `hold_id` and `expires_at` cleared. Reference data — venues,
   seats, shows — is never touched. The UI states that demo data resets hourly.
2. **Daily keep-alive.** A GitHub Actions cron requests `/health`. OCI reclaims
   idle Always Free resources, and in the chosen topology it is the **compute**
   idle policy that applies, since the database is a container on that VM rather
   than a managed service. The keep-alive also covers the Autonomous Database
   policy should the planned migration happen: an Always Free ADB is stopped after
   7 days without connections and may be **permanently deleted after 90 cumulative
   days stopped**. For a link on a resume — clicked months after it is written —
   that is a time bomb, so the keep-alive is designed in from the start rather
   than added after an outage.

Basic rate limiting on hold creation, via ASP.NET Core's built-in rate limiter.

## 11. Repository layout

```
ticketing/
├─ db/migrations/     V001__schema.sql, V002__seed.sql, V003__demo_reset.sql
├─ api/
│  ├─ Ticketing.Api/    Program.cs, Endpoints/, Domain/, Data/
│  └─ Ticketing.Tests/
├─ web/                 Angular application
├─ compose.yaml         local: oracle + api + web
├─ compose.prod.yaml    caddy + api + oracle
└─ README.md
```

Two .NET projects rather than the conventional four (`.Core`, `.Data`,
`.Infrastructure`, `.Api`). At this size, folders provide the same separation
without the overhead. The README states this explicitly — knowing when *not* to
split is the more useful signal.

## 12. The README is a deliverable

For a portfolio project the README is the most-read artifact. It must contain:

- A live demo link and a screenshot
- An architecture diagram
- The concurrency explanation: `FOR UPDATE NOWAIT`, the two-way expiry rule, the unique constraint as a net
- An animated capture of two browsers racing for the same seat
- Local setup: `docker compose up`, with the expected Oracle first-boot wait stated
- The deliberate exclusions from §3, each with its rationale
- "What I would change at scale"

## 13. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Ampere A1 "out of host capacity" in popular regions | Home region is permanent — choose a less saturated one at signup. Retry; capacity does free up. |
| Oracle first boot takes 1–3 minutes and the image is ~2 GB | Use the `gvenzl` faststart variant; state the expected wait in the README so it reads as known behaviour, not a hang. |
| Oracle container start-up makes CI slow | One shared container per test suite via collection fixture. |
| Public demo gets vandalized | Hourly reset plus rate limiting (§10). |
| Free-tier resources reclaimed while idle | Daily keep-alive (§10). |
| Accidental OCI charges | Stay within Always Free shapes; set a budget alert at the lowest threshold. |

## 14. Sequencing

1. Database schema and seed scripts; Oracle running locally in Docker.
2. API skeleton with `/health` and EF Core wired to Oracle.
3. Read endpoints: shows, seat map.
4. **The hold transaction** plus its concurrency test.
5. Confirm, release, sweeper.
6. Angular: shows list, seat map, hold countdown, confirmation.
7. Containerize; `docker compose up` works from a clean clone.
8. Deploy to OCI; TLS, CI/CD, reset job, keep-alive.
9. README, diagram, demo capture.

The concurrency work lands at step 4 — early, because it is the part most likely
to reveal that an assumption was wrong.
