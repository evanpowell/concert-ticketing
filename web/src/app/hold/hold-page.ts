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
    // The countdown is presentational only. The server re-checks expiry inside
    // the booking transaction against its own clock, so a wrong client clock
    // cannot buy an expired hold.
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
