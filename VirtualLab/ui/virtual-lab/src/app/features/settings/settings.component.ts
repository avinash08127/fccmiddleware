import { Component } from '@angular/core';
import { runtimeConfig } from '../../core/config/runtime-config';

@Component({
  selector: 'vl-settings',
  standalone: true,
  template: `
    <h2>Settings</h2>
    <p>
      Environment: <strong>{{ runtimeConfig.environmentName }}</strong>
    </p>
    <p>
      API base URL: <code>{{ runtimeConfig.apiBaseUrl || '(proxied locally)' }}</code>
    </p>
    <p>
      SignalR hub: <code>{{ runtimeConfig.signalRHubUrl }}</code>
    </p>
  `,
})
export class SettingsComponent {
  readonly runtimeConfig = runtimeConfig;
}
