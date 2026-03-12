import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { switchMap } from 'rxjs';
import { LabApiService, type LogRecord } from '../../core/services/lab-api.service';

@Component({
  selector: 'vl-logs',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <section class="hero">
      <div>
        <p class="eyebrow">Observability</p>
        <h2>Auth failures should not hide in the noise.</h2>
        <p class="copy">
          Category filters are wired to the persisted log stream so FCC and callback auth rejections
          can be isolated immediately.
        </p>
      </div>
      <div class="quick-actions">
        <button type="button" (click)="category.set('AuthFailure')">AuthFailure</button>
        <button type="button" (click)="category.set('CallbackAttempt')">CallbackAttempt</button>
        <button type="button" (click)="category.set('')">All Categories</button>
      </div>
    </section>

    <section class="filters">
      <label>
        <span>Category</span>
        <select [ngModel]="category()" (ngModelChange)="category.set($event)">
          <option value="">All</option>
          <option value="AuthFailure">AuthFailure</option>
          <option value="CallbackAttempt">CallbackAttempt</option>
          <option value="TransactionGenerated">TransactionGenerated</option>
        </select>
      </label>

      <label>
        <span>Site Code</span>
        <input [ngModel]="siteCode()" (ngModelChange)="siteCode.set($event.trim())" placeholder="VL-MW-BT001" />
      </label>

      <label>
        <span>Correlation ID</span>
        <input [ngModel]="correlationId()" (ngModelChange)="correlationId.set($event.trim())" placeholder="corr-default-flow" />
      </label>
    </section>

    <section class="results" *ngIf="logs() as entries">
      <article class="log-card" *ngFor="let entry of entries">
        <div class="log-card__header">
          <div>
            <span class="pill" [class.pill--warning]="entry.category === 'AuthFailure'">{{ entry.category }}</span>
            <strong>{{ entry.eventType }}</strong>
          </div>
          <time>{{ entry.occurredAtUtc | date: 'medium' }}</time>
        </div>

        <p class="message">{{ entry.message }}</p>

        <dl>
          <div>
            <dt>Severity</dt>
            <dd>{{ entry.severity }}</dd>
          </div>
          <div>
            <dt>Site</dt>
            <dd>{{ entry.siteCode ?? 'global' }}</dd>
          </div>
          <div>
            <dt>Correlation</dt>
            <dd>{{ entry.correlationId || 'n/a' }}</dd>
          </div>
        </dl>

        <pre>{{ entry.rawPayloadJson }}</pre>
      </article>

      <p class="empty" *ngIf="entries.length === 0">No log entries match the current filter.</p>
    </section>
  `,
  styles: `
    .hero,
    .filters,
    .results {
      display: grid;
      gap: 1rem;
    }

    .hero {
      grid-template-columns: 2fr minmax(240px, 1fr);
      margin-bottom: 1.5rem;
    }

    .eyebrow {
      color: var(--vl-accent);
      font-size: 0.8rem;
      letter-spacing: 0.16em;
      margin: 0 0 0.75rem;
      text-transform: uppercase;
    }

    h2,
    p {
      margin: 0;
    }

    .copy {
      color: var(--vl-muted);
      line-height: 1.6;
      margin-top: 0.75rem;
      max-width: 60ch;
    }

    .quick-actions,
    .filters {
      align-items: end;
      grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
    }

    .quick-actions button,
    select,
    input {
      background: var(--vl-panel-strong);
      border: 1px solid var(--vl-line);
      border-radius: 14px;
      color: inherit;
      font: inherit;
      padding: 0.8rem 1rem;
    }

    label {
      display: grid;
      gap: 0.45rem;
    }

    label span {
      color: var(--vl-muted);
      font-size: 0.9rem;
    }

    .log-card {
      background: var(--vl-panel-strong);
      border: 1px solid var(--vl-line);
      border-radius: 22px;
      padding: 1.25rem;
    }

    .log-card__header {
      align-items: center;
      display: flex;
      gap: 1rem;
      justify-content: space-between;
      margin-bottom: 0.75rem;
    }

    .log-card__header > div {
      align-items: center;
      display: flex;
      flex-wrap: wrap;
      gap: 0.75rem;
    }

    .pill {
      background: rgba(29, 122, 90, 0.16);
      border-radius: 999px;
      color: var(--vl-accent);
      font-size: 0.78rem;
      padding: 0.3rem 0.7rem;
      text-transform: uppercase;
    }

    .pill--warning {
      background: rgba(207, 95, 45, 0.16);
      color: #9a3412;
    }

    .message {
      line-height: 1.6;
      margin-bottom: 1rem;
    }

    dl {
      display: grid;
      gap: 0.75rem;
      grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
      margin: 0 0 1rem;
    }

    dt {
      color: var(--vl-muted);
      font-size: 0.85rem;
    }

    dd {
      margin: 0.15rem 0 0;
    }

    pre {
      background: rgba(15, 23, 42, 0.04);
      border-radius: 16px;
      margin: 0;
      overflow: auto;
      padding: 1rem;
      white-space: pre-wrap;
      word-break: break-word;
    }

    .empty {
      color: var(--vl-muted);
      padding: 1rem 0;
    }

    @media (max-width: 900px) {
      .hero {
        grid-template-columns: 1fr;
      }

      .log-card__header {
        align-items: start;
        flex-direction: column;
      }
    }
  `,
})
export class LogsComponent {
  private readonly api = inject(LabApiService);

  readonly category = signal('AuthFailure');
  readonly siteCode = signal('');
  readonly correlationId = signal('');
  readonly filters = computed(() => ({
    category: this.category(),
    siteCode: this.siteCode(),
    correlationId: this.correlationId(),
    limit: 100,
  }));
  readonly logs = toSignal(
    toObservable(this.filters).pipe(switchMap((filters) => this.api.getLogs(filters))),
    { initialValue: [] as LogRecord[] },
  );
}
