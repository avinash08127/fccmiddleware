import { Component, DestroyRef, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subject, EMPTY, interval } from 'rxjs';
import { switchMap, catchError } from 'rxjs/operators';
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

type PrimeSeverity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

/** Offline threshold: 5 minutes without a heartbeat */
const OFFLINE_THRESHOLD_MS = 5 * 60 * 1000;

function isAgentOffline(agent: AgentHealthSummary): boolean {
  if (agent.connectivityState === ConnectivityState.FULLY_OFFLINE) return true;
  if (!agent.lastSeenAt) return true;
  return Date.now() - new Date(agent.lastSeenAt).getTime() > OFFLINE_THRESHOLD_MS;
}

function connectivitySeverity(state: ConnectivityState | null): PrimeSeverity {
  switch (state) {
    case ConnectivityState.FULLY_ONLINE:     return 'success';
    case ConnectivityState.INTERNET_DOWN:    return 'warn';
    case ConnectivityState.FCC_UNREACHABLE:  return 'warn';
    case ConnectivityState.FULLY_OFFLINE:    return 'danger';
    default:                                 return 'secondary';
  }
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
              <label>Site Code</label>
              <input
                pInputText
                [(ngModel)]="filters.siteCode"
                placeholder="Search site code…"
                (ngModelChange)="onFiltersChange()"
              />
            </div>
            <div class="filter-field">
              <label>Connectivity State</label>
              <p-select
                [options]="connectivityOptions"
                [(ngModel)]="filters.connectivityState"
                placeholder="All States"
                [showClear]="true"
                (ngModelChange)="onFiltersChange()"
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
            Failed to load agents. <a (click)="manualRefresh()">Retry</a>
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
                <tr class="clickable-row" (click)="navigateToDetail(agent)">
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

        <!-- All Agents Table -->
        <p-card styleClass="table-card">
          <ng-template pTemplate="header">
            <div class="card-header-row">
              <span>All Agents</span>
              <span class="refresh-note">Auto-refreshes every 30 s</span>
            </div>
          </ng-template>

          <p-table
            [value]="filteredOnline()"
            sortMode="single"
            [paginator]="true"
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
              <tr class="clickable-row" (click)="navigateToDetail(agent)">
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
    .refresh-note {
      font-size: 0.75rem;
      color: var(--p-text-muted-color, #64748b);
      font-weight: 400;
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
    .error-msg a { cursor: pointer; text-decoration: underline; color: inherit; }

    code { font-family: monospace; font-size: 0.78rem; }
  `],
})
export class AgentListComponent {
  private readonly agentService = inject(AgentService);
  private readonly masterDataService = inject(MasterDataService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  // ── Legal entity ─────────────────────────────────────────────────────────
  private readonly legalEntities = signal<LegalEntity[]>([]);
  readonly selectedLegalEntityId = signal<string | null>(null);
  readonly legalEntityOptions = computed(() =>
    this.legalEntities().map((e) => ({ label: `${e.name} (${e.code})`, value: e.id })),
  );

  // ── Agent data ────────────────────────────────────────────────────────────
  readonly agents = signal<AgentHealthSummary[]>([]);
  readonly loading = signal(false);
  readonly error = signal(false);

  // ── Filters ───────────────────────────────────────────────────────────────
  filters: AgentFilters = { siteCode: '', connectivityState: null };
  private activeFilters = signal<AgentFilters>({ siteCode: '', connectivityState: null });

  // ── Computed: split offline vs online after applying filters ──────────────
  private readonly allFiltered = computed(() => {
    const f = this.activeFilters();
    return this.agents().filter((a) => {
      if (f.siteCode && !a.siteCode.toLowerCase().includes(f.siteCode.toLowerCase())) return false;
      if (f.connectivityState && a.connectivityState !== f.connectivityState) return false;
      return true;
    });
  });

  readonly filteredOffline = computed(() => this.allFiltered().filter(isAgentOffline));
  readonly filteredOnline  = computed(() => this.allFiltered().filter((a) => !isAgentOffline(a)));

  // ── Load trigger ──────────────────────────────────────────────────────────
  private readonly refresh$ = new Subject<void>();

  readonly connectivityOptions = [
    { label: 'Online',         value: ConnectivityState.FULLY_ONLINE },
    { label: 'Internet Down',  value: ConnectivityState.INTERNET_DOWN },
    { label: 'FCC Unreachable',value: ConnectivityState.FCC_UNREACHABLE },
    { label: 'Offline',        value: ConnectivityState.FULLY_OFFLINE },
  ];

  constructor() {
    this.masterDataService
      .getLegalEntities()
      .pipe(takeUntilDestroyed())
      .subscribe({ next: (entities) => this.legalEntities.set(entities) });

    this.refresh$
      .pipe(
        switchMap(() => {
          const entityId = this.selectedLegalEntityId();
          if (!entityId) return EMPTY;
          this.loading.set(true);
          this.error.set(false);
          return this.agentService.getAgents({ legalEntityId: entityId, pageSize: 500 }).pipe(
            catchError(() => {
              this.error.set(true);
              this.loading.set(false);
              return EMPTY;
            }),
          );
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((result) => {
        this.agents.set(result.data);
        this.loading.set(false);
      });

    // Auto-refresh every 30 seconds
    interval(30_000)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.refresh$.next());
  }

  onLegalEntityChange(entityId: string | null): void {
    this.selectedLegalEntityId.set(entityId);
    this.agents.set([]);
    if (entityId) this.refresh$.next();
  }

  onFiltersChange(): void {
    this.activeFilters.set({ ...this.filters });
  }

  clearFilters(): void {
    this.filters = { siteCode: '', connectivityState: null };
    this.activeFilters.set({ ...this.filters });
  }

  manualRefresh(): void {
    this.refresh$.next();
  }

  navigateToDetail(agent: AgentHealthSummary): void {
    this.router.navigate(['/agents', agent.deviceId]);
  }

  // ── Template helpers ──────────────────────────────────────────────────────

  connectivityClass(state: ConnectivityState | null): string {
    return connectivityCssClass(state);
  }

  connectivityLabel(state: ConnectivityState | null): string {
    return connectivityLabel(state);
  }

  connectivitySeverity(state: ConnectivityState | null): PrimeSeverity {
    return connectivitySeverity(state);
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
