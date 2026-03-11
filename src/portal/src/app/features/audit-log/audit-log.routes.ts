import { Routes } from '@angular/router';

export const AUDIT_LOG_ROUTES: Routes = [
  { path: '', redirectTo: 'list', pathMatch: 'full' },
  {
    path: 'list',
    loadComponent: () =>
      import('./audit-log.component').then((m) => m.AuditLogComponent),
  },
];
