import { Routes } from '@angular/router';
import { roleGuard } from '../../core/auth/role.guard';
import { AppRole } from '../../core/auth/auth-state';

const PORTAL_USER_ROLES: AppRole[] = ['SystemAdmin', 'SystemAdministrator', 'OperationsManager', 'SiteSupervisor', 'Auditor', 'SupportReadOnly'];

export const MASTER_DATA_ROUTES: Routes = [
  {
    path: '',
    redirectTo: 'status',
    pathMatch: 'full',
  },
  {
    path: 'status',
    loadComponent: () =>
      import('./master-data.component').then((m) => m.MasterDataComponent),
    canActivate: [roleGuard(PORTAL_USER_ROLES)],
  },
];
