import { Component } from '@angular/core';

@Component({
  selector: 'vl-logs',
  standalone: true,
  template: `
    <h2>Logs</h2>
    <p>
      The scaffold keeps a dedicated surface ready for correlation IDs, raw payload inspection, and
      callback history once persistence lands.
    </p>
  `,
})
export class LogsComponent {}
