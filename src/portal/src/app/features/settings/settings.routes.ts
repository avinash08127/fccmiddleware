import { Routes } from '@angular/router';
import { roleGuard } from '../../core/auth/role.guard';
import { ALL_ROLES } from '../../core/auth/auth-state';

export const SETTINGS_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./settings.component').then((m) => m.SettingsComponent),
    canActivate: [roleGuard(ALL_ROLES)],
  },
];
