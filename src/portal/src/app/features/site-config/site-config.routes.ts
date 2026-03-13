import { Routes } from '@angular/router';
import { roleGuard } from '../../core/auth/role.guard';
import { ALL_ROLES } from '../../core/auth/auth-state';

export const SITE_CONFIG_ROUTES: Routes = [
  { path: '', redirectTo: 'list', pathMatch: 'full' },
  {
    path: 'list',
    loadComponent: () =>
      import('./site-config.component').then((m) => m.SiteConfigComponent),
    canActivate: [roleGuard(ALL_ROLES)],
  },
  {
    path: ':id',
    loadComponent: () =>
      import('./site-detail.component').then((m) => m.SiteDetailComponent),
    canActivate: [roleGuard(ALL_ROLES)],
  },
];
