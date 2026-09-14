import { Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { CurrencyPipe } from '@angular/common';
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
  imports: [CurrencyPipe],
  templateUrl: './seat-map-page.html',
  styleUrl: './seat-map-page.css',
})
export class SeatMapPage implements OnInit {
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

  // showId is a required input, populated by the router after construction.
  ngOnInit(): void {
    this.load();
    // Polling is what makes concurrency visible: a seat taken in another window
    // turns amber here. Three seconds feels live without being a load problem.
    const timer = setInterval(() => this.load(), 3000);
    this.destroyRef.onDestroy(() => clearInterval(timer));
  }

  private load(): void {
    this.api.seats(Number(this.showId())).subscribe({
      next: seats => {
        this.seats.set(seats);
        // Drop selections for seats someone else took in the meantime.
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
      if (next.has(seat.seatId)) {
        next.delete(seat.seatId);
      } else {
        next.add(seat.seatId);
      }
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
        ? this.isSelected(seat)
          ? 'selected'
          : `available, $${price}`
        : seat.status.toLowerCase();
    return `Section ${seat.section}, row ${seat.rowLabel}, seat ${seat.seatNumber}, ${state}`;
  }

  protected setEmail(event: Event): void {
    this.email.set((event.target as HTMLInputElement).value);
  }

  protected hold(): void {
    if (this.selectedCount() === 0) return;
    this.submitting.set(true);
    this.error.set(null);

    this.api.createHold(Number(this.showId()), [...this.selected()], this.email()).subscribe({
      next: hold =>
        this.router.navigate(['/holds', hold.holdId], {
          state: { expiresAt: hold.expiresAt },
        }),
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
