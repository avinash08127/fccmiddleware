import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { filter, firstValueFrom } from 'rxjs';
import {
  type ForecourtPumpView,
  type LabPreAuthActionRequest,
  type LogRecord,
  type PayloadContractValidationReport,
  type PreAuthSessionRecord,
  type SiteForecourtView,
  type SiteListItem,
  type TransactionRecord,
  LabApiService,
} from '../../core/services/lab-api.service';
import { LiveUpdatesService, type LabLiveEvent } from '../../core/services/live-updates.service';

interface TimelineEntry {
  occurredAtUtc: string;
  operation: string;
  eventType: string;
  message: string;
  fromStatus: string | null;
  toStatus: string | null;
  metadata: unknown;
}

@Component({
  selector: 'vl-preauth-console',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <section class="hero">
      <div>
        <p class="eyebrow">VL-2.4 Pre-Auth Console</p>
        <h2>Drive create-only and create-then-authorize flows from the lab UI.</h2>
        <p class="copy">
          The console emits real pre-auth requests into the simulation layer, keeps customer tax
          inputs attached to the request payload, and renders the persisted sequence timeline beside
          linked dispense activity.
        </p>
      </div>

      <div class="hero-panel">
        <label>
          <span>Site</span>
          <select [ngModel]="selectedSiteId()" (ngModelChange)="changeSite($event)">
            <option *ngFor="let site of sites()" [ngValue]="site.id">
              {{ site.siteCode }} · {{ site.preAuthMode }} · {{ site.activeProfile.name }}
            </option>
          </select>
        </label>

        <div class="hero-meta">
          <span class="pill">{{ liveUpdates.connectionState() }}</span>
          <span class="pill">{{ selectedSite()?.preAuthMode ?? 'No mode' }}</span>
        </div>

        <p class="meta-copy" *ngIf="selectedSite() as site">
          {{ site.siteCode }} · {{ site.deliveryMode }} delivery · {{ sessions().length }} sessions
          loaded
        </p>
      </div>
    </section>

    <section *ngIf="error()" class="error-banner">{{ error() }}</section>

    <section class="workspace" *ngIf="selectedSite() as site; else emptyState">
      <article class="panel form-panel">
        <header class="panel-header">
          <div>
            <h3>Operator controls</h3>
            <p>Use a site/profile combination that matches the flow you want to simulate.</p>
          </div>
          <button type="button" class="secondary" (click)="refreshSelectedSite()" [disabled]="loading()">
            {{ loading() ? 'Refreshing…' : 'Refresh' }}
          </button>
        </header>

        <div class="mode-banner" [class.mode-banner--alt]="site.preAuthMode === 'CreateThenAuthorize'">
          <strong>{{ site.preAuthMode === 'CreateOnly' ? 'Create-only profile' : 'Create then authorize profile' }}</strong>
          <p>
            {{
              site.preAuthMode === 'CreateOnly'
                ? 'Create requests immediately move the session to AUTHORIZED.'
                : 'Create leaves the session pending until an authorize action is sent.'
            }}
          </p>
        </div>

        <div class="form-grid">
          <label>
            <span>Correlation ID</span>
            <input [ngModel]="correlationId()" (ngModelChange)="correlationId.set($event.trim())" />
          </label>

          <label>
            <span>Amount</span>
            <input
              type="number"
              min="1"
              step="1"
              [ngModel]="amount()"
              (ngModelChange)="amount.set(numberOrNull($event) ?? 15000)"
            />
          </label>

          <label>
            <span>Expires in seconds</span>
            <input
              type="number"
              min="0"
              step="1"
              [ngModel]="expiresInSeconds()"
              (ngModelChange)="expiresInSeconds.set(numberOrNull($event))"
            />
          </label>

          <label>
            <span>Pump</span>
            <select [ngModel]="selectedPumpId()" (ngModelChange)="changePump($event)">
              <option *ngFor="let pump of forecourtPumps()" [ngValue]="pump.id">
                Pump {{ pump.pumpNumber }} · {{ pump.label }}
              </option>
            </select>
          </label>

          <label>
            <span>Nozzle</span>
            <select [ngModel]="selectedNozzleId()" (ngModelChange)="selectedNozzleId.set($event)">
              <option *ngFor="let nozzle of selectedPumpNozzles()" [ngValue]="nozzle.id">
                N{{ nozzle.nozzleNumber }} · {{ nozzle.label }} · {{ nozzle.productCode }}
              </option>
            </select>
          </label>

          <label>
            <span>Selected session</span>
            <select [ngModel]="selectedSessionId()" (ngModelChange)="selectedSessionId.set($event)">
              <option [ngValue]="''">None</option>
              <option *ngFor="let session of sessions()" [ngValue]="session.id">
                {{ session.externalReference }} · {{ session.status }}
              </option>
            </select>
          </label>
        </div>

        <div class="form-grid">
          <label>
            <span>Customer name</span>
            <input
              [ngModel]="customerName()"
              (ngModelChange)="customerName.set($event)"
              placeholder="Example Logistics"
            />
          </label>

          <label>
            <span>Customer tax ID</span>
            <input
              [ngModel]="customerTaxId()"
              (ngModelChange)="customerTaxId.set($event)"
              placeholder="TIN-77831"
            />
          </label>

          <label>
            <span>Tax office / region</span>
            <input
              [ngModel]="customerTaxOffice()"
              (ngModelChange)="customerTaxOffice.set($event)"
              placeholder="Lagos-West"
            />
          </label>
        </div>

        <div class="toggle-grid">
          <label class="check">
            <input
              type="checkbox"
              [ngModel]="simulateFailure()"
              (ngModelChange)="simulateFailure.set(!!$event)"
            />
            <span>Inject failure</span>
          </label>

          <label>
            <span>Failure status</span>
            <input
              type="number"
              min="400"
              step="1"
              [ngModel]="failureStatusCode()"
              (ngModelChange)="failureStatusCode.set(numberOrNull($event))"
            />
          </label>

          <label>
            <span>Failure code</span>
            <input
              [ngModel]="failureCode()"
              (ngModelChange)="failureCode.set($event)"
              placeholder="SIMULATED_FAILURE"
            />
          </label>

          <label>
            <span>Failure message</span>
            <input
              [ngModel]="failureMessage()"
              (ngModelChange)="failureMessage.set($event)"
              placeholder="forced failure"
            />
          </label>
        </div>

        <div class="action-row">
          <button type="button" (click)="createOnlyFlow()" [disabled]="busyAction() !== null || !canCreateOnly()">
            Create only
          </button>
          <button
            type="button"
            (click)="createThenAuthorizeFlow()"
            [disabled]="busyAction() !== null || !canCreateThenAuthorize()"
          >
            Create + authorize
          </button>
          <button type="button" class="secondary" (click)="authorizeSelected()" [disabled]="busyAction() !== null">
            Authorize selected
          </button>
          <button type="button" class="secondary" (click)="cancelSelected()" [disabled]="busyAction() !== null">
            Cancel selected
          </button>
          <button type="button" class="danger" (click)="expireSelected()" [disabled]="busyAction() !== null">
            Expire selected
          </button>
        </div>

        <p class="action-copy" *ngIf="busyAction()">Running {{ busyAction() }}…</p>
        <p class="action-copy" *ngIf="lastActionMessage()">{{ lastActionMessage() }}</p>
      </article>

      <article class="panel session-panel">
        <header class="panel-header">
          <div>
            <h3>Session list</h3>
            <p>Latest pre-auth sessions for the selected site.</p>
          </div>
          <span class="pill">{{ sessions().length }}</span>
        </header>

        <div class="stack" *ngIf="sessions().length; else emptySessions">
          <button
            *ngFor="let session of sessions().slice(0, 10)"
            type="button"
            class="session-card"
            [class.session-card--selected]="selectedSessionId() === session.id"
            [class.session-card--terminal]="isTerminalStatus(session.status)"
            (click)="selectedSessionId.set(session.id)"
          >
            <div>
              <strong>{{ session.externalReference }}</strong>
              <p>{{ session.mode }} · {{ session.correlationId }}</p>
            </div>
            <div class="row-meta">
              <span>{{ session.status }}</span>
              <span class="pill" [class.pill--warning]="sessionHasValidationProblems(session)">
                {{ sessionValidationOutcome(session) }}
              </span>
              <small>{{ session.reservedAmount | number: '1.0-0' }}</small>
            </div>
          </button>
        </div>
      </article>
    </section>

    <section class="details-grid" *ngIf="selectedSession() as session">
      <article class="panel">
        <header class="panel-header">
          <div>
            <h3>Sequence timeline</h3>
            <p>Persisted transitions and sequence decisions for {{ session.externalReference }}.</p>
          </div>
          <span class="pill">{{ timelineEntries().length }}</span>
        </header>

        <div class="timeline" *ngIf="timelineEntries().length; else emptyTimeline">
          <div class="timeline-entry" *ngFor="let entry of timelineEntries()">
            <div class="timeline-marker"></div>
            <div>
              <strong>{{ entry.eventType }}</strong>
              <p>{{ entry.message }}</p>
              <small>
                {{ formatDateTime(entry.occurredAtUtc) }} · {{ entry.operation }}
                <span *ngIf="entry.toStatus"> · {{ entry.fromStatus || 'n/a' }} → {{ entry.toStatus }}</span>
              </small>
            </div>
          </div>
        </div>
      </article>

      <article class="panel">
        <header class="panel-header">
          <div>
            <h3>Linked transactions</h3>
            <p>Dispense results that share the same correlation ID.</p>
          </div>
          <span class="pill">{{ linkedTransactions().length }}</span>
        </header>

        <div class="stack" *ngIf="linkedTransactions().length; else emptyLinkedTransactions">
          <div class="row-card" *ngFor="let transaction of linkedTransactions()">
            <div>
              <strong>{{ transaction.externalTransactionId }}</strong>
              <p>
                {{ transaction.productCode }} · Pump {{ transaction.pumpNumber }}/Nozzle
                {{ transaction.nozzleNumber }}
              </p>
            </div>
            <div class="row-meta">
              <span>{{ transaction.status }}</span>
              <small>{{ formatDateTime(transaction.occurredAtUtc) }}</small>
            </div>
          </div>
        </div>
      </article>

      <article class="panel">
        <header class="panel-header">
          <div>
            <h3>Payload inspectors</h3>
            <p>Keep raw and canonical request/response payloads visible for debugging.</p>
          </div>
          <span class="pill">{{ session.status }}</span>
        </header>

        <div class="inspector-grid">
          <div>
            <strong>Raw request</strong>
            <pre>{{ formatJson(session.rawRequestJson) }}</pre>
          </div>
          <div>
            <strong>Canonical request</strong>
            <pre>{{ formatJson(session.canonicalRequestJson) }}</pre>
          </div>
          <div>
            <strong>Raw response</strong>
            <pre>{{ formatJson(session.rawResponseJson) }}</pre>
          </div>
          <div>
            <strong>Canonical response</strong>
            <pre>{{ formatJson(session.canonicalResponseJson) }}</pre>
          </div>
        </div>
      </article>

      <article class="panel">
        <header class="panel-header">
          <div>
            <h3>Contract diagnostics</h3>
            <p>Missing fields and mapping mismatches stay visible without blocking the flow.</p>
          </div>
          <span class="pill" [class.pill--warning]="sessionHasValidationProblems(session)">
            {{ sessionValidationOutcome(session) }}
          </span>
        </header>

        <div class="inspector-grid">
          <div>
            <strong>Request validation</strong>
            <p class="empty-state" *ngIf="!session.requestValidation.enabled">No request validation configured.</p>
            <div class="stack" *ngIf="session.requestValidation.enabled">
              <span class="pill" [class.pill--warning]="hasValidationProblems(session.requestValidation)">
                {{ session.requestValidation.outcome }}
              </span>
              <small class="empty-state">
                {{ session.requestValidation.errorCount }} errors ·
                {{ session.requestValidation.warningCount }} warnings
              </small>
              <article class="row-card" *ngFor="let issue of session.requestValidation.issues">
                <div>
                  <strong>{{ issue.code }}</strong>
                  <p>{{ issue.message }}</p>
                </div>
                <div class="row-meta">
                  <span>{{ issue.payloadKind }}</span>
                  <small>{{ issue.path }}</small>
                </div>
              </article>
              <article class="row-card" *ngFor="let comparison of session.requestValidation.comparisons">
                <div>
                  <strong>{{ comparison.sourceField }} → {{ comparison.targetField }}</strong>
                  <p>{{ comparison.message }}</p>
                </div>
                <div class="row-meta">
                  <span>{{ comparison.status }}</span>
                  <small>{{ comparison.transform || 'identity' }}</small>
                </div>
              </article>
            </div>
          </div>

          <div>
            <strong>Response validation</strong>
            <p class="empty-state" *ngIf="!session.responseValidation.enabled">No response validation configured.</p>
            <div class="stack" *ngIf="session.responseValidation.enabled">
              <span class="pill" [class.pill--warning]="hasValidationProblems(session.responseValidation)">
                {{ session.responseValidation.outcome }}
              </span>
              <small class="empty-state">
                {{ session.responseValidation.errorCount }} errors ·
                {{ session.responseValidation.warningCount }} warnings
              </small>
              <article class="row-card" *ngFor="let issue of session.responseValidation.issues">
                <div>
                  <strong>{{ issue.code }}</strong>
                  <p>{{ issue.message }}</p>
                </div>
                <div class="row-meta">
                  <span>{{ issue.payloadKind }}</span>
                  <small>{{ issue.path }}</small>
                </div>
              </article>
              <article class="row-card" *ngFor="let comparison of session.responseValidation.comparisons">
                <div>
                  <strong>{{ comparison.sourceField }} → {{ comparison.targetField }}</strong>
                  <p>{{ comparison.message }}</p>
                </div>
                <div class="row-meta">
                  <span>{{ comparison.status }}</span>
                  <small>{{ comparison.transform || 'identity' }}</small>
                </div>
              </article>
            </div>
          </div>
        </div>
      </article>
    </section>

    <section class="details-grid" *ngIf="selectedSite()">
      <article class="panel">
        <header class="panel-header">
          <div>
            <h3>Pre-auth logs</h3>
            <p>Sequence, state-transition, and FCC request/response records.</p>
          </div>
          <span class="pill">{{ preAuthLogs().length }}</span>
        </header>

        <div class="stack" *ngIf="preAuthLogs().length; else emptyLogs">
          <div class="row-card" *ngFor="let entry of preAuthLogs().slice(0, 8)">
            <div>
              <strong>{{ entry.category }} · {{ entry.eventType }}</strong>
              <p>{{ entry.message }}</p>
            </div>
            <div class="row-meta">
              <span>{{ entry.correlationId || 'n/a' }}</span>
              <small>{{ formatDateTime(entry.occurredAtUtc) }}</small>
            </div>
          </div>
        </div>
      </article>

      <article class="panel">
        <header class="panel-header">
          <div>
            <h3>Live event feed</h3>
            <p>SignalR updates for pre-auth and related forecourt actions.</p>
          </div>
          <span class="pill">{{ liveFeed().length }}</span>
        </header>

        <div class="stack" *ngIf="liveFeed().length; else emptyFeed">
          <div class="row-card" *ngFor="let event of liveFeed().slice(0, 10)">
            <div>
              <strong>{{ event.eventType }} · {{ liveAction(event) }}</strong>
              <p>{{ liveMessage(event) }}</p>
            </div>
            <div class="row-meta">
              <span>{{ event.correlationId || 'n/a' }}</span>
              <small>{{ formatDateTime(event.occurredAtUtc || '') }}</small>
            </div>
          </div>
        </div>
      </article>
    </section>

    <ng-template #emptySessions>
      <p class="empty-state">No pre-auth sessions exist yet for this site.</p>
    </ng-template>

    <ng-template #emptyTimeline>
      <p class="empty-state">Timeline entries will appear after the first pre-auth action.</p>
    </ng-template>

    <ng-template #emptyLinkedTransactions>
      <p class="empty-state">No linked transactions have been generated for this session yet.</p>
    </ng-template>

    <ng-template #emptyLogs>
      <p class="empty-state">No pre-auth log entries captured yet for this site.</p>
    </ng-template>

    <ng-template #emptyFeed>
      <p class="empty-state">No live updates received yet for this site.</p>
    </ng-template>

    <ng-template #emptyState>
      <section class="panel empty-panel">
        <h3>No active site available</h3>
        <p>Select or seed a site before using the pre-auth console.</p>
      </section>
    </ng-template>
  `,
  styles: `
    :host {
      display: grid;
      gap: 1rem;
    }

    .hero,
    .workspace,
    .details-grid {
      display: grid;
      gap: 1rem;
    }

    .hero {
      grid-template-columns: minmax(0, 1.7fr) minmax(320px, 1fr);
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
    .panel {
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
    p {
      margin: 0;
    }

    .copy,
    .meta-copy,
    .panel-header p,
    .empty-state,
    .action-copy,
    .mode-banner p,
    .timeline-entry small,
    .row-card p {
      color: var(--vl-muted);
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
      background: rgba(255, 255, 255, 0.88);
      border: 1px solid var(--vl-line);
      color: inherit;
      padding: 0.8rem 1rem;
    }

    button {
      background: var(--vl-accent);
      border: 1px solid transparent;
      color: #fff;
      cursor: pointer;
      padding: 0.85rem 1rem;
    }

    button.secondary {
      background: transparent;
      border-color: var(--vl-line);
      color: inherit;
    }

    button.danger {
      background: #8b1e1e;
    }

    button:disabled {
      cursor: not-allowed;
      opacity: 0.6;
    }

    .hero-meta,
    .panel-header,
    .row-card,
    .action-row {
      display: flex;
      gap: 0.75rem;
      justify-content: space-between;
    }

    .pill {
      align-items: center;
      background: rgba(29, 122, 90, 0.12);
      border-radius: 999px;
      color: var(--vl-emerald);
      display: inline-flex;
      font-size: 0.8rem;
      font-weight: 600;
      padding: 0.28rem 0.7rem;
    }

    .workspace {
      grid-template-columns: minmax(0, 1.25fr) minmax(320px, 0.85fr);
    }

    .form-grid,
    .toggle-grid,
    .details-grid,
    .inspector-grid {
      display: grid;
      gap: 0.75rem;
      grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
    }

    .toggle-grid {
      align-items: end;
    }

    .check {
      align-items: center;
      background: rgba(255, 255, 255, 0.58);
      border: 1px solid var(--vl-line);
      border-radius: 16px;
      display: flex;
      gap: 0.7rem;
      padding: 0.8rem 0.95rem;
    }

    .check input {
      margin: 0;
      padding: 0;
    }

    .mode-banner {
      background: rgba(29, 122, 90, 0.08);
      border: 1px solid rgba(29, 122, 90, 0.14);
      border-radius: 18px;
      padding: 0.95rem 1rem;
    }

    .mode-banner--alt {
      background: rgba(207, 95, 45, 0.1);
      border-color: rgba(207, 95, 45, 0.16);
    }

    .stack,
    .timeline {
      display: grid;
      gap: 0.75rem;
    }

    .session-card,
    .row-card {
      align-items: start;
      background: rgba(255, 255, 255, 0.58);
      border: 1px solid var(--vl-line);
      border-radius: 16px;
      color: inherit;
      padding: 0.95rem 1rem;
      text-align: left;
    }

    .session-card {
      display: flex;
      justify-content: space-between;
    }

    .session-card--selected {
      border-color: rgba(207, 95, 45, 0.4);
      box-shadow: inset 0 0 0 1px rgba(207, 95, 45, 0.24);
    }

    .session-card--terminal {
      background: rgba(139, 30, 30, 0.06);
    }

    .row-meta {
      display: grid;
      gap: 0.25rem;
      justify-items: end;
      text-align: right;
    }

    .timeline-entry {
      display: grid;
      gap: 0.85rem;
      grid-template-columns: 16px minmax(0, 1fr);
    }

    .timeline-marker {
      background: linear-gradient(180deg, var(--vl-accent), rgba(255, 194, 123, 0.4));
      border-radius: 999px;
      min-height: 100%;
      width: 8px;
    }

    .inspector-grid pre {
      background: rgba(15, 23, 42, 0.04);
      border-radius: 16px;
      margin: 0.5rem 0 0;
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
      .workspace,
      .details-grid {
        grid-template-columns: 1fr;
      }
    }

    @media (max-width: 720px) {
      .action-row,
      .panel-header,
      .row-card,
      .session-card {
        flex-direction: column;
      }

      .row-meta {
        justify-items: start;
        text-align: left;
      }
    }
  `,
})
export class PreauthConsoleComponent {
  readonly sites = signal<SiteListItem[]>([]);
  readonly selectedSiteId = signal<string>('');
  readonly forecourt = signal<SiteForecourtView | null>(null);
  readonly sessions = signal<PreAuthSessionRecord[]>([]);
  readonly transactions = signal<TransactionRecord[]>([]);
  readonly logs = signal<LogRecord[]>([]);
  readonly liveFeed = signal<LabLiveEvent[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly busyAction = signal<string | null>(null);
  readonly lastActionMessage = signal('');
  readonly selectedSessionId = signal('');
  readonly selectedPumpId = signal<string | null>(null);
  readonly selectedNozzleId = signal<string | null>(null);
  readonly correlationId = signal(`preauth-${Date.now()}`);
  readonly amount = signal(15000);
  readonly expiresInSeconds = signal<number | null>(300);
  readonly customerName = signal('Example Logistics');
  readonly customerTaxId = signal('TIN-77831');
  readonly customerTaxOffice = signal('Lagos-West');
  readonly simulateFailure = signal(false);
  readonly failureStatusCode = signal<number | null>(503);
  readonly failureCode = signal('SIMULATED_FAILURE');
  readonly failureMessage = signal('forced failure');
  readonly selectedSite = computed(
    () => this.sites().find((site) => site.id === this.selectedSiteId()) ?? null,
  );
  readonly canCreateOnly = computed(() => this.selectedSite()?.preAuthMode === 'CreateOnly');
  readonly canCreateThenAuthorize = computed(
    () => this.selectedSite()?.preAuthMode === 'CreateThenAuthorize',
  );
  readonly forecourtPumps = computed(() => this.forecourt()?.pumps ?? []);
  readonly selectedPump = computed<ForecourtPumpView | null>(() => {
    const pumpId = this.selectedPumpId();
    return this.forecourtPumps().find((pump) => pump.id === pumpId) ?? null;
  });
  readonly selectedPumpNozzles = computed(() => this.selectedPump()?.nozzles ?? []);
  readonly selectedSession = computed(
    () => this.sessions().find((session) => session.id === this.selectedSessionId()) ?? null,
  );
  readonly timelineEntries = computed(() => this.parseTimeline(this.selectedSession()?.timelineJson));
  readonly linkedTransactions = computed(() => {
    const correlationId = this.selectedSession()?.correlationId;
    return correlationId
      ? this.transactions().filter((transaction) => transaction.correlationId === correlationId)
      : [];
  });
  readonly preAuthLogs = computed(() =>
    this.logs().filter((entry) =>
      entry.category === 'PreAuthSequence' ||
      entry.category === 'StateTransition' ||
      entry.category === 'FccRequest' ||
      entry.category === 'FccResponse',
    ),
  );
  readonly liveUpdates = inject(LiveUpdatesService);

  private readonly api = inject(LabApiService);
  private readonly destroyRef = inject(DestroyRef);

  constructor() {
    void this.loadSites();

    this.liveUpdates.events$
      .pipe(
        filter((event) => this.isRelevantEvent(event)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((event) => {
        this.liveFeed.update((entries) => [event, ...entries].slice(0, 20));
        void this.refreshSelectedSite();
      });
  }

  async changeSite(siteId: string): Promise<void> {
    this.selectedSiteId.set(siteId);
    this.selectedSessionId.set('');
    this.liveFeed.set([]);
    this.lastActionMessage.set('');
    await this.refreshSelectedSite();
  }

  changePump(pumpId: string): void {
    this.selectedPumpId.set(pumpId);
    this.selectedNozzleId.set(this.selectedPumpNozzles()[0]?.id ?? null);
  }

  async refreshSelectedSite(): Promise<void> {
    const siteId = this.selectedSiteId();
    const site = this.selectedSite();
    if (!siteId || !site) {
      return;
    }

    this.loading.set(true);

    try {
      const [forecourt, sessions, transactions, logs] = await Promise.all([
        firstValueFrom(this.api.getForecourt(siteId)),
        firstValueFrom(this.api.getPreAuthSessions({ siteCode: site.siteCode, limit: 30 })),
        firstValueFrom(this.api.getTransactions({ siteCode: site.siteCode, limit: 20 })),
        firstValueFrom(this.api.getLogs({ siteCode: site.siteCode, limit: 50 })),
      ]);

      this.forecourt.set(forecourt);
      this.sessions.set(sessions);
      this.transactions.set(transactions);
      this.logs.set(logs);
      this.ensurePumpAndNozzleSelection(forecourt);
      this.ensureSessionSelection(sessions);
      this.error.set(null);
    } catch {
      this.error.set('Pre-auth console data could not be loaded. Check the API and SignalR hub.');
    } finally {
      this.loading.set(false);
    }
  }

  async createOnlyFlow(): Promise<void> {
    if (!this.canCreateOnly()) {
      this.lastActionMessage.set('Select a CREATE_ONLY site to run the create-only flow.');
      return;
    }

    await this.runPreAuthAction('create-only', () =>
      this.executeAction({
        action: 'create',
        ...this.buildCreatePayload(),
      }),
    );
  }

  async createThenAuthorizeFlow(): Promise<void> {
    if (!this.canCreateThenAuthorize()) {
      this.lastActionMessage.set(
        'Select a CREATE_THEN_AUTHORIZE site to run the create-then-authorize flow.',
      );
      return;
    }

    await this.runPreAuthAction('create-then-authorize', async () => {
      const createResult = await this.executeAction({
        action: 'create',
        ...this.buildCreatePayload(),
      });

      const preAuthId = createResult.session?.externalReference;
      if (!preAuthId) {
        return createResult;
      }

      return this.executeAction({
        action: 'authorize',
        preAuthId,
        correlationId: createResult.correlationId,
        amount: this.amount(),
      });
    });
  }

  async authorizeSelected(): Promise<void> {
    const session = this.selectedSession();
    if (!session) {
      this.lastActionMessage.set('Select a pre-auth session before authorizing.');
      return;
    }

    await this.runPreAuthAction('authorize', () =>
      this.executeAction({
        action: 'authorize',
        preAuthId: session.externalReference,
        correlationId: session.correlationId,
        amount: this.amount(),
      }),
    );
  }

  async cancelSelected(): Promise<void> {
    const session = this.selectedSession();
    if (!session) {
      this.lastActionMessage.set('Select a pre-auth session before cancelling.');
      return;
    }

    await this.runPreAuthAction('cancel', () =>
      this.executeAction({
        action: 'cancel',
        preAuthId: session.externalReference,
        correlationId: session.correlationId,
      }),
    );
  }

  async expireSelected(): Promise<void> {
    const session = this.selectedSession();
    if (!session) {
      this.lastActionMessage.set('Select a pre-auth session before expiring it.');
      return;
    }

    await this.runPreAuthAction('expire', () =>
      this.executeAction({
        action: 'expire',
        preAuthId: session.externalReference,
        correlationId: session.correlationId,
      }),
    );
  }

  liveAction(event: LabLiveEvent): string {
    if ('action' in event && typeof event.action === 'string' && event.action.length > 0) {
      return event.action;
    }

    return 'update';
  }

  liveMessage(event: LabLiveEvent): string {
    if ('message' in event && typeof event.message === 'string' && event.message.length > 0) {
      return event.message;
    }

    return 'Live update received.';
  }

  isTerminalStatus(status: string): boolean {
    return status === 'CANCELLED' || status === 'COMPLETED' || status === 'EXPIRED' || status === 'FAILED';
  }

  formatDateTime(value: string): string {
    return value ? new Date(value).toLocaleString() : 'n/a';
  }

  formatJson(value: string | null | undefined): string {
    if (!value) {
      return '{}';
    }

    try {
      return JSON.stringify(JSON.parse(value), null, 2);
    } catch {
      return value;
    }
  }

  numberOrNull(value: string | number | null): number | null {
    if (value === null || value === '') {
      return null;
    }

    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  }

  private async loadSites(): Promise<void> {
    try {
      const sites = await firstValueFrom(this.api.getSites(false));
      this.sites.set(sites);

      if (sites.length > 0) {
        this.selectedSiteId.set(sites[0].id);
        await this.refreshSelectedSite();
      }
    } catch {
      this.error.set('Sites could not be loaded for the pre-auth console.');
    }
  }

  private buildCreatePayload(): Pick<
    LabPreAuthActionRequest,
    | 'correlationId'
    | 'pumpNumber'
    | 'nozzleNumber'
    | 'amount'
    | 'expiresInSeconds'
    | 'simulateFailure'
    | 'failureStatusCode'
    | 'failureMessage'
    | 'failureCode'
    | 'customerName'
    | 'customerTaxId'
    | 'customerTaxOffice'
  > {
    const pump = this.selectedPump();
    const nozzle = this.selectedPumpNozzles().find((entry) => entry.id === this.selectedNozzleId()) ?? null;

    return {
      correlationId: this.correlationId() || null,
      pumpNumber: pump?.pumpNumber ?? null,
      nozzleNumber: nozzle?.nozzleNumber ?? null,
      amount: this.amount(),
      expiresInSeconds: this.expiresInSeconds(),
      simulateFailure: this.simulateFailure(),
      failureStatusCode: this.simulateFailure() ? this.failureStatusCode() : null,
      failureMessage: this.simulateFailure() ? this.failureMessage() : null,
      failureCode: this.simulateFailure() ? this.failureCode() : null,
      customerName: this.customerName() || null,
      customerTaxId: this.customerTaxId() || null,
      customerTaxOffice: this.customerTaxOffice() || null,
    };
  }

  private ensurePumpAndNozzleSelection(forecourt: SiteForecourtView): void {
    const currentPump =
      forecourt.pumps.find((pump) => pump.id === this.selectedPumpId()) ?? forecourt.pumps[0] ?? null;
    const currentNozzle =
      currentPump?.nozzles.find((nozzle) => nozzle.id === this.selectedNozzleId()) ??
      currentPump?.nozzles[0] ??
      null;

    this.selectedPumpId.set(currentPump?.id ?? null);
    this.selectedNozzleId.set(currentNozzle?.id ?? null);
  }

  private ensureSessionSelection(sessions: PreAuthSessionRecord[]): void {
    if (sessions.some((session) => session.id === this.selectedSessionId())) {
      return;
    }

    this.selectedSessionId.set(sessions[0]?.id ?? '');
  }

  private parseTimeline(timelineJson: string | undefined): TimelineEntry[] {
    if (!timelineJson) {
      return [];
    }

    try {
      const value = JSON.parse(timelineJson) as TimelineEntry[];
      return Array.isArray(value) ? value : [];
    } catch {
      return [];
    }
  }

  private isForecourtEvent(event: LabLiveEvent): event is Extract<LabLiveEvent, { eventType: 'forecourt-action' }> {
    return event.eventType === 'forecourt-action';
  }

  private isPreAuthEvent(event: LabLiveEvent): event is Extract<LabLiveEvent, { eventType: 'preauth-action' }> {
    return event.eventType === 'preauth-action';
  }

  private isRelevantEvent(event: LabLiveEvent): boolean {
    const site = this.selectedSite();
    if (!site) {
      return false;
    }

    if (this.isPreAuthEvent(event)) {
      return event.siteCode === site.siteCode;
    }

    if (this.isForecourtEvent(event)) {
      return event.nozzle?.siteCode === site.siteCode || this.hasSessionWithCorrelation(event.correlationId);
    }

    return false;
  }

  private hasSessionWithCorrelation(correlationId: string | undefined): boolean {
    return !!correlationId && this.sessions().some((session) => session.correlationId === correlationId);
  }

  private async executeAction(request: LabPreAuthActionRequest) {
    const siteId = this.selectedSiteId();
    return firstValueFrom(this.api.simulatePreAuth(siteId, request));
  }

  private async runPreAuthAction(
    action: string,
    operation: () => Promise<{ message: string; session: PreAuthSessionRecord | null; correlationId: string }>,
  ): Promise<void> {
    this.busyAction.set(action);

    try {
      const result = await operation();
      this.lastActionMessage.set(result.message);

      if (result.session) {
        this.selectedSessionId.set(result.session.id);
        this.correlationId.set(result.correlationId);
      }

      await this.refreshSelectedSite();
    } catch (error) {
      this.lastActionMessage.set(this.extractErrorMessage(error, `${action} failed.`));
      await this.refreshSelectedSite();
    } finally {
      this.busyAction.set(null);
    }
  }

  hasValidationProblems(report: PayloadContractValidationReport | null | undefined): boolean {
    return !!report && (report.errorCount > 0 || report.warningCount > 0 || report.mismatchCount > 0 || report.missingCount > 0);
  }

  sessionHasValidationProblems(session: PreAuthSessionRecord): boolean {
    return this.hasValidationProblems(session.requestValidation) || this.hasValidationProblems(session.responseValidation);
  }

  sessionValidationOutcome(session: PreAuthSessionRecord): string {
    if (this.hasValidationProblems(session.requestValidation)) {
      return `Request ${session.requestValidation.outcome}`;
    }

    if (this.hasValidationProblems(session.responseValidation)) {
      return `Response ${session.responseValidation.outcome}`;
    }

    if (session.requestValidation.enabled || session.responseValidation.enabled) {
      return 'Validated';
    }

    return 'No rules';
  }

  private extractErrorMessage(error: unknown, fallback: string): string {
    if (error instanceof HttpErrorResponse) {
      if (typeof error.error === 'string' && error.error.length > 0) {
        return error.error;
      }

      if (error.error && typeof error.error.message === 'string') {
        return error.error.message;
      }
    }

    return fallback;
  }
}
