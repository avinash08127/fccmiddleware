import { Routes } from '@angular/router';

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
  },
];
