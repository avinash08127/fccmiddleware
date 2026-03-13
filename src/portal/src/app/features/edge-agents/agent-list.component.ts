import { Component, DestroyRef, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subject, EMPTY, interval, merge } from 'rxjs';
import { switchMap, catchError, debounceTime, exhaustMap } from 'rxjs/operators';
import { TableModule } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { CardModule } from 'primeng/card';
import { SelectModule } from 'primeng/select';
import { PanelModule } from 'primeng/panel';
import { InputTextModule } from 'primeng/inputtext';
import { TagModule } from 'primeng/tag';
import { BadgeModule } from 'primeng/badge';

import { AgentService } from '../../core/services/agent.service';
import { MasterDataService } from '../../core/services/master-data.service';
import { AgentHealthSummary, ConnectivityState } from '../../core/models/agent.model';
import { LegalEntity } from '../../core/models/master-data.model';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { UtcDatePipe } from '../../shared/pipes/utc-date.pipe';
import { hasAnyRequiredRole, getCurrentAccount } from '../../core/auth/auth-state';
import { MsalService } from '@azure/msal-angular';

/** Offline threshold: 5 minutes without a heartbeat */
const OFFLINE_THRESHOLD_MS = 5 * 60 * 1000;
const PAGE_SIZE = 100;

function isAgentOffline(agent: AgentHealthSummary): boolean {
  if (agent.connectivityState === ConnectivityState.FULLY_OFFLINE) return true;
  if (!agent.lastSeenAt) return true;
  return Date.now() - new Date(agent.lastSeenAt).getTime() > OFFLINE_THRESHOLD_MS;
}

function connectivityLabel(state: ConnectivityState | null): string {
  switch (state) {
    case ConnectivityState.FULLY_ONLINE:     return 'Online';
    case ConnectivityState.INTERNET_DOWN:    return 'Internet Down';
    case ConnectivityState.FCC_UNREACHABLE:  return 'FCC Unreachable';
    case ConnectivityState.FULLY_OFFLINE:    return 'Offline';
    default:                                 return 'Unknown';
  }
}

function connectivityCssClass(state: ConnectivityState | null): string {
  switch (state) {
    case ConnectivityState.FULLY_ONLINE:    return 'badge-online';
    case ConnectivityState.INTERNET_DOWN:   return 'badge-internet-down';
    case ConnectivityState.FCC_UNREACHABLE: return 'badge-fcc-unreachable';
    case ConnectivityState.FULLY_OFFLINE:   return 'badge-offline';
    default:                                return 'badge-unknown';
  }
}

function formatLag(seconds: number | null): string {
  if (seconds === null) return '—';
  if (seconds < 60) return `${seconds}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m`;
  return `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m`;
}

interface AgentFilters {
  siteCode: string;
  connectivityState: ConnectivityState | null;
}

