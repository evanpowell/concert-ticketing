# Angular Buyer Flow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A browser UI for the complete buyer flow — browse shows, pick seats on a live seat map, hold them for 15 minutes, confirm, see tickets — where two windows racing for the same seat is visibly demonstrable.

**Architecture:** Angular 22 standalone components with signal-based state. No NgRx: a single injectable service holding signals is correctly sized here. The seat map polls every 3 seconds so a seat visibly changes state when someone else takes it — that is what makes the concurrency work observable rather than merely tested.

**Tech Stack:** Angular 22.1.8, TypeScript, Vitest 4, plain CSS.

**Spec:** `docs/superpowers/specs/2026-09-12-concert-ticketing-design.md` §8

**Depends on:** `2026-09-13-database-api-core.md` (complete, tagged `api-core-complete`)

---

## What Angular 22 actually generates

Verified by scaffolding on 2026-09-13. Several defaults differ from older Angular
material you may find online:

1. **Zoneless.** There is no `zone.js` dependency at all. Change detection is
   driven by signals, so anything that updates the UI must be written to a signal.
   A `setInterval` that assigns to a signal triggers re-render correctly; one that
   mutates a plain field does not.
2. **Vitest 4**, not Karma/Jasmine. `ng test` runs Vitest. Zoneless tests use
   `await fixture.whenStable()`.
3. **No `.component.` infix.** Files are `app.ts`, `app.html`, `app.css`. Classes
   are `App`, not `AppComponent`.
4. **Standalone only.** No `NgModule` anywhere; components declare `imports`.
5. **Built-in control flow.** Use `@if` / `@for`, not `*ngIf` / `*ngFor`.
6. `provideHttpClient()` is **not** included by default and must be added.

## An API gap this plan fixes first

`GET /api/shows/{id}/seats` returns `showSeatId`, but `POST /api/shows/{id}/holds`
expects `seatIds` — physical seat ids. Nothing in the seat map response exposes
`seatId`, so a client cannot construct a hold request from it. The API is
currently unusable by any frontend.

Task 1 fixes this by adding `seatId` to the seat map projection.

**The alternative considered:** change the hold endpoint to take `showSeatIds`.
That is arguably purer — the sellable unit is the show-seat — but it would churn
the proven concurrency tests for no behavioural gain, and
`POST /api/shows/1/holds {"seatIds":[1,2]}` already reads correctly: the show
scopes the seats. Exposing `seatId` is the smaller, equally defensible change.

## File structure

```
web/src/app/
  app.ts/.html/.css           shell: header, router outlet
  app.config.ts               providers, incl. provideHttpClient
  app.routes.ts               four routes
  api/
    models.ts                 interfaces mirroring the API contracts
    ticketing-api.ts          typed HttpClient wrapper, one method per endpoint
  shows/shows-page.ts/.html/.css
  seats/seat-map-page.ts/.html/.css
  hold/hold-page.ts/.html/.css
  order/order-page.ts/.html/.css
web/proxy.conf.json           dev server -> API, avoids CORS in development
```

Each page component owns its own fetching. There is no shared store because
nothing is genuinely shared: each route loads what it needs.

---

## Task 1: Expose seatId on the seat map

**Files:**
- Modify: `api/Ticketing.Api/Endpoints/Contracts.cs`, `api/Ticketing.Api/Endpoints/ShowEndpoints.cs`
- Test: `api/Ticketing.Tests/ShowQueryTests.cs`

- [ ] **Step 1: Write the failing assertion**

In `api/Ticketing.Tests/ShowQueryTests.cs`, change the `SeatDto` record to include
`SeatId` and add an assertion to `Seat_map_returns_one_hundred_seats`:

```csharp
    public record SeatDto(int ShowSeatId, int SeatId, string Section, string RowLabel, int SeatNumber, string Status, int PriceCents);
```

and inside that test, after the existing assertions:

```csharp
        // The hold endpoint takes seatIds, so the seat map must expose them or no
        // client can construct a hold request.
        Assert.All(seats, s => Assert.True(s.SeatId > 0));
```

- [ ] **Step 2: Run to verify it fails**

```bash
cd /Users/evan/other/ticketing/api && dotnet test --filter Seat_map_returns_one_hundred_seats
```

Expected: FAIL — `SeatId` deserializes to 0 because the API does not send it.

- [ ] **Step 3: Add SeatId to the contract**

In `api/Ticketing.Api/Endpoints/Contracts.cs`:

```csharp
public record SeatView(int ShowSeatId, int SeatId, string Section, string RowLabel, int SeatNumber, string Status, int PriceCents);
```

- [ ] **Step 4: Populate it in the projection**

In `api/Ticketing.Api/Endpoints/ShowEndpoints.cs`, in the seats projection:

