import { Routes } from '@angular/router';
import { AppShellComponent } from './core/layout/app-shell.component';
import { DashboardComponent } from './features/dashboard/dashboard.component';
import { SitesComponent } from './features/sites/sites.component';
import { FccProfilesComponent } from './features/fcc-profiles/fcc-profiles.component';
import { ForecourtDesignerComponent } from './features/forecourt-designer/forecourt-designer.component';
import { LiveConsoleComponent } from './features/live-console/live-console.component';
import { PreauthConsoleComponent } from './features/preauth-console/preauth-console.component';
import { TransactionsComponent } from './features/transactions/transactions.component';
import { LogsComponent } from './features/logs/logs.component';
import { ScenariosComponent } from './features/scenarios/scenarios.component';
import { SettingsComponent } from './features/settings/settings.component';

export const routes: Routes = [
  {
    path: '',
    component: AppShellComponent,
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      { path: 'dashboard', component: DashboardComponent, title: 'Dashboard' },
      { path: 'sites', component: SitesComponent, title: 'Sites' },
      { path: 'fcc-profiles', component: FccProfilesComponent, title: 'FCC Profiles' },
      {
        path: 'forecourt-designer',
        component: ForecourtDesignerComponent,
        title: 'Forecourt Designer',
      },
      { path: 'live-console', component: LiveConsoleComponent, title: 'Live Pump Console' },
      { path: 'preauth-console', component: PreauthConsoleComponent, title: 'Pre-Auth Console' },
      { path: 'transactions', component: TransactionsComponent, title: 'Transactions' },
      { path: 'logs', component: LogsComponent, title: 'Logs' },
      { path: 'scenarios', component: ScenariosComponent, title: 'Scenarios' },
      { path: 'settings', component: SettingsComponent, title: 'Settings' },
    ],
  },
];
