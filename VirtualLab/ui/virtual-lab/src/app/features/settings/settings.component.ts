import { Component } from '@angular/core';
import { environment } from '../../../environments/environment';

@Component({
  selector: 'vl-settings',
  standalone: true,
  template: `
    <h2>Settings</h2>
    <p>
      Environment: <strong>{{ environment.environmentName }}</strong>
    </p>
    <p>
      API base URL: <code>{{ environment.apiBaseUrl || '(proxied locally)' }}</code>
    </p>
    <p>
      SignalR hub: <code>{{ environment.signalRHubUrl }}</code>
    </p>
  `,
})
export class SettingsComponent {
  readonly environment = environment;
}
