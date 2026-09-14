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