```csharp
                .Select(ss => new SeatView(
                    ss.ShowSeatId,
                    ss.SeatId,
                    ss.Seat.Section,
                    ss.Seat.RowLabel,
                    ss.Seat.SeatNumber,
                    ss.Status == SeatStatus.Held && ss.ExpiresAt != null && ss.ExpiresAt <= DateTimeOffset.UtcNow
                        ? SeatStatus.Available
                        : ss.Status,
                    ss.PriceCents))
```

- [ ] **Step 5: Run the full suite**

```bash
cd /Users/evan/other/ticketing/api && dotnet test
```

Expected: PASS, 24 tests.

- [ ] **Step 6: Commit**

```bash
cd /Users/evan/other/ticketing
git add -A
git commit -m "fix: expose seatId on the seat map so clients can request holds"
```

---

## Task 2: Wire the Angular app to the API

**Files:**
- Create: `web/proxy.conf.json`, `web/src/app/api/models.ts`, `web/src/app/api/ticketing-api.ts`
- Modify: `web/src/app/app.config.ts`, `web/angular.json`

- [ ] **Step 1: Write the dev proxy**

Create `web/proxy.conf.json`:

```json
{
  "/api": { "target": "http://localhost:5099", "secure": false },
  "/health": { "target": "http://localhost:5099", "secure": false }
}
```

The dev server proxies to the API so the browser sees one origin and CORS never
applies in development. In production the API serves the built app itself, so
there is no proxy and still one origin.

- [ ] **Step 2: Point the dev server at the proxy**

In `web/angular.json`, find `projects.web.architect.serve.options` (create the
`options` object if only `configurations` exists) and add:

```json
            "proxyConfig": "proxy.conf.json"
```

- [ ] **Step 3: Write the models**

Create `web/src/app/api/models.ts`:

```typescript
export type SeatStatus = 'AVAILABLE' | 'HELD' | 'SOLD';

export interface ShowSummary {
  showId: number;
  title: string;
  artist: string;
  startsAt: string;
  venueName: string;
}

export interface SeatView {
  showSeatId: number;
  seatId: number;
  section: string;
  rowLabel: string;
  seatNumber: number;
  status: SeatStatus;
  priceCents: number;
}

export interface HoldView {
  holdId: number;
  expiresAt: string;
  showSeatIds: number[];
}

export interface TicketView {
  ticketId: number;
  showSeatId: number;
  section: string;
  rowLabel: string;
  seatNumber: number;
  priceCents: number;
}

export interface OrderView {
  orderId: number;
  email: string;
  totalCents: number;
  createdAt: string;
  tickets: TicketView[];
}

/** RFC 9457 Problem Details, which is what the API returns for every error. */
export interface ProblemDetails {
  title?: string;
  status?: number;
  detail?: string;
}
```

- [ ] **Step 4: Write the API client**

Create `web/src/app/api/ticketing-api.ts`:

```typescript
import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { HoldView, OrderView, SeatView, ShowSummary } from './models';

@Injectable({ providedIn: 'root' })
export class TicketingApi {
  private readonly http = inject(HttpClient);

  shows(): Observable<ShowSummary[]> {
    return this.http.get<ShowSummary[]>('/api/shows');
  }

  show(showId: number): Observable<ShowSummary> {
    return this.http.get<ShowSummary>(`/api/shows/${showId}`);
  }

  seats(showId: number): Observable<SeatView[]> {
    return this.http.get<SeatView[]>(`/api/shows/${showId}/seats`);
  }

  createHold(showId: number, seatIds: number[], email: string): Observable<HoldView> {
    return this.http.post<HoldView>(`/api/shows/${showId}/holds`, { seatIds, email });
  }

  releaseHold(holdId: number): Observable<void> {
    return this.http.delete<void>(`/api/holds/${holdId}`);
  }

  confirmHold(holdId: number): Observable<{ orderId: number }> {
    return this.http.post<{ orderId: number }>(`/api/holds/${holdId}/confirm`, null);
  }

  order(orderId: number): Observable<OrderView> {
    return this.http.get<OrderView>(`/api/orders/${orderId}`);
  }
}
```

- [ ] **Step 5: Provide HttpClient**

Replace `web/src/app/app.config.ts`:

```typescript
import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideHttpClient(),
    provideRouter(routes),
  ],
};
```

- [ ] **Step 6: Verify the app still builds**

```bash
cd /Users/evan/other/ticketing/web && npm run build
```

Expected: `Application bundle generation complete`.

- [ ] **Step 7: Commit**

```bash
cd /Users/evan/other/ticketing
git add web/
git commit -m "feat: Angular app scaffold with typed API client"
```

---

## Task 3: Shell and routes

**Files:**
- Modify: `web/src/app/app.routes.ts`, `web/src/app/app.html`, `web/src/app/app.css`, `web/src/styles.css`, `web/src/app/app.spec.ts`