@Component({
  selector: 'app-agent-list',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    TableModule,
    ButtonModule,
    CardModule,
    SelectModule,
    PanelModule,
    InputTextModule,
    TagModule,
    BadgeModule,
    EmptyStateComponent,
    UtcDatePipe,
  ],
  template: `
    <div class="page-container">
      <!-- Header -->
      <div class="page-header">
        <h1 class="page-title"><i class="pi pi-server"></i> Edge Agent Monitoring</h1>
        <div class="header-actions">
          <p-select
            [options]="legalEntityOptions()"
            [ngModel]="selectedLegalEntityId()"
            (ngModelChange)="onLegalEntityChange($event)"
            optionLabel="label"
            optionValue="value"
            placeholder="Select Legal Entity"
            styleClass="entity-selector"
          />
          @if (isAdmin()) {
            <p-button
              label="Generate Token"
              icon="pi pi-key"
              severity="secondary"
              size="small"
              (onClick)="navigateToBootstrapToken()"
            />
          }
          <p-button
            icon="pi pi-refresh"
            severity="secondary"
            [rounded]="true"
            [text]="true"
            pTooltip="Refresh now"
            (onClick)="manualRefresh()"
          />
        </div>
      </div>

      @if (legalEntitiesError()) {
        <div class="error-msg">
          <i class="pi pi-exclamation-triangle"></i>
          Failed to load legal entities. Please refresh the page.
        </div>
      }

      @if (!selectedLegalEntityId()) {
        <app-empty-state
          icon="pi-building"
          title="Select a Legal Entity"
          description="Choose a legal entity above to view Edge Agent status."
        />
      } @else {

        <!-- Filters -->
        <p-panel header="Filters" [toggleable]="true" styleClass="filters-panel">
          <div class="filters-row">
            <div class="filter-field">
              <label for="agent-filter-site-code">Site Code</label>
              <input
                pInputText
                id="agent-filter-site-code"
                [(ngModel)]="filters.siteCode"
                placeholder="Search site code…"
                (ngModelChange)="onSiteCodeFilterChange()"
              />
            </div>
            <div class="filter-field">
              <label for="agent-filter-connectivity-state">Connectivity State</label>
              <p-select
                inputId="agent-filter-connectivity-state"
                [options]="connectivityOptions"
                [(ngModel)]="filters.connectivityState"
                placeholder="All States"
                [showClear]="true"
                (ngModelChange)="onConnectivityFilterChange()"
              />
            </div>
            <div class="filter-field filter-field--action">
              <p-button
                label="Clear"
                severity="secondary"
                icon="pi pi-times"
                size="small"
                (onClick)="clearFilters()"
              />
            </div>
          </div>
        </p-panel>

        @if (loading() && agents().length === 0) {
          <div class="loading-msg"><i class="pi pi-spin pi-spinner"></i> Loading agents…</div>
        }

        @if (error()) {
          <div class="error-msg">
            <i class="pi pi-exclamation-triangle"></i>
            Failed to load agents. <button type="button" class="link-btn" (click)="manualRefresh()">Retry</button>
          </div>
        }

        <!-- Offline Section -->
        @if (filteredOffline().length > 0) {
          <p-card styleClass="offline-section">
            <ng-template pTemplate="header">
              <div class="offline-header">
                <i class="pi pi-exclamation-circle"></i>
                <span>Offline / Unreachable Agents</span>
                <p-badge [value]="filteredOffline().length.toString()" severity="danger" />
              </div>
            </ng-template>

            <p-table
              [value]="filteredOffline()"
              sortMode="single"
              [paginator]="filteredOffline().length > 10"
              [rows]="10"
              styleClass="p-datatable-sm p-datatable-striped p-datatable-hoverable-rows"
            >
              <ng-template pTemplate="header">
                <tr>
                  <th pSortableColumn="siteCode">Site <p-sortIcon field="siteCode" /></th>
                  <th>Connectivity</th>
                  <th pSortableColumn="bufferDepth">Buffer <p-sortIcon field="bufferDepth" /></th>
                  <th pSortableColumn="lastSeenAt">Last Seen <p-sortIcon field="lastSeenAt" /></th>
                  <th pSortableColumn="batteryPercent">Battery <p-sortIcon field="batteryPercent" /></th>
                  <th pSortableColumn="agentVersion">Version <p-sortIcon field="agentVersion" /></th>
                  <th pSortableColumn="syncLagSeconds">Sync Lag <p-sortIcon field="syncLagSeconds" /></th>
                </tr>
              </ng-template>
              <ng-template pTemplate="body" let-agent>
                <tr class="clickable-row" tabindex="0" (click)="navigateToDetail(agent)" (keydown.enter)="navigateToDetail(agent)">
                  <td>
                    <div class="site-cell">
                      <strong>{{ agent.siteCode }}</strong>
                      @if (agent.siteName) { <span class="site-name">{{ agent.siteName }}</span> }
                    </div>
                  </td>
                  <td>
                    <span class="conn-badge" [class]="connectivityClass(agent.connectivityState)">
                      {{ connectivityLabel(agent.connectivityState) }}
                    </span>
                  </td>
                  <td>{{ agent.bufferDepth ?? '—' }}</td>
                  <td>{{ agent.lastSeenAt | utcDate:'short' }}</td>
                  <td>
                    @if (agent.batteryPercent !== null) {
                      <span [class]="batteryClass(agent.batteryPercent)">
                        {{ agent.batteryPercent }}%
                        @if (agent.isCharging) { <i class="pi pi-bolt"></i> }
                      </span>
                    } @else { — }
                  </td>
                  <td><code>{{ agent.agentVersion }}</code></td>
                  <td [class]="lagClass(agent.syncLagSeconds)">{{ formatLag(agent.syncLagSeconds) }}</td>
                </tr>
              </ng-template>
            </p-table>
          </p-card>
        }

        <!-- Online Agents Table -->
        <p-card styleClass="table-card">
          <ng-template pTemplate="header">
            <div class="card-header-row">
              <span>
                Online Agents
                @if (totalCount() !== null) {
                  <span class="agent-count">— {{ agents().length }} of {{ totalCount() }} loaded</span>
                }
              </span>
              <span class="refresh-note">Auto-refreshes every 30 s</span>
            </div>
          </ng-template>

          <p-table
            [value]="filteredOnline()"
            sortMode="single"
            [paginator]="filteredOnline().length > 20"
            [rows]="20"
            [rowsPerPageOptions]="[20, 50, 100]"
            styleClass="p-datatable-sm p-datatable-striped p-datatable-hoverable-rows"
            [tableStyle]="{ 'min-width': '900px' }"
          >
            <ng-template pTemplate="header">
              <tr>
                <th pSortableColumn="siteCode">Site <p-sortIcon field="siteCode" /></th>
                <th pSortableColumn="connectivityState">Connectivity <p-sortIcon field="connectivityState" /></th>
                <th pSortableColumn="bufferDepth">Buffer Depth <p-sortIcon field="bufferDepth" /></th>
                <th pSortableColumn="lastSeenAt">Last Seen <p-sortIcon field="lastSeenAt" /></th>
                <th pSortableColumn="batteryPercent">Battery <p-sortIcon field="batteryPercent" /></th>
                <th pSortableColumn="agentVersion">Version <p-sortIcon field="agentVersion" /></th>
                <th pSortableColumn="syncLagSeconds">Sync Lag <p-sortIcon field="syncLagSeconds" /></th>
              </tr>
            </ng-template>

            <ng-template pTemplate="body" let-agent>
              <tr class="clickable-row" tabindex="0" (click)="navigateToDetail(agent)" (keydown.enter)="navigateToDetail(agent)">
                <td>
                  <div class="site-cell">
                    <strong>{{ agent.siteCode }}</strong>
                    @if (agent.siteName) { <span class="site-name">{{ agent.siteName }}</span> }
                  </div>
                </td>
                <td>
                  <span class="conn-badge" [class]="connectivityClass(agent.connectivityState)">
                    {{ connectivityLabel(agent.connectivityState) }}
                  </span>
                </td>
                <td>{{ agent.bufferDepth ?? '—' }}</td>
                <td>{{ agent.lastSeenAt | utcDate:'short' }}</td>
                <td>
                  @if (agent.batteryPercent !== null) {
                    <span [class]="batteryClass(agent.batteryPercent)">
                      {{ agent.batteryPercent }}%
                      @if (agent.isCharging) { <i class="pi pi-bolt"></i> }
                    </span>
                  } @else { — }
                </td>
                <td><code>{{ agent.agentVersion }}</code></td>
                <td [class]="lagClass(agent.syncLagSeconds)">{{ formatLag(agent.syncLagSeconds) }}</td>
              </tr>
            </ng-template>

            <ng-template pTemplate="emptymessage">
              <tr>
                <td colspan="7">
                  <app-empty-state
                    icon="pi-check-circle"
                    title="No online agents match your filters"
                    description="Try clearing the filters or check the offline section above."
                  />
                </td>
              </tr>
            </ng-template>
          </p-table>

          @if (hasMore()) {
            <div class="load-more-row">
              <p-button
                [label]="loadingMore() ? 'Loading…' : 'Load More Agents'"
                icon="pi pi-chevron-down"
                severity="secondary"
                [loading]="loadingMore()"
                [disabled]="loadingMore()"
                (onClick)="loadMore()"
              />
              @if (totalCount() !== null) {
                <span class="load-more-hint">
                  {{ totalCount()! - agents().length }} more agent(s) not yet loaded
                </span>
              }
            </div>
          }
        </p-card>
      }
    </div>
  `,
  styles: [`
    :host { display: block; padding: 1.5rem; }

    .page-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      margin-bottom: 1.25rem;
      flex-wrap: wrap;
      gap: 1rem;
    }
    .page-title {
      font-size: 1.5rem;
      font-weight: 700;
      margin: 0;
      display: flex;
      align-items: center;
      gap: 0.5rem;
      color: var(--p-text-color, #1e293b);
    }
    .header-actions {
      display: flex;
      align-items: center;
      gap: 0.5rem;
    }
    .entity-selector { min-width: 240px; }

    .filters-panel { margin-bottom: 1rem; }
    .filters-row {
      display: flex;
      flex-wrap: wrap;
      gap: 0.75rem 1rem;
      align-items: flex-end;
    }
    .filter-field {
      display: flex;
      flex-direction: column;
      gap: 0.25rem;
      min-width: 180px;
    }
    .filter-field label {
      font-size: 0.78rem;
      font-weight: 600;
      color: var(--p-text-muted-color, #64748b);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    .filter-field--action { align-self: flex-end; }

    /* Connectivity badges */
    .conn-badge {
      display: inline-block;
      padding: 0.2rem 0.6rem;
      border-radius: 9999px;
      font-size: 0.75rem;
      font-weight: 600;
      white-space: nowrap;
    }
    .badge-online           { background: #dcfce7; color: #15803d; }
    .badge-internet-down    { background: #fef9c3; color: #a16207; }
    .badge-fcc-unreachable  { background: #ffedd5; color: #c2410c; }
    .badge-offline          { background: #fee2e2; color: #dc2626; }
    .badge-unknown          { background: #f1f5f9; color: #64748b; }

    /* Offline section */
    .offline-section { margin-bottom: 1rem; border: 2px solid #fca5a5; }
    .offline-header {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      padding: 0.75rem 1rem;
      background: #fef2f2;
      color: #dc2626;
      font-weight: 700;
      border-radius: 0.375rem 0.375rem 0 0;
    }

    .table-card { margin-top: 0; }
    .card-header-row {
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 0.75rem 1rem;
      font-weight: 600;
    }
    .agent-count {
      font-size: 0.8rem;
      font-weight: 400;
      color: var(--p-text-muted-color, #64748b);
    }
    .refresh-note {
      font-size: 0.75rem;
      color: var(--p-text-muted-color, #64748b);
      font-weight: 400;
    }

    .load-more-row {
      display: flex;
      align-items: center;
      gap: 1rem;
      padding: 1rem;
      border-top: 1px solid var(--p-surface-border, #e2e8f0);
    }
    .load-more-hint {
      font-size: 0.8rem;
      color: var(--p-text-muted-color, #64748b);
    }

    .clickable-row { cursor: pointer; }

    .site-cell { display: flex; flex-direction: column; gap: 0.1rem; }
    .site-name { font-size: 0.78rem; color: var(--p-text-muted-color, #64748b); }

    .battery-ok      { color: #16a34a; }
    .battery-low     { color: #d97706; }
    .battery-critical { color: #dc2626; font-weight: 600; }

    .lag-ok      {}
    .lag-warn    { color: #d97706; }
    .lag-danger  { color: #dc2626; font-weight: 600; }

    .loading-msg {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      padding: 2rem;
      color: var(--p-text-muted-color, #64748b);
      justify-content: center;
    }
    .error-msg {
      padding: 1rem;
      background: #fef2f2;
      border: 1px solid #fca5a5;
      border-radius: 0.375rem;
      color: #dc2626;
      margin-bottom: 1rem;
      display: flex;
      align-items: center;
      gap: 0.5rem;
    }
    .error-msg .link-btn {
      cursor: pointer;
      text-decoration: underline;
      color: inherit;
      background: none;
      border: none;
      padding: 0;
      font: inherit;
    }

    code { font-family: monospace; font-size: 0.78rem; }
  `],
})
export class AgentListComponent {
  private readonly agentService = inject(AgentService);
  private readonly masterDataService = inject(MasterDataService);
  private readonly msal = inject(MsalService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly isAdmin = computed(() => {
    const account = getCurrentAccount(this.msal.instance);
    return account ? hasAnyRequiredRole(account, ['SystemAdmin', 'SystemAdministrator']) : false;
  });

  // ── Legal entity ─────────────────────────────────────────────────────────
  private readonly legalEntities = signal<LegalEntity[]>([]);
  readonly selectedLegalEntityId = signal<string | null>(null);
  readonly legalEntityOptions = computed(() =>
    this.legalEntities().map((e) => ({ label: `${e.name} (${e.code})`, value: e.id })),
  );

  // ── Agent data ────────────────────────────────────────────────────────────
  readonly agents = signal<AgentHealthSummary[]>([]);
  readonly loading = signal(false);
  readonly loadingMore = signal(false);
  readonly error = signal(false);
  readonly hasMore = signal(false);
  readonly totalCount = signal<number | null>(null);
  readonly legalEntitiesError = signal(false);
  private readonly nextCursor = signal<string | null>(null);

  // ── Filters — now sent to backend as query params ─────────────────────────
  filters: AgentFilters = { siteCode: '', connectivityState: null };

  // ── Offline/online split from loaded pages (client-side categorisation) ───
  readonly filteredOffline = computed(() => this.agents().filter(isAgentOffline));
  readonly filteredOnline  = computed(() => this.agents().filter((a) => !isAgentOffline(a)));

  // ── Refresh triggers ──────────────────────────────────────────────────────
  private readonly immediateRefresh$ = new Subject<void>();
  private readonly debouncedRefresh$ = new Subject<void>();
  private readonly loadMore$         = new Subject<void>();

  readonly connectivityOptions = [
    { label: 'Online',          value: ConnectivityState.FULLY_ONLINE },
    { label: 'Internet Down',   value: ConnectivityState.INTERNET_DOWN },
    { label: 'FCC Unreachable', value: ConnectivityState.FCC_UNREACHABLE },
    { label: 'Offline',         value: ConnectivityState.FULLY_OFFLINE },
  ];

  constructor() {
    this.masterDataService
      .getLegalEntities()
      .pipe(takeUntilDestroyed())
      .subscribe({
        next: (entities) => this.legalEntities.set(entities),
        error: () => this.legalEntitiesError.set(true),
      });

    // First-page load: immediate or debounced refresh → switchMap cancels in-flight request
    merge(
      this.immediateRefresh$,
      this.debouncedRefresh$.pipe(debounceTime(400)),
    ).pipe(
      switchMap(() => {
        const entityId = this.selectedLegalEntityId();
        if (!entityId) return EMPTY;
        this.loading.set(true);
        this.error.set(false);
        this.agents.set([]);
        this.nextCursor.set(null);
        this.hasMore.set(false);
        this.totalCount.set(null);
        return this.agentService.getAgents({
          legalEntityId: entityId,
          pageSize: PAGE_SIZE,
          siteCode: this.filters.siteCode || undefined,
          connectivityState: this.filters.connectivityState ?? undefined,
        }).pipe(
          catchError(() => {
            this.error.set(true);
            this.loading.set(false);
            return EMPTY;
          }),
        );
      }),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe((result) => {
      this.agents.set(result.data);
      this.hasMore.set(result.meta.hasMore);
      this.nextCursor.set(result.meta.nextCursor);
      this.totalCount.set(result.meta.totalCount);
      this.loading.set(false);
    });

    // Load more: exhaustMap prevents concurrent fetches if the button is clicked rapidly
    this.loadMore$.pipe(
      exhaustMap(() => {
        const entityId = this.selectedLegalEntityId();
        const cursor   = this.nextCursor();
        if (!entityId || !cursor) return EMPTY;
        this.loadingMore.set(true);
        return this.agentService.getAgents({
          legalEntityId: entityId,
          pageSize: PAGE_SIZE,
          cursor,
          siteCode: this.filters.siteCode || undefined,
          connectivityState: this.filters.connectivityState ?? undefined,
        }).pipe(
          catchError(() => {
            this.loadingMore.set(false);
            return EMPTY;
          }),
        );
      }),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe((result) => {
      this.agents.update((current) => [...current, ...result.data]);
      this.hasMore.set(result.meta.hasMore);
      this.nextCursor.set(result.meta.nextCursor);
      this.loadingMore.set(false);
    });

    // Auto-refresh every 30 seconds (skip when tab is hidden) — resets to page 1
    interval(30_000)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => { if (!document.hidden) this.immediateRefresh$.next(); });
  }

  onLegalEntityChange(entityId: string | null): void {
    this.selectedLegalEntityId.set(entityId);
    if (entityId) {
      this.immediateRefresh$.next();
    } else {
      this.agents.set([]);
      this.hasMore.set(false);
      this.totalCount.set(null);
      this.nextCursor.set(null);
    }
  }

  onSiteCodeFilterChange(): void {
    this.debouncedRefresh$.next();
  }

  onConnectivityFilterChange(): void {
    this.immediateRefresh$.next();
  }

  clearFilters(): void {
    this.filters = { siteCode: '', connectivityState: null };
    this.immediateRefresh$.next();
  }

  manualRefresh(): void {
    this.immediateRefresh$.next();
  }

  loadMore(): void {
    this.loadMore$.next();
  }

  navigateToDetail(agent: AgentHealthSummary): void {
    this.router.navigate(['/agents', agent.deviceId]);
  }

  navigateToBootstrapToken(): void {
    this.router.navigate(['/agents', 'bootstrap-token']);
  }

  // ── Template helpers ──────────────────────────────────────────────────────

  connectivityClass(state: ConnectivityState | null): string {
    return connectivityCssClass(state);
  }

  connectivityLabel(state: ConnectivityState | null): string {
    return connectivityLabel(state);
  }

  formatLag(seconds: number | null): string {
    return formatLag(seconds);
  }

  batteryClass(pct: number): string {
    if (pct <= 10) return 'battery-critical';
    if (pct <= 25) return 'battery-low';
    return 'battery-ok';
  }

  lagClass(seconds: number | null): string {
    if (seconds === null) return '';
    if (seconds > 3600) return 'lag-danger';
    if (seconds > 300)  return 'lag-warn';
    return 'lag-ok';
  }
}
