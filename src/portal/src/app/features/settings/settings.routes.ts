import { Routes } from '@angular/router';
import { roleGuard } from '../../core/auth/role.guard';

export const SETTINGS_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./settings.component').then((m) => m.SettingsComponent),
    canActivate: [roleGuard(['SystemAdmin', 'OperationsManager'])],
  },
];
