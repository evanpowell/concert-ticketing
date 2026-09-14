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
