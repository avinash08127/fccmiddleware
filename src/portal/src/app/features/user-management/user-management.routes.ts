import { Routes } from '@angular/router';
import { roleGuard } from '../../core/auth/role.guard';
import { ADMIN_ROLES } from '../../core/auth/auth-state';

export const USER_MANAGEMENT_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./user-list.component').then((m) => m.UserListComponent),
    canActivate: [roleGuard(ADMIN_ROLES)],
  },
];
