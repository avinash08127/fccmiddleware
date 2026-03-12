import { Routes } from '@angular/router';
import { roleGuard } from '../../core/auth/role.guard';

export const EDGE_AGENT_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./agent-list.component').then((m) => m.AgentListComponent),
  },
  {
    path: 'bootstrap-token',
    loadComponent: () =>
      import('./bootstrap-token.component').then(
        (m) => m.BootstrapTokenComponent,
      ),
    canActivate: [roleGuard(['SystemAdmin'])],
  },
  {
    path: ':id',
    loadComponent: () =>
      import('./agent-detail.component').then((m) => m.AgentDetailComponent),
  },
];
