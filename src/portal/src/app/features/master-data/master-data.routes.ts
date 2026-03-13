import { Routes } from '@angular/router';
import { roleGuard } from '../../core/auth/role.guard';
import { ALL_ROLES } from '../../core/auth/auth-state';

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
    canActivate: [roleGuard(ALL_ROLES)],
  },
];
