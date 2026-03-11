import { Component } from '@angular/core';

@Component({
  selector: 'vl-preauth-console',
  standalone: true,
  template: `
    <h2>Pre-Auth Console</h2>
    <p>
      Phase 0 keeps both required modes visible: <code>CREATE_ONLY</code> and
      <code>CREATE_THEN_AUTHORIZE</code>.
    </p>
  `,
})
export class PreauthConsoleComponent {}
