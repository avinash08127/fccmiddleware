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
      import('./dlq-list.component').then((m) => m.DlqListComponent),
  },
  {
    path: 'items/:id',
    loadComponent: () =>
      import('./dlq-detail.component').then((m) => m.DlqDetailComponent),
  },
];
