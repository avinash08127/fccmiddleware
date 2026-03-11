import { Routes } from '@angular/router';

export const routes: Routes = [
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
    path: 'config',
    loadChildren: () =>
      import('./features/site-config/site-config.routes').then(
        (m) => m.SITE_CONFIG_ROUTES
      ),
  },
  {
    path: 'audit',
    loadChildren: () =>
      import('./features/audit-log/audit-log.routes').then(
        (m) => m.AUDIT_LOG_ROUTES
      ),
  },
  { path: '', redirectTo: 'transactions', pathMatch: 'full' },
];
