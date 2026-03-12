import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { catchError, firstValueFrom, merge, of, switchMap, tap, timer } from 'rxjs';
import {
  type FccProfileSummary,
  type LogDetailRecord,
  type LogRecord,
  type SiteListItem,
  LabApiService,
} from '../../core/services/lab-api.service';
import { LiveUpdatesService } from '../../core/services/live-updates.service';

type LogInspectorTab = 'request' | 'response' | 'payloads' | 'metadata';

@Component({
  selector: 'vl-logs',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <section class="hero">
      <div>
        <p class="eyebrow">VL-2.5 Logs</p>
        <h2>Tail the simulator and inspect the transport context behind every event.</h2>
        <p class="copy">
          This view keeps the persisted log stream live, supports correlation-driven debugging, and
          opens a request/response inspector for the selected record.
        </p>
      </div>

      <div class="hero-panel">
        <div class="hero-meta">
          <span class="pill">{{ liveUpdates.connectionState() }}</span>
          <span class="pill" [class.pill--warning]="!liveTail()">
            {{ liveTail() ? 'Live tail on' : 'Live tail paused' }}
          </span>
        </div>

        <label class="check">
          <input
            type="checkbox"
            [ngModel]="liveTail()"
            (ngModelChange)="liveTail.set(!!$event); refreshNow()"
          />
          <span>Follow new log activity automatically</span>
        </label>

        <div class="action-row">
          <button type="button" class="secondary" (click)="refreshNow()">Refresh now</button>
          <button type="button" (click)="exportJson()">Export JSON</button>
        </div>
      </div>
    </section>

    <section class="filters">
      <label>
        <span>Site</span>
        <select [ngModel]="siteId()" (ngModelChange)="siteId.set($event)">
          <option value="">All sites</option>
          <option *ngFor="let site of sites()" [ngValue]="site.id">{{ site.siteCode }}</option>
        </select>
      </label>

      <label>
        <span>Profile</span>
        <select [ngModel]="profileId()" (ngModelChange)="profileId.set($event)">
          <option value="">All profiles</option>
          <option *ngFor="let profile of profiles()" [ngValue]="profile.id">
            {{ profile.profileKey }}
          </option>
        </select>
      </label>

      <label>
        <span>Severity</span>
        <select [ngModel]="severity()" (ngModelChange)="severity.set($event)">
          <option value="">All severities</option>
          <option *ngFor="let option of severities" [ngValue]="option">{{ option }}</option>
        </select>
      </label>

      <label>
        <span>Category</span>
        <select [ngModel]="category()" (ngModelChange)="category.set($event)">
          <option value="">All categories</option>
          <option *ngFor="let option of categories" [ngValue]="option">{{ option }}</option>
        </select>
      </label>

      <label>
        <span>Correlation ID</span>
        <input
          [ngModel]="correlationId()"
          (ngModelChange)="correlationId.set($event.trim())"
          placeholder="corr-default-flow"
        />
      </label>

      <label>
        <span>Search</span>
        <input
          [ngModel]="search()"
          (ngModelChange)="search.set($event.trim())"
          placeholder="Message or event type"
        />
      </label>
    </section>

    <section *ngIf="error()" class="error-banner">{{ error() }}</section>

    <section class="workspace">
      <article class="panel list-panel">
        <header class="panel-header">
          <div>
            <h3>Persisted log stream</h3>
            <p>{{ loading() ? 'Refreshing logs…' : logs().length + ' records loaded' }}</p>
          </div>
          <span class="pill">{{ logs().length }}</span>
        </header>

        <div class="stack" *ngIf="logs().length; else emptyList">
          <button
            type="button"
            class="log-row"
            *ngFor="let entry of logs()"
            [class.log-row--selected]="selectedLogId() === entry.id"
            (click)="selectedLogId.set(entry.id)"
          >
            <div class="row-main">
              <div class="row-head">
                <span class="status-chip" [class.status-chip--warning]="entry.severity !== 'Information'">
                  {{ entry.severity }}
                </span>
                <strong>{{ entry.category }} · {{ entry.eventType }}</strong>
              </div>
              <p>{{ entry.message }}</p>
            </div>

            <div class="row-meta">
              <small>{{ entry.siteCode || 'global' }}</small>
              <small>{{ entry.profileKey || 'no-profile' }}</small>
              <small>{{ entry.correlationId || 'n/a' }}</small>
              <small>{{ formatDateTime(entry.occurredAtUtc) }}</small>
            </div>
          </button>
        </div>
      </article>

      <article class="panel detail-panel" *ngIf="selectedLog() as entry; else emptySelection">
        <header class="panel-header">
          <div>
            <h3>{{ entry.category }} · {{ entry.eventType }}</h3>
            <p>{{ entry.siteCode || 'global' }} · {{ entry.profileKey || 'no-profile' }}</p>
          </div>
          <span class="pill">{{ formatDateTime(entry.occurredAtUtc) }}</span>
        </header>

        <section *ngIf="detailError()" class="error-banner">{{ detailError() }}</section>

        <div class="summary-grid">
          <div>
            <span>Severity</span>
            <strong>{{ entry.severity }}</strong>
          </div>
          <div>
            <span>Correlation</span>
            <strong>{{ entry.correlationId || 'n/a' }}</strong>
          </div>
          <div>
            <span>Transaction</span>
            <strong>{{ entry.externalTransactionId || entry.simulatedTransactionId || 'n/a' }}</strong>
          </div>
          <div>
            <span>Pre-auth</span>
            <strong>{{ entry.preAuthSessionId || 'n/a' }}</strong>
          </div>
        </div>

        <p class="message">{{ entry.message }}</p>

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

        <section *ngIf="activeTab() === 'request'" class="split-inspector">
          <div>
            <h4>Request headers</h4>
            <pre>{{ formatJson(entry.requestHeadersJson) }}</pre>
          </div>
          <div>
            <h4>Request payload</h4>
            <pre>{{ formatJson(entry.requestPayloadJson || entry.rawPayloadJson) }}</pre>
          </div>
        </section>

        <section *ngIf="activeTab() === 'response'" class="split-inspector">
          <div>
            <h4>Response headers</h4>
            <pre>{{ formatJson(entry.responseHeadersJson) }}</pre>
          </div>
          <div>
            <h4>Response payload</h4>
            <pre>{{ formatJson(entry.responsePayloadJson || entry.canonicalPayloadJson) }}</pre>
          </div>
        </section>

        <section *ngIf="activeTab() === 'payloads'" class="split-inspector">
          <div>
            <h4>Raw payload</h4>
            <pre>{{ formatJson(entry.rawPayloadJson) }}</pre>
          </div>
          <div>
            <h4>Canonical payload</h4>
            <pre>{{ formatJson(entry.canonicalPayloadJson) }}</pre>
          </div>
        </section>

        <section *ngIf="activeTab() === 'metadata'" class="split-inspector">
          <div>
            <h4>Metadata</h4>
            <pre>{{ formatJson(entry.metadataJson) }}</pre>
          </div>
          <div>
            <h4>Quick links</h4>
            <pre>{{
              formatJson(
                stringifyJson({
                  simulatedTransactionId: entry.simulatedTransactionId,
                  externalTransactionId: entry.externalTransactionId,
                  preAuthSessionId: entry.preAuthSessionId,
                  profileName: entry.profileName,
                })
              )
            }}</pre>
          </div>
        </section>
      </article>
    </section>

    <ng-template #emptyList>
      <section class="panel empty-panel">
        <h3>No logs match the current filters</h3>
        <p>Change the filters or resume live tail to follow new simulator activity.</p>
      </section>
    </ng-template>

    <ng-template #emptySelection>
      <section class="panel empty-panel">
        <h3>Select a log entry</h3>
        <p>Request, response, payload, and metadata inspectors appear here.</p>
      </section>
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
    .split-inspector {
      display: grid;
      gap: 1rem;
    }

    .hero {
      grid-template-columns: minmax(0, 1.8fr) minmax(280px, 1fr);
    }

    .panel,
    .hero-panel,
    .error-banner {
      background: var(--vl-panel-strong);
      border: 1px solid var(--vl-line);
      border-radius: 22px;
      box-shadow: var(--vl-shadow);
      padding: 1.25rem;
    }

    .hero-panel,
    .list-panel,
    .detail-panel {
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
    .panel-header p,
    .message,
    .empty-state,
    .log-row p {
      color: var(--vl-muted);
    }

    .hero-meta,
    .action-row,
    .panel-header,
    .row-head {
      align-items: center;
      display: flex;
      gap: 0.75rem;
      justify-content: space-between;
    }

    .filters {
      grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
    }

    .workspace {
      grid-template-columns: minmax(320px, 0.95fr) minmax(0, 1.35fr);
      align-items: start;
    }

    .stack {
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

    .log-row {
      background: rgba(255, 255, 255, 0.54);
      border: 1px solid var(--vl-line);
      border-radius: 18px;
      color: inherit;
      display: grid;
      gap: 0.65rem;
      padding: 1rem;
      text-align: left;
    }

    .log-row--selected {
      border-color: rgba(207, 95, 45, 0.45);
      box-shadow: inset 0 0 0 1px rgba(207, 95, 45, 0.2);
    }

    .row-meta {
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

    pre {
      background: rgba(15, 23, 42, 0.04);
      border-radius: 16px;
      margin: 0;
      max-height: 360px;
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
export class LogsComponent {
  readonly categories = [
    'AuthFailure',
    'CallbackAttempt',
    'CallbackFailure',
    'FccRequest',
    'FccResponse',
    'PreAuthSequence',
    'StateTransition',
    'TransactionGenerated',
    'TransactionPulled',
    'TransactionPushed',
  ];
  readonly severities = ['Information', 'Warning', 'Error'];
  readonly inspectorTabs: LogInspectorTab[] = ['request', 'response', 'payloads', 'metadata'];
  readonly sites = signal<SiteListItem[]>([]);
  readonly profiles = signal<FccProfileSummary[]>([]);
  readonly logs = signal<LogRecord[]>([]);
  readonly selectedLog = signal<LogDetailRecord | null>(null);
  readonly selectedLogId = signal('');
  readonly siteId = signal('');
  readonly profileId = signal('');
  readonly severity = signal('');
  readonly category = signal('');
  readonly correlationId = signal('');
  readonly search = signal('');
  readonly liveTail = signal(true);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly detailError = signal<string | null>(null);
  readonly activeTab = signal<LogInspectorTab>('request');
  readonly liveUpdates = inject(LiveUpdatesService);

  private readonly api = inject(LabApiService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly refreshNonce = signal(0);
  private readonly listQuery = computed(() => ({
    siteId: this.siteId(),
    profileId: this.profileId(),
    severity: this.severity(),
    category: this.category(),
    correlationId: this.correlationId(),
    search: this.search(),
    liveTail: this.liveTail(),
    refreshNonce: this.refreshNonce(),
  }));
  private readonly detailQuery = computed(() => ({
    id: this.selectedLogId(),
    liveTail: this.liveTail(),
    refreshNonce: this.refreshNonce(),
  }));

  constructor() {
    void this.loadFilters();

    toObservable(this.listQuery)
      .pipe(
        tap(() => {
          this.loading.set(true);
          this.error.set(null);
        }),
        switchMap(filters =>
          merge(
            of(null),
            filters.liveTail ? timer(3000, 3000) : of(null),
            filters.liveTail ? this.liveUpdates.events$ : of(null),
          ).pipe(
            switchMap(() =>
              this.api
                .getLogs({
                  siteId: filters.siteId || undefined,
                  profileId: filters.profileId || undefined,
                  severity: filters.severity || undefined,
                  category: filters.category || undefined,
                  correlationId: filters.correlationId || undefined,
                  search: filters.search || undefined,
                  limit: 120,
                })
                .pipe(
                  catchError((error: unknown) => {
                    this.error.set(this.readError(error, 'Logs could not be loaded.'));
                    return of([] as LogRecord[]);
                  }),
                ),
            ),
          ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(records => {
        this.logs.set(records);
        this.ensureSelection(records);
        this.loading.set(false);
      });

    toObservable(this.detailQuery)
      .pipe(
        switchMap(selection => {
          if (!selection.id) {
            this.selectedLog.set(null);
            return of(null);
          }

          this.detailError.set(null);
          return merge(
            of(null),
            selection.liveTail ? timer(3000, 3000) : of(null),
            selection.liveTail ? this.liveUpdates.events$ : of(null),
          ).pipe(
            switchMap(() =>
              this.api.getLog(selection.id).pipe(
                catchError((error: unknown) => {
                  this.detailError.set(this.readError(error, 'Log detail could not be loaded.'));
                  return of(null);
                }),
              ),
            ),
          );
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(detail => {
        this.selectedLog.set(detail);
      });
  }

  refreshNow(): void {
    this.refreshNonce.update(value => value + 1);
  }

  exportJson(): void {
    const payload = {
      exportedAtUtc: new Date().toISOString(),
      filters: {
        siteId: this.siteId() || null,
        profileId: this.profileId() || null,
        severity: this.severity() || null,
        category: this.category() || null,
        correlationId: this.correlationId() || null,
        search: this.search() || null,
      },
      logs: this.logs(),
      selected: this.selectedLog(),
    };

    const blob = new Blob([JSON.stringify(payload, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = `virtual-lab-logs-${Date.now()}.json`;
    anchor.click();
    URL.revokeObjectURL(url);
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

  stringifyJson(value: unknown): string {
    return JSON.stringify(value);
  }

  private async loadFilters(): Promise<void> {
    try {
      const [sites, profiles] = await Promise.all([
        firstValueFrom(this.api.getSites(true)),
        firstValueFrom(this.api.getProfiles()),
      ]);

      this.sites.set(sites.filter(site => site.isActive));
      this.profiles.set(profiles.filter(profile => profile.isActive));
    } catch {
      this.error.set('Log filters could not be initialized from sites and profiles.');
    }
  }

  private ensureSelection(records: LogRecord[]): void {
    if (records.length === 0) {
      this.selectedLogId.set('');
      return;
    }

    const selectedId = this.selectedLogId();
    if (!selectedId || !records.some(record => record.id === selectedId)) {
      this.selectedLogId.set(records[0].id);
    }
  }

  private readError(error: unknown, fallback: string): string {
    if (error instanceof HttpErrorResponse) {
      const message = error.error?.message;
      return typeof message === 'string' && message.length > 0 ? message : fallback;
    }

    return fallback;
  }
}
