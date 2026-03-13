import { Routes } from '@angular/router';
import { roleGuard } from '../../core/auth/role.guard';
import { RECONCILIATION_VIEW_ROLES } from './reconciliation.roles';

export const RECONCILIATION_ROUTES: Routes = [
  { path: '', redirectTo: 'exceptions', pathMatch: 'full' },
  {
    path: 'exceptions',
    loadComponent: () =>
      import('./reconciliation-list.component').then((m) => m.ReconciliationListComponent),
    canActivate: [roleGuard(RECONCILIATION_VIEW_ROLES)],
  },
  {
    path: 'exceptions/:id',
    loadComponent: () =>
      import('./reconciliation-detail.component').then((m) => m.ReconciliationDetailComponent),
    canActivate: [roleGuard(RECONCILIATION_VIEW_ROLES)],
  },
];
