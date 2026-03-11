import { Component, inject } from '@angular/core';
import { LiveUpdatesService } from '../../core/services/live-updates.service';

@Component({
  selector: 'vl-live-console',
  standalone: true,
  template: `
    <h2>Live Pump Console</h2>
    <p>SignalR baseline wiring is active.</p>
    <p>
      Connection state: <strong>{{ liveUpdates.connectionState() }}</strong>
    </p>
  `,
})
export class LiveConsoleComponent {
  readonly liveUpdates = inject(LiveUpdatesService);
}
