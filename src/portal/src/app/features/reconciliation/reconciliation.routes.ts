import { Routes } from '@angular/router';

export const RECONCILIATION_ROUTES: Routes = [
  { path: '', redirectTo: 'exceptions', pathMatch: 'full' },
  {
    path: 'exceptions',
    loadComponent: () =>
      import('./reconciliation-list.component').then((m) => m.ReconciliationListComponent),
  },
  {
    path: 'exceptions/:id',
    loadComponent: () =>
      import('./reconciliation-detail.component').then((m) => m.ReconciliationDetailComponent),
  },
];
