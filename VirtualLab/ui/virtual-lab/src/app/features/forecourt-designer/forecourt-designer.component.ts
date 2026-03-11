import { Component } from '@angular/core';

@Component({
  selector: 'vl-forecourt-designer',
  standalone: true,
  template: `
    <h2>Forecourt Designer</h2>
    <p>
      Reserved for pump, nozzle, and product layout editing. The scaffold exists so future work can
      wire the real backend state model instead of a client-only mock.
    </p>
  `,
})
export class ForecourtDesignerComponent {}
