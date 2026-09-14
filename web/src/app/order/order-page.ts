import { Component, OnInit, inject, input, signal } from '@angular/core';
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
export class OrderPage implements OnInit {
  private readonly api = inject(TicketingApi);

  readonly orderId = input.required<string>();

  protected readonly order = signal<OrderView | null>(null);
  protected readonly error = signal<string | null>(null);

  // Required inputs are populated by the router AFTER construction, so anything
  // that reads them belongs in ngOnInit. Angular 22 rejects this at compile time
  // (NG8118) rather than failing at runtime.
  ngOnInit(): void {
    this.api.order(Number(this.orderId())).subscribe({
      next: order => this.order.set(order),
      error: () => this.error.set('Could not load that order.'),
    });
  }
}
