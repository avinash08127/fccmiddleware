import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { catchError, firstValueFrom, merge, of, switchMap, tap, timer } from 'rxjs';
import {
  type PayloadContractValidationReport,
  type PushTransactionsResult,
  type SimulatedTransactionStatus,
  type SiteListItem,
  type TransactionDeliveryMode,
  type TransactionDetailRecord,
  type TransactionRecord,
  LabApiService,
} from '../../core/services/lab-api.service';
import { LiveUpdatesService } from '../../core/services/live-updates.service';

type InspectorTab = 'raw' | 'canonical' | 'timeline' | 'delivery' | 'metadata';

@Component({
  selector: 'vl-transactions',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <section class="hero">
      <div>
        <p class="eyebrow">VL-2.5 Transactions</p>
        <h2>Inspect delivery state, payload shape, and replay behavior from one surface.</h2>
        <p class="copy">
          The list stays live without a browser refresh, while payloads, callback attempts, and the
          assembled timeline load on demand for the selected transaction.
        </p>
      </div>

      <div class="hero-panel">
        <div class="hero-meta">
          <span class="pill">{{ liveUpdates.connectionState() }}</span>
          <span class="pill" [class.pill--warning]="!autoRefresh()">
            {{ autoRefresh() ? 'Auto refresh on' : 'Manual mode' }}
          </span>
        </div>

        <label class="check">
          <input
            type="checkbox"
            [ngModel]="autoRefresh()"
            (ngModelChange)="autoRefresh.set(!!$event); refreshNow()"
          />
          <span>Keep transaction list and detail live</span>
        </label>

        <button type="button" class="secondary" (click)="refreshNow()">Refresh now</button>
      </div>
    </section>

    <section class="filters">
      <label>
        <span>Site</span>
        <select [ngModel]="siteId()" (ngModelChange)="siteId.set($event)">
          <option value="">All sites</option>
          <option *ngFor="let site of sites()" [ngValue]="site.id">
            {{ site.siteCode }} · {{ site.deliveryMode }}
          </option>
        </select>
      </label>

      <label>
        <span>Delivery mode</span>
        <select
          [ngModel]="deliveryMode()"
          (ngModelChange)="setDeliveryMode($event)"
        >
          <option value="">All modes</option>
          <option *ngFor="let option of deliveryModes" [ngValue]="option">{{ option }}</option>
        </select>
      </label>

      <label>
        <span>Status</span>
        <select
          [ngModel]="status()"
          (ngModelChange)="setStatus($event)"
        >
          <option value="">All statuses</option>
          <option *ngFor="let option of statuses" [ngValue]="option">{{ option }}</option>
        </select>
      </label>

      <label>
        <span>Search</span>
        <input
          [ngModel]="search()"
          (ngModelChange)="search.set($event.trim())"
          placeholder="Transaction ID, correlation, product"
        />
      </label>

      <label>
        <span>Correlation ID</span>
        <input
          [ngModel]="correlationId()"
          (ngModelChange)="correlationId.set($event.trim())"
          placeholder="corr-default-flow"
        />
      </label>
    </section>

    <section *ngIf="error()" class="error-banner">{{ error() }}</section>

    <section class="workspace">
      <article class="panel list-panel">
        <header class="panel-header">
          <div>
            <h3>Transactions</h3>
            <p>{{ loading() ? 'Refreshing list…' : transactions().length + ' records loaded' }}</p>
          </div>
          <span class="pill">{{ transactions().length }}</span>
        </header>

        <div class="stack" *ngIf="transactions().length; else emptyList">
          <button
            type="button"
            class="transaction-row"
            *ngFor="let transaction of transactions()"
            [class.transaction-row--selected]="selectedTransactionId() === transaction.id"
            (click)="selectedTransactionId.set(transaction.id)"
          >
            <div class="row-main">
              <strong>{{ transaction.externalTransactionId }}</strong>
              <p>
                {{ transaction.siteCode }} · {{ transaction.productCode }} · Pump
                {{ transaction.pumpNumber }}/Nozzle {{ transaction.nozzleNumber }}
              </p>
            </div>

            <div class="row-meta">
              <span class="status-chip" [class.status-chip--warning]="transaction.status === 'Failed'">
                {{ transaction.status }}
              </span>
              <span
                class="pill"
                [class.pill--warning]="hasValidationProblems(transaction.contractValidation)"
                *ngIf="transaction.contractValidation.enabled"
              >
                {{ transaction.contractValidation.outcome }}
              </span>
              <small>{{ transaction.deliveryMode }} · {{ transaction.callbackAttemptCount }} callback attempts</small>
              <small>{{ formatDateTime(transaction.occurredAtUtc) }}</small>
            </div>
          </button>
        </div>
      </article>

      <article class="panel detail-panel" *ngIf="selectedTransaction() as transaction; else emptySelection">
        <header class="panel-header">
          <div>
            <h3>{{ transaction.externalTransactionId }}</h3>
            <p>
              {{ transaction.siteCode }} · {{ transaction.profileKey }} · {{ transaction.deliveryMode }}
            </p>
          </div>

          <div class="detail-status">
            <span class="status-chip" [class.status-chip--warning]="transaction.status === 'Failed'">
              {{ transaction.status }}
            </span>
            <span class="pill">{{ transaction.correlationId }}</span>
          </div>
        </header>

        <section *ngIf="detailError()" class="error-banner">{{ detailError() }}</section>

        <div class="summary-grid">
          <div>
            <span>Site</span>
            <strong>{{ transaction.siteName }}</strong>
          </div>
          <div>
            <span>Product</span>
            <strong>{{ transaction.productName }}</strong>
          </div>
          <div>
            <span>Volume</span>
            <strong>{{ transaction.volume | number: '1.2-2' }} L</strong>
          </div>
          <div>
            <span>Total</span>
            <strong>{{ transaction.totalAmount | number: '1.2-2' }} {{ transaction.currencyCode }}</strong>
          </div>
          <div>
            <span>Occurred</span>
            <strong>{{ formatDateTime(transaction.occurredAtUtc) }}</strong>
          </div>
          <div>
            <span>Delivered</span>
            <strong>{{ transaction.deliveredAtUtc ? formatDateTime(transaction.deliveredAtUtc) : 'Pending' }}</strong>
          </div>
          <div *ngIf="transaction.contractValidation.enabled">
            <span>Contract validation</span>
            <strong>{{ transaction.contractValidation.outcome }}</strong>
          </div>
        </div>

        <div class="action-grid">
          <label>
            <span>Replay correlation override</span>
            <input
              [ngModel]="replayCorrelationId()"
              (ngModelChange)="replayCorrelationId.set($event.trim())"
              placeholder="optional replay correlation"
            />
          </label>

          <label>
            <span>Callback target override</span>
            <input
              [ngModel]="targetKey()"
              (ngModelChange)="targetKey.set($event.trim())"
              placeholder="optional target key"
            />
          </label>

          <button type="button" (click)="replaySelected()" [disabled]="busyAction() !== null">
            {{ busyAction() === 'replay' ? 'Replaying…' : 'Replay transaction' }}
          </button>

          <button type="button" class="secondary" (click)="repushSelected()" [disabled]="busyAction() !== null">
            {{ busyAction() === 're-push' ? 'Re-pushing…' : 'Re-push delivery' }}
          </button>
        </div>

        <p *ngIf="actionMessage()" class="action-copy">{{ actionMessage() }}</p>

        <div class="tab-row">
          <button
            type="button"
            *ngFor="let tab of inspectorTabs"
            [class.tab--active]="activeTab() === tab"
            (click)="activeTab.set(tab)"
          >
            {{ tab }}
          </button>
        </div>

        <section *ngIf="activeTab() === 'raw'" class="inspector">
          <pre>{{ formatJson(transaction.rawPayloadJson) }}</pre>
        </section>

        <section *ngIf="activeTab() === 'canonical'" class="inspector">
          <pre>{{ formatJson(transaction.canonicalPayloadJson) }}</pre>

          <section class="validation-panel" *ngIf="transaction.contractValidation.enabled">
            <div class="timeline-head">
              <strong>Contract validation</strong>
              <span class="pill" [class.pill--warning]="hasValidationProblems(transaction.contractValidation)">
                {{ transaction.contractValidation.outcome }}
              </span>
            </div>
            <p class="action-copy">
              {{ transaction.contractValidation.errorCount }} errors ·
              {{ transaction.contractValidation.warningCount }} warnings ·
              {{ transaction.contractValidation.matchedCount }} mapped fields aligned
            </p>

            <div class="split-inspector">
              <div>
                <h4>Issues</h4>
                <div class="stack" *ngIf="transaction.contractValidation.issues.length; else noValidationIssues">
                  <article class="timeline-row" *ngFor="let issue of transaction.contractValidation.issues">
                    <div class="timeline-head">
                      <strong>{{ issue.payloadKind }} · {{ issue.code }}</strong>
                      <span class="pill" [class.pill--warning]="issue.severity !== 'Information'">{{ issue.severity }}</span>
                    </div>
                    <p>{{ issue.message }}</p>
                    <small>{{ issue.path }}</small>
                  </article>
                </div>
              </div>

              <div>
                <h4>Field comparisons</h4>
                <div class="stack" *ngIf="transaction.contractValidation.comparisons.length; else noValidationComparisons">
                  <article class="timeline-row" *ngFor="let comparison of transaction.contractValidation.comparisons">
                    <div class="timeline-head">
                      <strong>{{ comparison.sourceField }} → {{ comparison.targetField }}</strong>
                      <span class="pill" [class.pill--warning]="comparison.status !== 'Matched'">{{ comparison.status }}</span>
                    </div>
                    <p>{{ comparison.message }}</p>
                    <small>
                      raw={{ comparison.sourceValue || 'n/a' }} · canonical={{ comparison.targetValue || 'n/a' }}
                      <span *ngIf="comparison.transform"> · transform={{ comparison.transform }}</span>
                    </small>
                  </article>
                </div>
              </div>
            </div>
          </section>
        </section>

        <section *ngIf="activeTab() === 'metadata'" class="inspector split-inspector">
          <div>
            <h4>Request headers</h4>
            <pre>{{ formatJson(transaction.rawHeadersJson) }}</pre>
          </div>
          <div>
            <h4>Metadata</h4>
            <pre>{{ formatJson(transaction.metadataJson) }}</pre>
          </div>
        </section>

        <section *ngIf="activeTab() === 'timeline'" class="timeline">
          <article class="timeline-row" *ngFor="let entry of transaction.timeline">
            <div class="timeline-head">
              <strong>{{ entry.eventType }}</strong>
              <span class="pill">{{ entry.source }}</span>
            </div>
            <p>{{ entry.message }}</p>
            <div class="timeline-meta">
              <small>{{ entry.category }}</small>
              <small>{{ entry.state || entry.severity }}</small>
              <small>{{ formatDateTime(entry.occurredAtUtc) }}</small>
            </div>
            <pre *ngIf="entry.metadataJson && entry.metadataJson !== '{}'">{{ formatJson(entry.metadataJson) }}</pre>
          </article>
        </section>

        <section *ngIf="activeTab() === 'delivery'" class="delivery-panel">
          <article class="attempt-card" *ngFor="let attempt of transaction.callbackAttempts">
            <div class="timeline-head">
              <strong>{{ attempt.targetKey }} · attempt {{ attempt.attemptNumber }}</strong>
              <span class="status-chip" [class.status-chip--warning]="attempt.status === 'Failed'">
                {{ attempt.status }}
              </span>
            </div>
            <p>{{ attempt.requestUrl }}</p>
            <div class="timeline-meta">
              <small>Response {{ attempt.responseStatusCode || 'n/a' }}</small>
              <small>Retries {{ attempt.retryCount }}/{{ attempt.maxRetryCount }}</small>
              <small>{{ formatDateTime(attempt.attemptedAtUtc) }}</small>
            </div>
            <div class="split-inspector">
              <div>
                <h4>Request</h4>
                <pre>{{ formatJson(attempt.requestPayloadJson) }}</pre>
              </div>
              <div>
                <h4>Response</h4>
                <pre>{{ formatJson(attempt.responsePayloadJson) }}</pre>
              </div>
              <div>
                <h4>Headers</h4>
                <pre>{{ formatJson(attempt.requestHeadersJson) }}</pre>
              </div>
              <div>
                <h4>Response headers</h4>
                <pre>{{ formatJson(attempt.responseHeadersJson) }}</pre>
              </div>
            </div>
          </article>

          <p class="empty-state" *ngIf="transaction.callbackAttempts.length === 0">
            No callback attempts are linked to this transaction yet.
          </p>
        </section>
      </article>
    </section>

    <ng-template #emptyList>
      <section class="panel empty-panel">
        <h3>No transactions match the current filters</h3>
        <p>Adjust the search or wait for live activity to create new records.</p>
      </section>
    </ng-template>

    <ng-template #emptySelection>
      <section class="panel empty-panel">
        <h3>Select a transaction</h3>
        <p>Payloads, timeline, and delivery attempts appear here once a record is selected.</p>
      </section>
    </ng-template>

    <ng-template #noValidationIssues>
      <p class="empty-state">No required-field issues were detected for this transaction.</p>
    </ng-template>

    <ng-template #noValidationComparisons>
      <p class="empty-state">No field-mapping comparisons were configured for this transaction.</p>
    </ng-template>
  `,
  styles: `
    :host {
      display: grid;
      gap: 1rem;
    }

    .hero,
    .filters,
    .workspace,
    .summary-grid,
    .action-grid,
    .split-inspector {
      display: grid;
      gap: 1rem;
    }

    .hero {
      grid-template-columns: minmax(0, 1.8fr) minmax(280px, 1fr);
    }

    .panel,
    .hero-panel,
    .error-banner,
    .validation-panel {
      background: var(--vl-panel-strong);
      border: 1px solid var(--vl-line);
      border-radius: 22px;
      box-shadow: var(--vl-shadow);
      padding: 1.25rem;
    }

    .hero-panel,
    .list-panel,
    .detail-panel,
    .delivery-panel,
    .validation-panel {
      display: grid;
      gap: 1rem;
    }

    .eyebrow {
      color: var(--vl-accent);
      font-size: 0.8rem;
      letter-spacing: 0.16em;
      margin: 0 0 0.75rem;
      text-transform: uppercase;
    }

    h2,
    h3,
    h4,
    p {
      margin: 0;
    }

    .copy,
    .action-copy,
    .empty-state,
    .panel-header p,
    .transaction-row p,
    .attempt-card p {
      color: var(--vl-muted);
    }

    .hero-meta,
    .panel-header,
    .timeline-head,
    .detail-status {
      align-items: center;
      display: flex;
      gap: 0.75rem;
      justify-content: space-between;
    }

    .filters,
    .action-grid {
      grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
    }

    .workspace {
      grid-template-columns: minmax(320px, 0.95fr) minmax(0, 1.35fr);
      align-items: start;
    }

    .stack,
    .timeline {
      display: grid;
      gap: 0.75rem;
    }

    label {
      display: grid;
      gap: 0.45rem;
    }

    label span {
      color: var(--vl-muted);
      font-size: 0.9rem;
    }

    select,
    input,
    button {
      border-radius: 14px;
      font: inherit;
    }

    select,
    input {
      background: rgba(255, 255, 255, 0.9);
      border: 1px solid var(--vl-line);
      color: inherit;
      padding: 0.8rem 1rem;
    }

    button {
      background: var(--vl-accent);
      border: 1px solid transparent;
      color: #fff;
      cursor: pointer;
      padding: 0.8rem 1rem;
    }

    button.secondary,
    .tab-row button {
      background: transparent;
      border-color: var(--vl-line);
      color: inherit;
    }

    button:disabled {
      cursor: wait;
      opacity: 0.7;
    }

    .check {
      align-items: center;
      background: rgba(255, 255, 255, 0.55);
      border: 1px solid var(--vl-line);
      border-radius: 16px;
      display: flex;
      gap: 0.7rem;
      padding: 0.85rem 1rem;
    }

    .check input {
      margin: 0;
      padding: 0;
    }

    .pill,
    .status-chip {
      align-items: center;
      background: rgba(29, 122, 90, 0.12);
      border-radius: 999px;
      color: var(--vl-emerald);
      display: inline-flex;
      font-size: 0.8rem;
      font-weight: 600;
      padding: 0.3rem 0.7rem;
    }

    .pill--warning,
    .status-chip--warning {
      background: rgba(207, 95, 45, 0.16);
      color: var(--vl-accent);
    }

    .transaction-row,
    .timeline-row,
    .attempt-card {
      background: rgba(255, 255, 255, 0.54);
      border: 1px solid var(--vl-line);
      border-radius: 18px;
      color: inherit;
      display: grid;
      gap: 0.65rem;
      padding: 1rem;
      text-align: left;
    }

    .transaction-row--selected {
      border-color: rgba(207, 95, 45, 0.45);
      box-shadow: inset 0 0 0 1px rgba(207, 95, 45, 0.2);
    }

    .row-meta,
    .timeline-meta {
      display: flex;
      flex-wrap: wrap;
      gap: 0.75rem;
    }

    .summary-grid,
    .split-inspector {
      grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
    }

    .summary-grid div {
      background: rgba(29, 122, 90, 0.06);
      border-radius: 16px;
      padding: 0.9rem 1rem;
    }

    .summary-grid span {
      color: var(--vl-muted);
      display: block;
      margin-bottom: 0.2rem;
    }

    .tab-row {
      display: flex;
      flex-wrap: wrap;
      gap: 0.75rem;
    }

    .tab-row .tab--active {
      background: var(--vl-accent);
      border-color: transparent;
      color: #fff;
    }

    .inspector pre,
    .timeline pre,
    .split-inspector pre {
      background: rgba(15, 23, 42, 0.04);
      border-radius: 16px;
      margin: 0;
      max-height: 320px;
      overflow: auto;
      padding: 1rem;
      white-space: pre-wrap;
      word-break: break-word;
    }

    .error-banner {
      color: #8b1e1e;
    }

    .empty-panel {
      text-align: center;
    }

    @media (max-width: 1200px) {
      .hero,
      .workspace {
        grid-template-columns: 1fr;
      }
    }
  `,
})
export class TransactionsComponent {
  readonly deliveryModes: TransactionDeliveryMode[] = ['Push', 'Pull', 'Hybrid'];
  readonly statuses: SimulatedTransactionStatus[] = [
    'Created',
    'ReadyForDelivery',
    'Delivered',
    'Acknowledged',
    'Failed',
  ];
  readonly inspectorTabs: InspectorTab[] = ['raw', 'canonical', 'timeline', 'delivery', 'metadata'];
  readonly sites = signal<SiteListItem[]>([]);
  readonly transactions = signal<TransactionRecord[]>([]);
  readonly selectedTransaction = signal<TransactionDetailRecord | null>(null);
  readonly selectedTransactionId = signal('');
  readonly siteId = signal('');
  readonly search = signal('');
  readonly correlationId = signal('');
  readonly deliveryMode = signal<TransactionDeliveryMode | ''>('');
  readonly status = signal<SimulatedTransactionStatus | ''>('');
  readonly targetKey = signal('');
  readonly replayCorrelationId = signal('');
  readonly autoRefresh = signal(true);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly detailError = signal<string | null>(null);
  readonly busyAction = signal<'replay' | 're-push' | null>(null);
  readonly actionMessage = signal('');
  readonly activeTab = signal<InspectorTab>('timeline');
  readonly liveUpdates = inject(LiveUpdatesService);

  private readonly api = inject(LabApiService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly refreshNonce = signal(0);
  private readonly listQuery = computed(() => ({
    siteId: this.siteId(),
    search: this.search(),
    correlationId: this.correlationId(),
    deliveryMode: this.deliveryMode(),
    status: this.status(),
    autoRefresh: this.autoRefresh(),
    refreshNonce: this.refreshNonce(),
  }));
  private readonly detailQuery = computed(() => ({
    id: this.selectedTransactionId(),
    autoRefresh: this.autoRefresh(),
    refreshNonce: this.refreshNonce(),
  }));

  constructor() {
    void this.loadSites();

    toObservable(this.listQuery)
      .pipe(
        tap(() => {
          this.loading.set(true);
          this.error.set(null);
        }),
        switchMap(filters =>
          merge(
            of(null),
            filters.autoRefresh ? timer(4000, 4000) : of(null),
            filters.autoRefresh ? this.liveUpdates.events$ : of(null),
          ).pipe(
            switchMap(() =>
              this.api
                .getTransactions({
                  siteId: filters.siteId || undefined,
                  search: filters.search || undefined,
                  correlationId: filters.correlationId || undefined,
                  deliveryMode: filters.deliveryMode || undefined,
                  status: filters.status || undefined,
                  limit: 80,
                })
                .pipe(
                  catchError((error: unknown) => {
                    this.error.set(this.readError(error, 'Transactions could not be loaded.'));
                    return of([] as TransactionRecord[]);
                  }),
                ),
            ),
          ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(records => {
        this.transactions.set(records);
        this.ensureSelection(records);
        this.loading.set(false);
      });

    toObservable(this.detailQuery)
      .pipe(
        switchMap(selection => {
          if (!selection.id) {
            this.selectedTransaction.set(null);
            return of(null);
          }

          this.detailError.set(null);
          return merge(
            of(null),
            selection.autoRefresh ? timer(4000, 4000) : of(null),
            selection.autoRefresh ? this.liveUpdates.events$ : of(null),
          ).pipe(
            switchMap(() =>
              this.api.getTransaction(selection.id).pipe(
                catchError((error: unknown) => {
                  this.detailError.set(this.readError(error, 'Transaction detail could not be loaded.'));
                  return of(null);
                }),
              ),
            ),
          );
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(detail => {
        this.selectedTransaction.set(detail);
      });
  }

  refreshNow(): void {
    this.refreshNonce.update(value => value + 1);
  }

  async replaySelected(): Promise<void> {
    const transaction = this.selectedTransaction();
    if (!transaction) {
      return;
    }

    this.busyAction.set('replay');
    this.actionMessage.set('');
    this.detailError.set(null);

    try {
      const result = await firstValueFrom(
        this.api.replayTransaction(transaction.id, {
          correlationId: this.replayCorrelationId() || null,
        }),
      );

      this.actionMessage.set(result.message);
      this.selectedTransactionId.set(result.transactionId);
      this.activeTab.set('timeline');
      this.refreshNow();
    } catch (error) {
      this.detailError.set(this.readError(error, 'Transaction replay failed.'));
    } finally {
      this.busyAction.set(null);
    }
  }

  async repushSelected(): Promise<void> {
    const transaction = this.selectedTransaction();
    if (!transaction) {
      return;
    }

    this.busyAction.set('re-push');
    this.actionMessage.set('');
    this.detailError.set(null);

    try {
      const result = await firstValueFrom(
        this.api.repushTransaction(transaction.id, {
          targetKey: this.targetKey() || null,
        }),
      );

      this.actionMessage.set(this.describeRepushResult(result));
      this.activeTab.set('delivery');
      this.refreshNow();
    } catch (error) {
      this.detailError.set(this.readError(error, 'Transaction re-push failed.'));
    } finally {
      this.busyAction.set(null);
    }
  }

  formatDateTime(value: string | null): string {
    return value ? new Date(value).toLocaleString() : 'n/a';
  }

  formatJson(value: string | null): string {
    if (!value) {
      return '{}';
    }

    try {
      return JSON.stringify(JSON.parse(value), null, 2);
    } catch {
      return value;
    }
  }

  setDeliveryMode(value: string): void {
    this.deliveryMode.set((value as TransactionDeliveryMode | '') ?? '');
  }

  setStatus(value: string): void {
    this.status.set((value as SimulatedTransactionStatus | '') ?? '');
  }

  private async loadSites(): Promise<void> {
    try {
      const sites = await firstValueFrom(this.api.getSites(true));
      this.sites.set(sites.filter(site => site.isActive));
    } catch {
      this.error.set('Sites could not be loaded for the transactions filter.');
    }
  }

  private ensureSelection(records: TransactionRecord[]): void {
    if (records.length === 0) {
      this.selectedTransactionId.set('');
      return;
    }

    const selectedId = this.selectedTransactionId();
    if (!selectedId || !records.some(record => record.id === selectedId)) {
      this.selectedTransactionId.set(records[0].id);
    }
  }

  private describeRepushResult(result: PushTransactionsResult): string {
    if (result.attempts.length === 0) {
      return result.message;
    }

    const latest = result.attempts[0];
    return `${result.message} Latest attempt ${latest.attemptNumber} for ${latest.targetKey} is ${latest.status}.`;
  }

  private readError(error: unknown, fallback: string): string {
    if (error instanceof HttpErrorResponse) {
      const message = error.error?.message;
      return typeof message === 'string' && message.length > 0 ? message : fallback;
    }

    return fallback;
  }

  hasValidationProblems(report: PayloadContractValidationReport | null | undefined): boolean {
    return !!report && (report.errorCount > 0 || report.warningCount > 0 || report.mismatchCount > 0 || report.missingCount > 0);
  }
}
