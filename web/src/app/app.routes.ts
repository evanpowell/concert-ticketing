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