- [ ] **Step 1: Define the routes**

Replace `web/src/app/app.routes.ts`:

```typescript
import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'shows' },
  {
    path: 'shows',
    loadComponent: () => import('./shows/shows-page').then(m => m.ShowsPage),
  },
  {
    path: 'shows/:showId',
    loadComponent: () => import('./seats/seat-map-page').then(m => m.SeatMapPage),
  },
  {
    path: 'holds/:holdId',
    loadComponent: () => import('./hold/hold-page').then(m => m.HoldPage),
  },
  {
    path: 'orders/:orderId',
    loadComponent: () => import('./order/order-page').then(m => m.OrderPage),
  },
  { path: '**', redirectTo: 'shows' },
];
```

`loadComponent` lazy-loads each route, so the initial bundle stays small.

- [ ] **Step 2: Write the shell template**

Replace `web/src/app/app.html` (the generated file is a 20 KB placeholder):

```html
<header class="site-header">
  <a routerLink="/shows" class="brand">The Chapel</a>
  <span class="tagline">Concert ticketing demo</span>
</header>

<main class="container">
  <router-outlet />
</main>

<footer class="site-footer">
  <p>Demo data resets hourly. No real tickets are sold.</p>
</footer>
```

- [ ] **Step 3: Import RouterLink in the shell**

Replace `web/src/app/app.ts`:

```typescript
import { Component } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {}
```

- [ ] **Step 4: Write global styles**

Replace `web/src/styles.css`:

```css
:root {
  --bg: #0f1115;
  --surface: #171a21;
  --surface-2: #1f232c;
  --text: #e8eaed;
  --muted: #9aa3b2;
  --accent: #7c5cff;
  --available: #2f6f4f;
  --available-hover: #3d8a63;
  --selected: #7c5cff;
  --held: #7a6320;
  --sold: #3a3f4a;
  --danger: #b4453c;
  --radius: 8px;
}

* { box-sizing: border-box; }

body {
  margin: 0;
  background: var(--bg);
  color: var(--text);
  font: 16px/1.5 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
}

a { color: var(--accent); }

button {
  font: inherit;
  cursor: pointer;
  border-radius: var(--radius);
  border: 1px solid transparent;
}

button:disabled { cursor: not-allowed; opacity: 0.55; }

/* Visible focus matters here: the seat map is keyboard navigable. */
:focus-visible {
  outline: 3px solid var(--accent);
  outline-offset: 2px;
}
```

- [ ] **Step 5: Write shell styles**

Replace `web/src/app/app.css`:

```css
.site-header {
  display: flex;
  align-items: baseline;
  gap: 0.75rem;
  padding: 1rem 1.5rem;
  border-bottom: 1px solid var(--surface-2);
}

.brand {
  font-size: 1.25rem;
  font-weight: 600;
  text-decoration: none;
  color: var(--text);
}

.tagline { color: var(--muted); font-size: 0.9rem; }

.container {
  max-width: 900px;
  margin: 0 auto;
  padding: 1.5rem;
}

.site-footer {
  max-width: 900px;
  margin: 0 auto;
  padding: 0 1.5rem 2rem;
  color: var(--muted);
  font-size: 0.85rem;
}
```

- [ ] **Step 6: Fix the generated spec**

The scaffolded `app.spec.ts` asserts on placeholder markup that no longer exists.
Replace `web/src/app/app.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';
import { routes } from './app.routes';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter(routes)],
    }).compileComponents();
  });

  it('renders the site header', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('.brand')?.textContent).toContain('The Chapel');
  });
});
```

- [ ] **Step 7: Commit**

```bash
cd /Users/evan/other/ticketing
git add web/
git commit -m "feat: app shell, routes, and base styling"
```

---

## Task 4: Shows list

**Files:**
- Create: `web/src/app/shows/shows-page.ts`, `shows-page.html`, `shows-page.css`

- [ ] **Step 1: Write the component**

Create `web/src/app/shows/shows-page.ts`:

```typescript
import { Component, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { TicketingApi } from '../api/ticketing-api';
import { ShowSummary } from '../api/models';

@Component({
  selector: 'app-shows-page',
  imports: [RouterLink, DatePipe],
  templateUrl: './shows-page.html',
  styleUrl: './shows-page.css',
})
export class ShowsPage {
  private readonly api = inject(TicketingApi);

  // Zoneless change detection is signal-driven: assigning to these re-renders.
  protected readonly shows = signal<ShowSummary[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  constructor() {
    this.api.shows().subscribe({
      next: shows => {
        this.shows.set(shows);
        this.loading.set(false);
      },
      error: () => {
        this.error.set('Could not load shows. Is the API running?');
        this.loading.set(false);
      },
    });
  }
}
```

