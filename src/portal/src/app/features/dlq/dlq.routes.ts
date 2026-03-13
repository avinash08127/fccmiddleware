import { Routes } from '@angular/router';
import { roleGuard } from '../../core/auth/role.guard';

export const DLQ_ROUTES: Routes = [
  {
    path: '',
    redirectTo: 'list',
    pathMatch: 'full',
  },
  {
    path: 'list',
    loadComponent: () =>
      import('./dlq-list.component').then((m) => m.DlqListComponent),
    canActivate: [roleGuard(['SystemAdmin', 'OperationsManager'])],
  },
  {
    path: 'items/:id',
    loadComponent: () =>
      import('./dlq-detail.component').then((m) => m.DlqDetailComponent),
    canActivate: [roleGuard(['SystemAdmin', 'OperationsManager'])],
  },
];
