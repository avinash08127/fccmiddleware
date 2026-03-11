import { Routes } from '@angular/router';

export const EDGE_AGENT_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./agent-list.component').then((m) => m.AgentListComponent),
  },
  {
    path: ':id',
    loadComponent: () =>
      import('./agent-detail.component').then((m) => m.AgentDetailComponent),
  },
];