- [ ] **Step 2: Write the template**

Create `web/src/app/shows/shows-page.html`:

```html
<h1>Upcoming shows</h1>

@if (loading()) {
  <p class="muted">Loading…</p>
} @else if (error()) {
  <p class="error">{{ error() }}</p>
} @else {
  <ul class="show-list">
    @for (show of shows(); track show.showId) {
      <li class="show-card">
        <a [routerLink]="['/shows', show.showId]">
          <span class="artist">{{ show.artist }}</span>
          <span class="title">{{ show.title }}</span>
          <span class="meta">
            {{ show.startsAt | date: 'EEE d MMM, h:mm a' }} · {{ show.venueName }}
          </span>
        </a>
      </li>
    } @empty {
      <li class="muted">No shows scheduled.</li>
    }
  </ul>
}
```

`@for` requires `track`. `@empty` handles the empty case without a separate `@if`.

- [ ] **Step 3: Write the styles**

Create `web/src/app/shows/shows-page.css`:

```css
h1 { font-size: 1.5rem; margin-top: 0; }
.muted { color: var(--muted); }
.error { color: var(--danger); }

.show-list { list-style: none; padding: 0; display: grid; gap: 0.75rem; }

.show-card a {
  display: grid;
  gap: 0.25rem;
  padding: 1rem;
  background: var(--surface);
  border: 1px solid var(--surface-2);
  border-radius: var(--radius);
  text-decoration: none;
  color: var(--text);
}

.show-card a:hover { border-color: var(--accent); }
.artist { font-weight: 600; font-size: 1.1rem; }
.title { color: var(--muted); }
.meta { color: var(--muted); font-size: 0.9rem; }
```

- [ ] **Step 4: Verify against the running API**

Start the API in one terminal:

```bash
cd /Users/evan/other/ticketing/api/Ticketing.Api && ASPNETCORE_ENVIRONMENT=Development dotnet run --urls http://localhost:5099
```

and the dev server in another:

```bash
cd /Users/evan/other/ticketing/web && npm start
```

Open `http://localhost:4200`. Expected: three shows — The Gloaming, Lankum,
Caoimhin O Raghallaigh — each linking to its seat map.

- [ ] **Step 5: Commit**

```bash
cd /Users/evan/other/ticketing
git add web/
git commit -m "feat: shows list page"
```

---

## Task 5: The seat map

The centrepiece of the UI. It polls, so a seat taken in another window goes grey
here within three seconds.

**Files:**
- Create: `web/src/app/seats/seat-map-page.ts`, `seat-map-page.html`, `seat-map-page.css`

- [ ] **Step 1: Write the component**

Create `web/src/app/seats/seat-map-page.ts`:

```typescript
import { Component, DestroyRef, computed, inject, input, signal } from '@angular/core';
import { Router } from '@angular/router';
import { TicketingApi } from '../api/ticketing-api';
import { SeatView } from '../api/models';

interface SeatRow {
  section: string;
  rowLabel: string;
  seats: SeatView[];
}

@Component({
  selector: 'app-seat-map-page',
  imports: [],
  templateUrl: './seat-map-page.html',
  styleUrl: './seat-map-page.css',
})
export class SeatMapPage {
  private readonly api = inject(TicketingApi);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  /** Bound from the :showId route parameter via withComponentInputBinding. */
  readonly showId = input.required<string>();

  protected readonly seats = signal<SeatView[]>([]);
  protected readonly selected = signal<Set<number>>(new Set());
  protected readonly email = signal('buyer@example.com');
  protected readonly submitting = signal(false);
  protected readonly error = signal<string | null>(null);

  /** Group flat seats into section/row bands for rendering. */
  protected readonly rows = computed<SeatRow[]>(() => {
    const byRow = new Map<string, SeatRow>();
    for (const seat of this.seats()) {
      const key = `${seat.section}-${seat.rowLabel}`;
      if (!byRow.has(key)) {
        byRow.set(key, { section: seat.section, rowLabel: seat.rowLabel, seats: [] });
      }
      byRow.get(key)!.seats.push(seat);
    }
    return [...byRow.values()];
  });

  protected readonly selectedCount = computed(() => this.selected().size);

  protected readonly selectedTotalCents = computed(() => {
    const chosen = this.selected();
    return this.seats()
      .filter(s => chosen.has(s.seatId))
      .reduce((sum, s) => sum + s.priceCents, 0);
  });

  constructor() {
    this.load();
    // Polling is what makes concurrency visible: a seat taken in another window
    // turns grey here. Three seconds is frequent enough to feel live without
    // being a load problem for a demo.
    const timer = setInterval(() => this.load(), 3000);
    this.destroyRef.onDestroy(() => clearInterval(timer));
  }

  private load(): void {
    this.api.seats(Number(this.showId())).subscribe({
      next: seats => {
        this.seats.set(seats);
        // Drop selections for seats someone else has taken in the meantime.
        const stillSelectable = new Set(
          seats.filter(s => s.status === 'AVAILABLE').map(s => s.seatId),
        );
        this.selected.update(current => {
          const next = new Set<number>();
          for (const id of current) if (stillSelectable.has(id)) next.add(id);
          return next;
        });
      },
      error: () => this.error.set('Could not load the seat map.'),
    });
  }

  protected toggle(seat: SeatView): void {
    if (seat.status !== 'AVAILABLE') return;
    this.selected.update(current => {
      const next = new Set(current);
      next.has(seat.seatId) ? next.delete(seat.seatId) : next.add(seat.seatId);
      return next;
    });
  }

  protected isSelected(seat: SeatView): boolean {
    return this.selected().has(seat.seatId);
  }

  protected seatLabel(seat: SeatView): string {
    const price = (seat.priceCents / 100).toFixed(2);
    const state =
      seat.status === 'AVAILABLE'
        ? this.isSelected(seat) ? 'selected' : `available, $${price}`
        : seat.status.toLowerCase();
    return `Section ${seat.section}, row ${seat.rowLabel}, seat ${seat.seatNumber}, ${state}`;
  }

  protected hold(): void {
    if (this.selectedCount() === 0) return;
    this.submitting.set(true);
    this.error.set(null);

    this.api.createHold(Number(this.showId()), [...this.selected()], this.email()).subscribe({
      next: hold => this.router.navigate(['/holds', hold.holdId]),
      error: err => {
        this.submitting.set(false);
        this.error.set(
          err.status === 409
            ? 'Someone just took one of those seats. The map has been refreshed.'
            : 'Could not hold those seats.',
        );
        this.load();
      },
    });
  }
}
```

