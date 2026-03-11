import { Component } from '@angular/core';

@Component({
  selector: 'vl-transactions',
  standalone: true,
  template: `
    <h2>Transactions</h2>
    <p>
      Transaction pull latency, delivery-mode mixes, and deterministic replay checks are anchored to
      the benchmark seed profile defined in Phase 0.
    </p>
  `,
})
export class TransactionsComponent {}
