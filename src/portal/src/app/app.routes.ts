import { Routes } from '@angular/router';
import { MsalGuard, MsalRedirectComponent } from '@azure/msal-angular';
import { ShellComponent } from './core/layout/shell.component';

export const routes: Routes = [
  // MSAL redirect handler — must be at root level
  { path: 'auth', component: MsalRedirectComponent },

  // Access-denied landing
  {
    path: 'access-denied',
    loadComponent: () =>
      import('./shared/components/access-denied/access-denied.component').then(
        (m) => m.AccessDeniedComponent
      ),
  },

  // Protected shell — all feature routes are children
  {
    path: '',
    component: ShellComponent,
    canActivate: [MsalGuard],
    children: [
      { path: '', redirectTo: 'dashboard', pathMatch: 'full' },
      {
        path: 'dashboard',
        loadChildren: () =>
          import('./features/dashboard/dashboard.routes').then(
            (m) => m.DASHBOARD_ROUTES
          ),
      },
      {
        path: 'transactions',
        loadChildren: () =>
          import('./features/transactions/transactions.routes').then(
            (m) => m.TRANSACTION_ROUTES
          ),
      },
      {
        path: 'reconciliation',
        loadChildren: () =>
          import('./features/reconciliation/reconciliation.routes').then(
            (m) => m.RECONCILIATION_ROUTES
          ),
      },
      {
        path: 'agents',
        loadChildren: () =>
          import('./features/edge-agents/edge-agents.routes').then(
            (m) => m.EDGE_AGENT_ROUTES
          ),
      },
      {
        path: 'adapters',
        loadChildren: () =>
          import('./features/adapters/adapters.routes').then((m) => m.ADAPTER_ROUTES),
      },
      {
        path: 'sites',
        loadChildren: () =>
          import('./features/site-config/site-config.routes').then(
            (m) => m.SITE_CONFIG_ROUTES
          ),
      },
      {
        path: 'master-data',
        loadChildren: () =>
          import('./features/master-data/master-data.routes').then(
            (m) => m.MASTER_DATA_ROUTES
          ),
      },
      {
        path: 'audit',
        loadChildren: () =>
          import('./features/audit-log/audit-log.routes').then(
            (m) => m.AUDIT_LOG_ROUTES
          ),
      },
      {
        path: 'dlq',
        loadChildren: () =>
          import('./features/dlq/dlq.routes').then((m) => m.DLQ_ROUTES),
      },
      {
        path: 'settings',
        loadChildren: () =>
          import('./features/settings/settings.routes').then(
            (m) => m.SETTINGS_ROUTES
          ),
      },
      {
        path: 'admin/users',
        loadChildren: () =>
          import('./features/user-management/user-management.routes').then(
            (m) => m.USER_MANAGEMENT_ROUTES
          ),
      },
    ],
  },
];
