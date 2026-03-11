import { Routes } from '@angular/router';

export const TRANSACTION_ROUTES: Routes = [
  { path: '', redirectTo: 'list', pathMatch: 'full' },
  {
    path: 'list',
    loadComponent: () =>
      import('./transaction-list.component').then((m) => m.TransactionListComponent),
  },
  {
    path: ':id',
    loadComponent: () =>
      import('./transaction-detail.component').then((m) => m.TransactionDetailComponent),
  },
];