- [ ] **Step 2: Enable route parameters as component inputs**

`input.required<string>()` for `showId` only works when the router binds route
params to inputs. In `web/src/app/app.config.ts`, change the router provider:

```typescript
import { provideRouter, withComponentInputBinding } from '@angular/router';
```

```typescript
    provideRouter(routes, withComponentInputBinding()),
```

- [ ] **Step 3: Write the template**

Create `web/src/app/seats/seat-map-page.html`:

```html
<h1>Choose your seats</h1>

<p class="legend">
  <span class="swatch available"></span> Available
  <span class="swatch selected"></span> Selected
  <span class="swatch held"></span> On hold
  <span class="swatch sold"></span> Sold
</p>

<div class="stage">STAGE</div>

@for (row of rows(); track row.section + row.rowLabel) {
  <div class="row">
    <span class="row-label">{{ row.section }}{{ row.rowLabel }}</span>
    <div class="seats">
      @for (seat of row.seats; track seat.showSeatId) {
        <button
          type="button"
          class="seat"
          [class.is-selected]="isSelected(seat)"
          [class.is-held]="seat.status === 'HELD'"
          [class.is-sold]="seat.status === 'SOLD'"
          [disabled]="seat.status !== 'AVAILABLE'"
          [attr.aria-pressed]="isSelected(seat)"
          [attr.aria-label]="seatLabel(seat)"
          (click)="toggle(seat)">
          {{ seat.seatNumber }}
        </button>
      }
    </div>
  </div>
}

<div class="checkout">
  <label>
    Email
    <input
      type="email"
      [value]="email()"
      (input)="email.set($any($event.target).value)" />
  </label>

  <p class="summary">
    {{ selectedCount() }} seat{{ selectedCount() === 1 ? '' : 's' }} ·
    {{ selectedTotalCents() / 100 | currency: 'USD' }}
  </p>

  @if (error()) {
    <p class="error" role="alert">{{ error() }}</p>
  }

  <button
    type="button"
    class="primary"
    [disabled]="selectedCount() === 0 || submitting()"
    (click)="hold()">
    {{ submitting() ? 'Holding…' : 'Hold for 15 minutes' }}
  </button>
</div>
```

- [ ] **Step 4: Import CurrencyPipe**

The template uses `| currency`, so add it to the component imports in
`seat-map-page.ts`:

```typescript
import { CurrencyPipe } from '@angular/common';
```

```typescript
  imports: [CurrencyPipe],
```

- [ ] **Step 5: Write the styles**

Create `web/src/app/seats/seat-map-page.css`:

