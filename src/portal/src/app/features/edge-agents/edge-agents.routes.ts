import { Routes } from '@angular/router';
import { roleGuard } from '../../core/auth/role.guard';
import { ALL_ROLES, WRITE_ROLES } from '../../core/auth/auth-state';

export const EDGE_AGENT_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./agent-list.component').then((m) => m.AgentListComponent),
    canActivate: [roleGuard(ALL_ROLES)],
  },
  {
    path: 'bootstrap-token',
    loadComponent: () =>
      import('./bootstrap-token.component').then(
        (m) => m.BootstrapTokenComponent,
      ),
    canActivate: [roleGuard(WRITE_ROLES)],
  },
  {
    path: ':id',
    loadComponent: () =>
      import('./agent-detail.component').then((m) => m.AgentDetailComponent),
    canActivate: [roleGuard(ALL_ROLES)],
  },
];
