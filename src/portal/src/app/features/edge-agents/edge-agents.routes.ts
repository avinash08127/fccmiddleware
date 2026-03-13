import { Routes } from '@angular/router';
import { roleGuard } from '../../core/auth/role.guard';
import { AppRole } from '../../core/auth/auth-state';

const PORTAL_USER_ROLES: AppRole[] = ['SystemAdmin', 'SystemAdministrator', 'OperationsManager', 'SiteSupervisor', 'Auditor', 'SupportReadOnly'];

export const EDGE_AGENT_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./agent-list.component').then((m) => m.AgentListComponent),
    canActivate: [roleGuard(PORTAL_USER_ROLES)],
  },
  {
    path: 'bootstrap-token',
    loadComponent: () =>
      import('./bootstrap-token.component').then(
        (m) => m.BootstrapTokenComponent,
      ),
    canActivate: [roleGuard(['SystemAdmin', 'SystemAdministrator'])],
  },
  {
    path: ':id',
    loadComponent: () =>
      import('./agent-detail.component').then((m) => m.AgentDetailComponent),
    canActivate: [roleGuard(PORTAL_USER_ROLES)],
  },
];