```css
h1 { font-size: 1.5rem; margin-top: 0; }

.legend { display: flex; align-items: center; gap: 0.5rem; color: var(--muted); font-size: 0.85rem; }
.swatch { width: 14px; height: 14px; border-radius: 3px; display: inline-block; }
.swatch.available { background: var(--available); }
.swatch.selected { background: var(--selected); }
.swatch.held { background: var(--held); }
.swatch.sold { background: var(--sold); }
.swatch + .swatch { margin-left: 0.75rem; }

.stage {
  margin: 1.5rem 0;
  padding: 0.5rem;
  text-align: center;
  letter-spacing: 0.3em;
  color: var(--muted);
  background: var(--surface);
  border-radius: var(--radius);
}

.row { display: flex; align-items: center; gap: 0.75rem; margin-bottom: 0.5rem; }
.row-label { width: 2.5rem; color: var(--muted); font-size: 0.85rem; }
.seats { display: flex; flex-wrap: wrap; gap: 0.35rem; }

.seat {
  width: 2.25rem;
  height: 2.25rem;
  background: var(--available);
  color: var(--text);
  transition: background 120ms ease;
}

.seat:hover:not(:disabled) { background: var(--available-hover); }
.seat.is-selected { background: var(--selected); }
.seat.is-held { background: var(--held); }
.seat.is-sold { background: var(--sold); }

.checkout {
  margin-top: 2rem;
  padding: 1rem;
  background: var(--surface);
  border-radius: var(--radius);
  display: grid;
  gap: 0.75rem;
  justify-items: start;
}

.checkout input {
  display: block;
  margin-top: 0.25rem;
  padding: 0.5rem;
  background: var(--surface-2);
  border: 1px solid var(--surface-2);
  border-radius: var(--radius);
  color: var(--text);
  min-width: 18rem;
}

.summary { margin: 0; color: var(--muted); }
.error { color: var(--danger); margin: 0; }

.primary {
  padding: 0.65rem 1.25rem;
  background: var(--accent);
  color: white;
  font-weight: 600;
}
```

- [ ] **Step 6: Verify in the browser**

With the API and dev server running, open a show. Expected: 100 seats in ten
rows, section A priced higher than B, clicking toggles selection, and the summary
updates.

- [ ] **Step 7: Commit**

```bash
cd /Users/evan/other/ticketing
git add web/
git commit -m "feat: polling seat map with keyboard-accessible seat selection"
```

---

## Task 6: Hold countdown

**Files:**
- Create: `web/src/app/hold/hold-page.ts`, `hold-page.html`, `hold-page.css`

- [ ] **Step 1: Write the component**

Create `web/src/app/hold/hold-page.ts`:

```typescript
import { Component, DestroyRef, computed, inject, input, signal } from '@angular/core';
import { Router } from '@angular/router';
import { TicketingApi } from '../api/ticketing-api';

@Component({
  selector: 'app-hold-page',
  imports: [],
  templateUrl: './hold-page.html',
  styleUrl: './hold-page.css',
})
export class HoldPage {
  private readonly api = inject(TicketingApi);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly holdId = input.required<string>();

  protected readonly expiresAt = signal<Date | null>(null);
  protected readonly now = signal(new Date());
  protected readonly submitting = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly secondsLeft = computed(() => {
    const expiry = this.expiresAt();
    if (!expiry) return null;
    return Math.max(0, Math.floor((expiry.getTime() - this.now().getTime()) / 1000));
  });

  protected readonly countdown = computed(() => {
    const seconds = this.secondsLeft();
    if (seconds === null) return '';
    const m = Math.floor(seconds / 60);
    const s = seconds % 60;
    return `${m}:${String(s).padStart(2, '0')}`;
  });

  protected readonly expired = computed(() => this.secondsLeft() === 0);

  constructor() {
    // The hold's expiry came from the API; the countdown is presentational only.
    // The server re-checks expiry inside the booking transaction, so a client
    // clock that is wrong cannot buy an expired hold.
    const state = history.state as { expiresAt?: string };
    if (state?.expiresAt) this.expiresAt.set(new Date(state.expiresAt));

    const timer = setInterval(() => this.now.set(new Date()), 1000);
    this.destroyRef.onDestroy(() => clearInterval(timer));
  }

  protected confirm(): void {
    this.submitting.set(true);
    this.error.set(null);

    this.api.confirmHold(Number(this.holdId())).subscribe({
      next: order => this.router.navigate(['/orders', order.orderId]),
      error: err => {
        this.submitting.set(false);
        this.error.set(
          err.status === 410
            ? 'This hold expired. Your seats have been released.'
            : 'Could not confirm this hold.',
        );
      },
    });
  }

  protected cancel(): void {
    this.api.releaseHold(Number(this.holdId())).subscribe({
      next: () => this.router.navigate(['/shows']),
      error: () => this.router.navigate(['/shows']),
    });
  }
}
```

- [ ] **Step 2: Pass expiry through navigation**

In `web/src/app/seats/seat-map-page.ts`, change the successful hold navigation so
the countdown has an expiry to render:

```typescript
      next: hold => this.router.navigate(['/holds', hold.holdId], {
        state: { expiresAt: hold.expiresAt },
      }),
```

