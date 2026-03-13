import { Routes } from '@angular/router';
import { ALL_ROLES } from '../../core/auth/auth-state';
import { roleGuard } from '../../core/auth/role.guard';

export const ADAPTER_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () => import('./adapters.component').then((m) => m.AdaptersComponent),
    canActivate: [roleGuard(ALL_ROLES)],
  },
  {
    path: ':adapterKey',
    loadComponent: () =>
      import('./adapter-detail.component').then((m) => m.AdapterDetailComponent),
    canActivate: [roleGuard(ALL_ROLES)],
  },
  {
    path: ':adapterKey/sites/:siteId',
    loadComponent: () =>
      import('./site-adapter-config.component').then((m) => m.SiteAdapterConfigComponent),
    canActivate: [roleGuard(ALL_ROLES)],
  },
];
