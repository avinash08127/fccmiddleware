import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { filter, firstValueFrom } from 'rxjs';
import {
  type ForecourtNozzleView,
  type ForecourtPumpView,
  type LogRecord,
  type NozzleSimulationSnapshot,
  type SiteForecourtView,
  type SiteListItem,
  type TransactionRecord,
  LabApiService,
} from '../../core/services/lab-api.service';
import {
  LiveUpdatesService,
  type ForecourtLiveEvent,
  type LabLiveEvent,
  type PreAuthLiveEvent,
} from '../../core/services/live-updates.service';

interface PumpSelection {
  pump: ForecourtPumpView;
  nozzle: ForecourtNozzleView;
}

@Component({
  selector: 'vl-live-console',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <section class="hero">
      <div>
        <p class="eyebrow">VL-2.4 Live Pump Console</p>
        <h2>Operate the virtual forecourt against the real simulation core.</h2>
        <p class="copy">
          Lift, dispense, hang, and inject failures from the operator surface. Nozzle state,
          generated transactions, and transition logs refresh from SignalR and the persisted API
          views.
        </p>
      </div>

      <div class="hero-panel">
        <label>
          <span>Site</span>
          <select [ngModel]="selectedSiteId()" (ngModelChange)="changeSite($event)">
            <option *ngFor="let site of sites()" [ngValue]="site.id">
              {{ site.siteCode }} · {{ site.preAuthMode }} · {{ site.deliveryMode }}
            </option>
          </select>
        </label>

        <div class="hero-meta">
          <span class="pill">{{ liveUpdates.connectionState() }}</span>
          <span class="pill" [class.pill--warning]="!!error()">{{ selectedSite()?.siteCode ?? 'No site' }}</span>
        </div>

        <p *ngIf="selectedSite() as site" class="meta-copy">
          {{ site.forecourt.activePumpCount }}/{{ site.forecourt.pumpCount }} pumps active ·
          {{ site.forecourt.activeNozzleCount }}/{{ site.forecourt.nozzleCount }} nozzles active
        </p>
      </div>
    </section>

    <section *ngIf="error()" class="error-banner">{{ error() }}</section>

    <section class="workspace" *ngIf="selectedSite() as site; else emptyState">
      <article class="panel forecourt-panel">
        <header class="panel-header">
          <div>
            <h3>Forecourt</h3>
            <p>Select a nozzle, then operate it from the control stack.</p>
          </div>
          <button type="button" class="secondary" (click)="refreshSelectedSite()" [disabled]="loading()">
            {{ loading() ? 'Refreshing…' : 'Refresh' }}
          </button>
        </header>

        <div *ngIf="forecourt() as currentForecourt; else loadingForecourt" class="pump-grid">
          <article class="pump-card" *ngFor="let pump of currentForecourt.pumps">
            <header>
              <div>
                <strong>{{ pump.label }}</strong>
                <p>Pump {{ pump.pumpNumber }} / FCC {{ pump.fccPumpNumber }}</p>
              </div>
              <span class="status-chip" [class.status-chip--warning]="!pump.isActive">
                {{ pump.isActive ? 'Active' : 'Inactive' }}
              </span>
            </header>

            <button
              *ngFor="let nozzle of pump.nozzles"
              type="button"
              class="nozzle-card"
              [class.nozzle-card--selected]="selectedNozzleId() === nozzle.id"
              [class.nozzle-card--faulted]="nozzle.state === 'Faulted'"
              (click)="selectNozzle(pump.id, nozzle.id)"
            >
              <div>
                <strong>{{ nozzle.label }}</strong>
                <p>{{ nozzle.productCode }} · N{{ nozzle.nozzleNumber }} / FCC {{ nozzle.fccNozzleNumber }}</p>
              </div>
              <span class="state-pill">{{ nozzle.state }}</span>
            </button>
          </article>
        </div>
      </article>

      <article class="panel controls-panel" *ngIf="selectedPumpNozzle() as selection">
        <header class="panel-header">
          <div>
            <h3>{{ selection.nozzle.label }}</h3>
            <p>
              Pump {{ selection.pump.pumpNumber }} · {{ selection.nozzle.productName }} ·
              {{ selection.nozzle.state }}
            </p>
          </div>
          <span class="status-chip" [class.status-chip--warning]="selection.nozzle.state === 'Faulted'">
            {{ latestSnapshot()?.correlationId || correlationId() || 'new-correlation' }}
          </span>
        </header>

        <div class="form-grid">
          <label>
            <span>Correlation ID</span>
            <input [ngModel]="correlationId()" (ngModelChange)="correlationId.set($event.trim())" />
          </label>

          <label>
            <span>Flow rate (L/min)</span>
            <input
              type="number"
              min="1"
              step="1"
              [ngModel]="flowRateLitresPerMinute()"
              (ngModelChange)="flowRateLitresPerMinute.set(numberOrNull($event) ?? 30)"
            />
          </label>

          <label>
            <span>Target amount</span>
            <input
              type="number"
              min="0"
              step="0.01"
              [ngModel]="targetAmount()"
              (ngModelChange)="targetAmount.set(numberOrNull($event))"
            />
          </label>

          <label>
            <span>Target volume</span>
            <input
              type="number"
              min="0"
              step="0.01"
              [ngModel]="targetVolume()"
              (ngModelChange)="targetVolume.set(numberOrNull($event))"
            />
          </label>

          <label>
            <span>Stop elapsed seconds</span>
            <input
              type="number"
              min="1"
              step="1"
              [ngModel]="stopElapsedSeconds()"
              (ngModelChange)="stopElapsedSeconds.set(numberOrNull($event) ?? 20)"
            />
          </label>

          <label>
            <span>Hang elapsed seconds</span>
            <input
              type="number"
              min="0"
              step="1"
              [ngModel]="hangElapsedSeconds()"
              (ngModelChange)="hangElapsedSeconds.set(numberOrNull($event))"
            />
          </label>
        </div>

        <div class="toggle-grid">
          <label class="check">
            <input
              type="checkbox"
              [ngModel]="injectDuplicate()"
              (ngModelChange)="injectDuplicate.set(!!$event)"
            />
            <span>Inject duplicate transaction</span>
          </label>

          <label class="check">
            <input
              type="checkbox"
              [ngModel]="simulateFailure()"
              (ngModelChange)="simulateFailure.set(!!$event)"
            />
            <span>Simulate delivery failure</span>
          </label>

          <label class="check">
            <input type="checkbox" [ngModel]="forceFault()" (ngModelChange)="forceFault.set(!!$event)" />
            <span>Force nozzle fault</span>
          </label>

          <label class="check">
            <input
              type="checkbox"
              [ngModel]="clearFaultOnHang()"
              (ngModelChange)="clearFaultOnHang.set(!!$event)"
            />
            <span>Clear fault on hang</span>
          </label>
        </div>

        <label>
          <span>Failure message</span>
          <input
            [ngModel]="failureMessage()"
            (ngModelChange)="failureMessage.set($event)"
            placeholder="forced push failure"
          />
        </label>

        <div class="action-row">
          <button type="button" (click)="liftNozzle()" [disabled]="busyAction() !== null">Lift nozzle</button>
          <button type="button" (click)="startDispense()" [disabled]="busyAction() !== null">
            Start dispense
          </button>
          <button type="button" class="secondary" (click)="stopDispense()" [disabled]="busyAction() !== null">
            Stop dispense
          </button>
          <button type="button" class="danger" (click)="hangNozzle()" [disabled]="busyAction() !== null">
            Hang nozzle
          </button>
        </div>

        <p class="action-copy" *ngIf="busyAction()">Running {{ busyAction() }}…</p>
        <p class="action-copy" *ngIf="lastActionMessage()">{{ lastActionMessage() }}</p>

        <div class="snapshot-grid" *ngIf="latestSnapshot() as snapshot">
          <div>
            <span>Live state</span>
            <strong>{{ snapshot.state }}</strong>
          </div>
          <div>
            <span>Correlation</span>
            <strong>{{ snapshot.correlationId || 'n/a' }}</strong>
          </div>
          <div>
            <span>Pre-auth link</span>
            <strong>{{ snapshot.preAuthSessionId || 'none' }}</strong>
          </div>
          <div>
            <span>Updated</span>
            <strong>{{ formatDateTime(snapshot.updatedAtUtc) }}</strong>
          </div>
        </div>
      </article>
    </section>

    <section class="activity-grid" *ngIf="selectedSite()">
      <article class="panel">
        <header class="panel-header">
          <div>
            <h3>Recent transactions</h3>
            <p>Generated dispense results for the selected site.</p>
          </div>
          <span class="pill">{{ transactions().length }}</span>
        </header>

        <div class="stack" *ngIf="transactions().length; else emptyTransactions">
          <div class="row-card" *ngFor="let transaction of transactions().slice(0, 8)">
            <div>
              <strong>{{ transaction.externalTransactionId }}</strong>
              <p>
                {{ transaction.productCode }} · Pump {{ transaction.pumpNumber }}/Nozzle
                {{ transaction.nozzleNumber }} · {{ transaction.deliveryMode }}
              </p>
            </div>
            <div class="row-meta">
              <span>{{ transaction.status }}</span>
              <small>
                {{ transaction.volume | number: '1.2-2' }} L · {{ transaction.totalAmount | number: '1.2-2' }}
              </small>
            </div>
          </div>
        </div>
      </article>

      <article class="panel">
        <header class="panel-header">
          <div>
            <h3>State transitions</h3>
            <p>Persisted transition and generation logs kept in view.</p>
          </div>
          <span class="pill">{{ stateLogs().length }}</span>
        </header>

        <div class="stack" *ngIf="stateLogs().length; else emptyLogs">
          <div class="row-card" *ngFor="let entry of stateLogs().slice(0, 8)">
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

      <article class="panel feed-panel">
        <header class="panel-header">
          <div>
            <h3>Live event feed</h3>
            <p>SignalR updates received for the active site.</p>
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

    <ng-template #loadingForecourt>
      <p class="empty-state">Loading forecourt state…</p>
    </ng-template>

    <ng-template #emptyTransactions>
      <p class="empty-state">No transactions generated yet for this site.</p>
    </ng-template>

    <ng-template #emptyLogs>
      <p class="empty-state">No transition logs captured yet for this site.</p>
    </ng-template>

    <ng-template #emptyFeed>
      <p class="empty-state">No live events received yet for this site.</p>
    </ng-template>

    <ng-template #emptyState>
      <section class="panel empty-panel">
        <h3>No active site available</h3>
        <p>Select or seed a site before using the live pump console.</p>
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
    .activity-grid {
      display: grid;
      gap: 1rem;
    }

    .hero {
      grid-template-columns: minmax(0, 1.8fr) minmax(300px, 1fr);
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
    .action-copy {
      color: var(--vl-muted);
    }

    .hero-panel,
    .controls-panel,
    .feed-panel,
    .panel {
      display: grid;
      gap: 1rem;
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
      cursor: wait;
      opacity: 0.65;
    }

    .hero-meta,
    .panel-header,
    .row-card,
    .pump-card header,
    .action-row {
      display: flex;
      gap: 0.75rem;
      justify-content: space-between;
    }

    .pill,
    .status-chip,
    .state-pill {
      align-items: center;
      background: rgba(29, 122, 90, 0.12);
      border-radius: 999px;
      color: var(--vl-emerald);
      display: inline-flex;
      font-size: 0.8rem;
      font-weight: 600;
      padding: 0.28rem 0.7rem;
    }

    .pill--warning,
    .status-chip--warning {
      background: rgba(207, 95, 45, 0.16);
      color: var(--vl-accent);
    }

    .workspace {
      grid-template-columns: minmax(0, 1.3fr) minmax(340px, 0.9fr);
    }

    .pump-grid {
      display: grid;
      gap: 1rem;
      grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
    }

    .pump-card {
      background: rgba(255, 255, 255, 0.58);
      border: 1px solid var(--vl-line);
      border-radius: 18px;
      display: grid;
      gap: 0.75rem;
      padding: 1rem;
    }

    .pump-card header p,
    .row-card p {
      color: var(--vl-muted);
    }

    .nozzle-card {
      align-items: center;
      background: rgba(255, 255, 255, 0.7);
      border: 1px solid var(--vl-line);
      color: inherit;
      display: flex;
      justify-content: space-between;
      text-align: left;
    }

    .nozzle-card--selected {
      border-color: rgba(207, 95, 45, 0.4);
      box-shadow: inset 0 0 0 1px rgba(207, 95, 45, 0.24);
    }

    .nozzle-card--faulted {
      background: rgba(139, 30, 30, 0.08);
    }

    .form-grid,
    .toggle-grid,
    .snapshot-grid,
    .activity-grid {
      grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
    }

    .form-grid,
    .toggle-grid,
    .snapshot-grid,
    .stack {
      display: grid;
      gap: 0.75rem;
    }

    .toggle-grid {
      align-items: start;
    }

    .check {
      align-items: center;
      background: rgba(255, 255, 255, 0.56);
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

    .snapshot-grid div {
      background: rgba(29, 122, 90, 0.06);
      border-radius: 16px;
      padding: 0.85rem 1rem;
    }

    .snapshot-grid span {
      color: var(--vl-muted);
      display: block;
      margin-bottom: 0.2rem;
    }

    .row-card {
      align-items: start;
      border: 1px solid var(--vl-line);
      border-radius: 16px;
      padding: 0.95rem 1rem;
    }

    .row-meta {
      display: grid;
      gap: 0.25rem;
      justify-items: end;
      text-align: right;
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
      .activity-grid {
        grid-template-columns: 1fr;
      }
    }

    @media (max-width: 720px) {
      .action-row,
      .panel-header,
      .row-card,
      .pump-card header {
        flex-direction: column;
      }

      .row-meta {
        justify-items: start;
        text-align: left;
      }
    }
  `,
})
export class LiveConsoleComponent {
  readonly sites = signal<SiteListItem[]>([]);
  readonly selectedSiteId = signal<string>('');
  readonly forecourt = signal<SiteForecourtView | null>(null);
  readonly transactions = signal<TransactionRecord[]>([]);
  readonly logs = signal<LogRecord[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly busyAction = signal<string | null>(null);
  readonly selectedPumpId = signal<string | null>(null);
  readonly selectedNozzleId = signal<string | null>(null);
  readonly correlationId = signal(`pump-${Date.now()}`);
  readonly flowRateLitresPerMinute = signal(30);
  readonly targetAmount = signal<number | null>(null);
  readonly targetVolume = signal<number | null>(10);
  readonly stopElapsedSeconds = signal(20);
  readonly hangElapsedSeconds = signal<number | null>(null);
  readonly injectDuplicate = signal(false);
  readonly simulateFailure = signal(false);
  readonly forceFault = signal(false);
  readonly clearFaultOnHang = signal(true);
  readonly failureMessage = signal('forced push failure');
  readonly lastActionMessage = signal('');
  readonly liveFeed = signal<LabLiveEvent[]>([]);
  readonly snapshotsByNozzleId = signal<Record<string, NozzleSimulationSnapshot>>({});
  readonly selectedSite = computed(
    () => this.sites().find((site) => site.id === this.selectedSiteId()) ?? null,
  );
  readonly selectedPumpNozzle = computed<PumpSelection | null>(() => {
    const forecourt = this.forecourt();
    const pumpId = this.selectedPumpId();
    const nozzleId = this.selectedNozzleId();

    if (!forecourt || !pumpId || !nozzleId) {
      return null;
    }

    const pump = forecourt.pumps.find((entry) => entry.id === pumpId);
    const nozzle = pump?.nozzles.find((entry) => entry.id === nozzleId);
    return pump && nozzle ? { pump, nozzle } : null;
  });
  readonly latestSnapshot = computed(() => {
    const nozzleId = this.selectedNozzleId();
    return nozzleId ? this.snapshotsByNozzleId()[nozzleId] ?? null : null;
  });
  readonly stateLogs = computed(() =>
    this.logs().filter((entry) =>
      entry.category === 'StateTransition' || entry.category === 'TransactionGenerated',
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
        this.prependLiveEvent(event);

        if (this.isForecourtEvent(event) && event.nozzle) {
          const nozzle = event.nozzle;
          this.snapshotsByNozzleId.update((snapshots) => ({
            ...snapshots,
            [nozzle.nozzleId]: nozzle,
          }));
        }

        void this.refreshSelectedSite();
      });
  }

  async changeSite(siteId: string): Promise<void> {
    this.selectedSiteId.set(siteId);
    this.lastActionMessage.set('');
    this.liveFeed.set([]);
    this.snapshotsByNozzleId.set({});
    await this.refreshSelectedSite();
  }

  selectNozzle(pumpId: string, nozzleId: string): void {
    this.selectedPumpId.set(pumpId);
    this.selectedNozzleId.set(nozzleId);
  }

  async refreshSelectedSite(): Promise<void> {
    const siteId = this.selectedSiteId();
    const site = this.selectedSite();
    if (!siteId || !site) {
      return;
    }

    this.loading.set(true);

    try {
      const [forecourt, transactions, logs] = await Promise.all([
        firstValueFrom(this.api.getForecourt(siteId)),
        firstValueFrom(this.api.getTransactions({ siteCode: site.siteCode, limit: 20 })),
        firstValueFrom(this.api.getLogs({ siteCode: site.siteCode, limit: 40 })),
      ]);

      this.forecourt.set(forecourt);
      this.transactions.set(transactions);
      this.logs.set(logs);
      this.ensureSelection(forecourt);
      this.error.set(null);
    } catch {
      this.error.set('Live console data could not be loaded. Check the API and SignalR hub.');
    } finally {
      this.loading.set(false);
    }
  }

  async liftNozzle(): Promise<void> {
    await this.runNozzleAction('lift', async () => {
      const selection = this.selectedPumpNozzle();
      const siteId = this.selectedSiteId();
      if (!selection || !siteId) {
        return;
      }

      return firstValueFrom(
        this.api.liftNozzle(siteId, selection.pump.id, selection.nozzle.id, {
          correlationId: this.correlationId() || null,
          forceFault: this.forceFault(),
          faultMessage: this.forceFault() ? this.failureMessage() : null,
        }),
      );
    });
  }

  async startDispense(): Promise<void> {
    await this.runNozzleAction('start-dispense', async () => {
      const selection = this.selectedPumpNozzle();
      const siteId = this.selectedSiteId();
      if (!selection || !siteId) {
        return;
      }

      return firstValueFrom(
        this.api.dispense(siteId, selection.pump.id, selection.nozzle.id, {
          action: 'start',
          correlationId: this.correlationId() || null,
          flowRateLitresPerMinute: this.flowRateLitresPerMinute(),
          targetAmount: this.targetAmount(),
          targetVolume: this.targetVolume(),
          injectDuplicate: this.injectDuplicate(),
          simulateFailure: this.simulateFailure(),
          failureMessage: this.simulateFailure() ? this.failureMessage() : null,
          forceFault: this.forceFault(),
        }),
      );
    });
  }

  async stopDispense(): Promise<void> {
    await this.runNozzleAction('stop-dispense', async () => {
      const selection = this.selectedPumpNozzle();
      const siteId = this.selectedSiteId();
      if (!selection || !siteId) {
        return;
      }

      return firstValueFrom(
        this.api.dispense(siteId, selection.pump.id, selection.nozzle.id, {
          action: 'stop',
          correlationId: this.correlationId() || null,
          elapsedSeconds: this.stopElapsedSeconds(),
        }),
      );
    });
  }

  async hangNozzle(): Promise<void> {
    await this.runNozzleAction('hang', async () => {
      const selection = this.selectedPumpNozzle();
      const siteId = this.selectedSiteId();
      if (!selection || !siteId) {
        return;
      }

      return firstValueFrom(
        this.api.hangNozzle(siteId, selection.pump.id, selection.nozzle.id, {
          correlationId: this.correlationId() || null,
          elapsedSeconds: this.hangElapsedSeconds(),
          clearFault: this.clearFaultOnHang(),
        }),
      );
    });
  }

  liveAction(event: LabLiveEvent): string {
    if ('action' in event && typeof event.action === 'string' && event.action.length > 0) {
      return event.action;
    }

    return 'update';
  }

  liveMessage(event: LabLiveEvent): string {
    if (this.isForecourtEvent(event)) {
      return event.message;
    }

    if (this.isPreAuthEvent(event)) {
      return event.message;
    }

    return 'Live update received.';
  }

  formatDateTime(value: string): string {
    return value ? new Date(value).toLocaleString() : 'n/a';
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
      this.error.set('Sites could not be loaded for the live console.');
    }
  }

  private ensureSelection(forecourt: SiteForecourtView): void {
    const selectedPump = forecourt.pumps.find((pump) => pump.id === this.selectedPumpId());
    const selectedNozzle = selectedPump?.nozzles.find((nozzle) => nozzle.id === this.selectedNozzleId());

    if (selectedPump && selectedNozzle) {
      return;
    }

    const firstPump = forecourt.pumps[0];
    const firstNozzle = firstPump?.nozzles[0];
    this.selectedPumpId.set(firstPump?.id ?? null);
    this.selectedNozzleId.set(firstNozzle?.id ?? null);
  }

  private prependLiveEvent(event: LabLiveEvent): void {
    this.liveFeed.update((entries) => [event, ...entries].slice(0, 20));
  }

  private isForecourtEvent(event: LabLiveEvent): event is ForecourtLiveEvent {
    return event.eventType === 'forecourt-action';
  }

  private isPreAuthEvent(event: LabLiveEvent): event is PreAuthLiveEvent {
    return event.eventType === 'preauth-action';
  }

  private isRelevantEvent(event: LabLiveEvent): event is ForecourtLiveEvent | PreAuthLiveEvent {
    const site = this.selectedSite();
    if (!site) {
      return false;
    }

    if (this.isForecourtEvent(event)) {
      return event.nozzle?.siteCode === site.siteCode;
    }

    if (this.isPreAuthEvent(event)) {
      return event.siteCode === site.siteCode;
    }

    return false;
  }

  private async runNozzleAction(
    action: string,
    operation: () => Promise<{ nozzle: NozzleSimulationSnapshot | null; message: string } | void>,
  ): Promise<void> {
    this.busyAction.set(action);

    try {
      const result = await operation();
      if (result?.nozzle) {
        const nozzle = result.nozzle;
        this.snapshotsByNozzleId.update((snapshots) => ({
          ...snapshots,
          [nozzle.nozzleId]: nozzle,
        }));
      }

      this.lastActionMessage.set(result?.message ?? `${action} completed.`);
      await this.refreshSelectedSite();
    } catch (error) {
      this.lastActionMessage.set(this.extractErrorMessage(error, `${action} failed.`));
      await this.refreshSelectedSite();
    } finally {
      this.busyAction.set(null);
    }
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