- [ ] **Step 3: Write the template**

Create `web/src/app/hold/hold-page.html`:

```html
<h1>Your seats are held</h1>

@if (expired()) {
  <p class="expired" role="alert">
    This hold has expired and the seats have been released.
  </p>
  <button type="button" class="primary" (click)="cancel()">Back to shows</button>
} @else {
  <p class="countdown" aria-live="polite">
    {{ countdown() }} remaining
  </p>
  <p class="muted">
    Confirm before the timer runs out, or the seats return to the pool for
    someone else.
  </p>

  @if (error()) {
    <p class="error" role="alert">{{ error() }}</p>
  }

  <div class="actions">
    <button type="button" class="primary" [disabled]="submitting()" (click)="confirm()">
      {{ submitting() ? 'Confirming…' : 'Confirm purchase' }}
    </button>
    <button type="button" class="secondary" (click)="cancel()">Release seats</button>
  </div>
}
```

- [ ] **Step 4: Write the styles**

Create `web/src/app/hold/hold-page.css`:

```css
h1 { font-size: 1.5rem; margin-top: 0; }
.countdown { font-size: 2.5rem; font-variant-numeric: tabular-nums; margin: 0.5rem 0; }
.muted { color: var(--muted); }
.error, .expired { color: var(--danger); }
.actions { display: flex; gap: 0.75rem; margin-top: 1.5rem; }

.primary { padding: 0.65rem 1.25rem; background: var(--accent); color: white; font-weight: 600; }
.secondary { padding: 0.65rem 1.25rem; background: var(--surface-2); color: var(--text); }
```

- [ ] **Step 5: Verify in the browser**

Hold seats, and confirm the countdown starts near 15:00 and ticks down each
second.

- [ ] **Step 6: Commit**

```bash
cd /Users/evan/other/ticketing
git add web/
git commit -m "feat: hold page with expiry countdown"
```

---

## Task 7: Order confirmation

**Files:**
- Create: `web/src/app/order/order-page.ts`, `order-page.html`, `order-page.css`

- [ ] **Step 1: Write the component**

Create `web/src/app/order/order-page.ts`:

```typescript
import { Component, inject, input, signal } from '@angular/core';
import { CurrencyPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { TicketingApi } from '../api/ticketing-api';
import { OrderView } from '../api/models';

@Component({
  selector: 'app-order-page',
  imports: [CurrencyPipe, RouterLink],
  templateUrl: './order-page.html',
  styleUrl: './order-page.css',
})
export class OrderPage {
  private readonly api = inject(TicketingApi);

  readonly orderId = input.required<string>();

  protected readonly order = signal<OrderView | null>(null);
  protected readonly error = signal<string | null>(null);

  constructor() {
    this.api.order(Number(this.orderId())).subscribe({
      next: order => this.order.set(order),
      error: () => this.error.set('Could not load that order.'),
    });
  }
}
```

- [ ] **Step 2: Write the template**

Create `web/src/app/order/order-page.html`:

```html
@if (error()) {
  <p class="error" role="alert">{{ error() }}</p>
} @else if (order(); as o) {
  <h1>You're going</h1>
  <p class="muted">Order #{{ o.orderId }} · confirmation sent to {{ o.email }}</p>

  <ul class="tickets">
    @for (ticket of o.tickets; track ticket.ticketId) {
      <li class="ticket">
        <span class="seat">Section {{ ticket.section }}, row {{ ticket.rowLabel }}, seat {{ ticket.seatNumber }}</span>
        <span class="price">{{ ticket.priceCents / 100 | currency: 'USD' }}</span>
      </li>
    }
  </ul>

  <p class="total">Total {{ o.totalCents / 100 | currency: 'USD' }}</p>

  <a routerLink="/shows">Back to shows</a>
} @else {
  <p class="muted">Loading…</p>
}
```

`@else if (order(); as o)` binds the non-null value to `o`, so the template does
not repeat `order()!` on every line.

- [ ] **Step 3: Write the styles**

Create `web/src/app/order/order-page.css`:

```css
h1 { font-size: 1.5rem; margin-top: 0; }
.muted { color: var(--muted); }
.error { color: var(--danger); }

.tickets { list-style: none; padding: 0; display: grid; gap: 0.5rem; margin: 1.5rem 0; }

.ticket {
  display: flex;
  justify-content: space-between;
  padding: 0.75rem 1rem;
  background: var(--surface);
  border-radius: var(--radius);
}

.price { color: var(--muted); font-variant-numeric: tabular-nums; }
.total { font-weight: 600; }
```

- [ ] **Step 4: Verify the whole flow**

Browse → pick two seats → hold → confirm → see both tickets and a total equal to
their sum.

- [ ] **Step 5: Commit**

