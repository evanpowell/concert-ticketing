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
