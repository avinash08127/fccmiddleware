import { Routes } from '@angular/router';

export const DLQ_ROUTES: Routes = [
  {
    path: '',
    redirectTo: 'list',
    pathMatch: 'full',
  },
  {
    path: 'list',
    loadComponent: () =>
      import('./dlq.component').then((m) => m.DlqComponent),
  },
];