```bash
cd /Users/evan/other/ticketing
git add web/
git commit -m "feat: order confirmation page"
```

---

## Task 8: Serve the built app from the API

One origin in production means no CORS, one container, one certificate.

**Files:**
- Modify: `api/Ticketing.Api/Program.cs`, `Dockerfile`

- [ ] **Step 1: Serve static files and fall back to index.html**

In `api/Ticketing.Api/Program.cs`, after `app.UseRateLimiter();`:

```csharp
// In production the Angular build is served from wwwroot by this same app, so
// there is one origin and CORS never applies. MapFallbackToFile sends unknown
// paths to index.html so client-side routes survive a page refresh.
app.UseDefaultFiles();
app.UseStaticFiles();
```

and after the endpoint mappings, before `app.Run();`:

```csharp
if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html")))
    app.MapFallbackToFile("index.html");
```

The guard matters: the integration tests run the API with no frontend built, and
an unguarded `MapFallbackToFile` would fail at startup. Verified 2026-09-13 that
`/nope.js` still returns 404 rather than the app shell — the fallback does not
swallow missing assets.

**Note:** the Scalar docs UI is mapped at `/`, which would collide with the
Angular app. Move the docs to `/docs`:

```csharp
app.MapScalarApiReference("/docs", options => options
    .WithTitle("Concert Ticketing API")
    .WithTheme(ScalarTheme.Purple));
```

Update `ApiDocsTests.Root_serves_the_interactive_docs_ui` to request `/docs`.

- [ ] **Step 2: Build the Angular app in the Docker image**

Replace the build stage of `Dockerfile` so the frontend is built alongside:

```dockerfile
# Frontend build stage.
FROM node:22-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

# API build stage.
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
# Angular emits to dist/web/browser; copy its CONTENTS to the wwwroot root so
# "/" serves index.html rather than "/browser/index.html".
COPY --from=web /web/dist/web/browser/ ./wwwroot/
COPY db/migrations/ ./migrations/
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Ticketing.Api.dll"]
```

- [ ] **Step 3: Build and run the combined image**

```bash
cd /Users/evan/other/ticketing
docker build -t ticketing-api:local .
docker run --rm -d --name ticketing-full -p 5101:8080 \
  -e "ConnectionStrings__Ticketing=User Id=ticketing;Password=ticketing;Data Source=host.docker.internal:1521/FREEPDB1" \
  ticketing-api:local
```

- [ ] **Step 4: Verify one origin serves everything**

```bash
curl -s -o /dev/null -w 'app   %{http_code}\n' http://localhost:5101/
curl -s -o /dev/null -w 'docs  %{http_code}\n' http://localhost:5101/docs
curl -s -o /dev/null -w 'api   %{http_code}\n' http://localhost:5101/api/shows
curl -s -o /dev/null -w 'route %{http_code}\n' http://localhost:5101/shows/1
```

Expected: all `200`. The last one matters most — it proves a deep client-side
route survives a refresh rather than 404ing.

- [ ] **Step 5: Stop the container and commit**

```bash
docker stop ticketing-full
cd /Users/evan/other/ticketing
git add -A
git commit -m "feat: serve the Angular build from the API for single-origin production"
```

---

## Task 9: Prove the race in a browser

The demo that sells the project.

- [ ] **Step 1: Reset the demo data**

```bash
cd /Users/evan/other/ticketing
docker compose exec -T oracle sqlplus -S ticketing/ticketing@localhost:1521/FREEPDB1 <<'SQL'
DELETE FROM ticket;
DELETE FROM customer_order;
UPDATE show_seat SET status='AVAILABLE', hold_id=NULL, expires_at=NULL;
DELETE FROM seat_hold;
COMMIT;
EXIT;
SQL
```

- [ ] **Step 2: Open the same show in two browser windows**

Side by side, both on `/shows/1`.

- [ ] **Step 3: Take a seat in window one**

Select seat A1-1 and hold it.

- [ ] **Step 4: Watch window two**

Within three seconds that seat turns amber (`HELD`) without any interaction.
**That is the polling and the database lock made visible.**

- [ ] **Step 5: Try to take the same seat in window two**

Select it — it is disabled. Select a different seat and confirm the flow still
works.

- [ ] **Step 6: Capture it**

Record a short screen capture of both windows for the README. This is the single
most persuasive artifact in the repository.

---

## Done when

- [ ] `npm start` + the API serves the complete buyer flow at `localhost:4200`
- [ ] The production image serves app, docs, and API from one origin, and deep routes survive refresh
- [ ] A seat taken in one window turns amber in another within three seconds
- [ ] Attempting a held seat is prevented in the UI, and a 409 from the API is handled gracefully if it slips through
- [ ] `dotnet test` still passes (24 tests) and `npm run build` succeeds
