import { Component, DestroyRef, inject, signal, computed, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { forkJoin, interval, EMPTY } from 'rxjs';
import { catchError, switchMap } from 'rxjs/operators';
import { Subject } from 'rxjs';
import { CardModule } from 'primeng/card';
import { ButtonModule } from 'primeng/button';
import { DividerModule } from 'primeng/divider';
import { ProgressBarModule } from 'primeng/progressbar';
import { TimelineModule } from 'primeng/timeline';
import { TagModule } from 'primeng/tag';
import { TableModule } from 'primeng/table';
import { SkeletonModule } from 'primeng/skeleton';

import { AgentService } from '../../core/services/agent.service';
import {
  AgentAuditEvent,
  AgentRegistration,
  AgentTelemetry,
  ConnectivityState,
} from '../../core/models/agent.model';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { UtcDatePipe } from '../../shared/pipes/utc-date.pipe';

// ── Connectivity helpers ─────────────────────────────────────────────────────

function connectivityCssClass(state: string | null): string {
  switch (state) {
    case ConnectivityState.FULLY_ONLINE:    return 'badge-online';
    case ConnectivityState.INTERNET_DOWN:   return 'badge-internet-down';
    case ConnectivityState.FCC_UNREACHABLE: return 'badge-fcc-unreachable';
    case ConnectivityState.FULLY_OFFLINE:   return 'badge-offline';
    default:                                return 'badge-unknown';
  }
}

function connectivityLabel(state: string | null): string {
  switch (state) {
    case ConnectivityState.FULLY_ONLINE:    return 'Fully Online';
    case ConnectivityState.INTERNET_DOWN:   return 'Internet Down';
    case ConnectivityState.FCC_UNREACHABLE: return 'FCC Unreachable';
    case ConnectivityState.FULLY_OFFLINE:   return 'Fully Offline';
    default:                                return state ?? 'Unknown';
  }
}

function connectivityIcon(state: string | null): string {
  switch (state) {
    case ConnectivityState.FULLY_ONLINE:    return 'pi pi-check-circle';
    case ConnectivityState.INTERNET_DOWN:   return 'pi pi-wifi';
    case ConnectivityState.FCC_UNREACHABLE: return 'pi pi-exclamation-triangle';
    case ConnectivityState.FULLY_OFFLINE:   return 'pi pi-times-circle';
    default:                                return 'pi pi-question-circle';
  }
}

function formatLag(seconds: number | null): string {
  if (seconds === null) return '—';
  if (seconds < 60) return `${seconds}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
  return `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m`;
}

function formatUptime(seconds: number): string {
  const d = Math.floor(seconds / 86400);
  const h = Math.floor((seconds % 86400) / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  if (d > 0) return `${d}d ${h}h`;
  if (h > 0) return `${h}h ${m}m`;
  return `${m}m`;
}

function formatStorage(mb: number): string {
  if (mb >= 1024) return `${(mb / 1024).toFixed(1)} GB`;
  return `${mb} MB`;
}

interface TimelineEvent {
  state: string;
  label: string;
  icon: string;
  cssClass: string;
  occurredAt: string;
  description: string;
}

@Component({
  selector: 'app-agent-detail',
  standalone: true,
  imports: [
    CommonModule,
    RouterModule,
    CardModule,
    ButtonModule,
    DividerModule,
    ProgressBarModule,
    TimelineModule,
    TagModule,
    TableModule,
    SkeletonModule,
    EmptyStateComponent,
    UtcDatePipe,
  ],
  template: `
    <div class="page-container">

      <!-- Header -->
      <div class="page-header">
        <div class="header-left">
          <p-button
            icon="pi pi-arrow-left"
            severity="secondary"
            [text]="true"
            (onClick)="goBack()"
            pTooltip="Back to agent list"
          />
          <div>
            <h1 class="page-title">
              <i class="pi pi-server"></i>
              {{ registration()?.siteCode ?? 'Loading…' }}
            </h1>
            @if (registration()) {
              <p class="page-subtitle">
                Device: <code>{{ registration()!.deviceId }}</code>
                &nbsp;&bull;&nbsp; Registered {{ registration()!.registeredAt | utcDate:'mediumDate' }}
              </p>
            }
          </div>
        </div>
        <div class="header-right">
          <span class="refresh-note"><i class="pi pi-refresh"></i> Auto-refresh 30s</span>
          <p-button
            icon="pi pi-refresh"
            severity="secondary"
            [rounded]="true"
            [text]="true"
            (onClick)="triggerRefresh()"
          />
        </div>
      </div>

      @if (loading() && !telemetry()) {
        <!-- Loading skeletons -->
        <div class="cards-grid">
          @for (_ of [1,2,3,4]; track $index) {
            <p-card>
              <p-skeleton height="120px" />
            </p-card>
          }
        </div>
      } @else if (error()) {
        <app-empty-state
          icon="pi-exclamation-circle"
          title="Failed to load agent data"
          description="Check the device ID and try again."
        />
      } @else if (telemetry()) {

        <!-- ── Row 1: Status + FCC Connection ─── -->
        <div class="cards-grid">

          <!-- Current Status Card -->
          <p-card header="Current Status" styleClass="detail-card">
            <div class="stat-row">
              <span class="stat-label">Connectivity</span>
              <span class="conn-badge" [class]="connClass(telemetry()!.connectivityState)">
                <i [class]="connIcon(telemetry()!.connectivityState)"></i>
                {{ connLabel(telemetry()!.connectivityState) }}
              </span>
            </div>
            <p-divider />
            <div class="stat-row">
              <span class="stat-label">Uptime</span>
              <span>{{ formatUptime(telemetry()!.device.appUptimeSeconds) }}</span>
            </div>
            <div class="stat-row">
              <span class="stat-label">Last Heartbeat</span>
              <span>{{ telemetry()!.fccHealth.lastHeartbeatAtUtc | utcDate:'short' }}</span>
            </div>
            <div class="stat-row">
              <span class="stat-label">Reported At</span>
              <span>{{ telemetry()!.reportedAtUtc | utcDate:'short' }}</span>
            </div>
            <div class="stat-row">
              <span class="stat-label">OS</span>
              <span>{{ telemetry()!.device.osVersion }}</span>
            </div>
            <div class="stat-row">
              <span class="stat-label">Device Model</span>
              <span>{{ telemetry()!.device.deviceModel }}</span>
            </div>
          </p-card>

          <!-- FCC Connection Card -->
          <p-card header="FCC Connection" styleClass="detail-card">
            <div class="stat-row">
              <span class="stat-label">Vendor</span>
              <strong>{{ telemetry()!.fccHealth.fccVendor }}</strong>
            </div>
            <div class="stat-row">
              <span class="stat-label">Host</span>
              <code>{{ telemetry()!.fccHealth.fccHost }}:{{ telemetry()!.fccHealth.fccPort }}</code>
            </div>
            <p-divider />
            <div class="stat-row">
              <span class="stat-label">FCC Reachable</span>
              @if (telemetry()!.fccHealth.isReachable) {
                <span class="text-success"><i class="pi pi-check-circle"></i> Yes</span>
              } @else {
                <span class="text-danger"><i class="pi pi-times-circle"></i> No</span>
              }
            </div>
            <div class="stat-row">
              <span class="stat-label">Last Heartbeat</span>
              <span>{{ telemetry()!.fccHealth.lastHeartbeatAtUtc | utcDate:'short' }}</span>
            </div>
            <div class="stat-row">
              <span class="stat-label">Heartbeat Age</span>
              <span [class]="heartbeatAgeClass(telemetry()!.fccHealth.heartbeatAgeSeconds)">
                {{ formatLag(telemetry()!.fccHealth.heartbeatAgeSeconds) }}
              </span>
            </div>
            <div class="stat-row">
              <span class="stat-label">Consecutive Failures</span>
              <span [class]="failureClass(telemetry()!.fccHealth.consecutiveHeartbeatFailures)">
                {{ telemetry()!.fccHealth.consecutiveHeartbeatFailures }}
              </span>
            </div>
          </p-card>

          <!-- Telemetry Card -->
          <p-card header="Device & Buffer" styleClass="detail-card">
            <div class="stat-label-top">Battery</div>
            <div class="battery-row">
              <p-progressBar
                [value]="telemetry()!.device.batteryPercent"
                [showValue]="true"
                styleClass="battery-bar"
                [class]="batteryBarClass(telemetry()!.device.batteryPercent)"
              />
              @if (telemetry()!.device.isCharging) {
                <i class="pi pi-bolt text-success" pTooltip="Charging"></i>
              }
            </div>
            <p-divider />
            <div class="stat-row">
              <span class="stat-label">Storage Free</span>
              <span>{{ formatStorage(telemetry()!.device.storageFreeMb) }}
                / {{ formatStorage(telemetry()!.device.storageTotalMb) }}</span>
            </div>
            <p-divider />
            <div class="stat-label-top">Buffer Breakdown</div>
            <div class="buffer-grid">
              <div class="buffer-item">
                <span class="buffer-count">{{ telemetry()!.buffer.pendingUploadCount }}</span>
                <span class="buffer-label">Pending</span>
              </div>
              <div class="buffer-item">
                <span class="buffer-count">{{ telemetry()!.buffer.syncedCount }}</span>
                <span class="buffer-label">Synced</span>
              </div>
              <div class="buffer-item">
                <span class="buffer-count">{{ telemetry()!.buffer.syncedToOdooCount }}</span>
                <span class="buffer-label">Odoo</span>
              </div>
              <div class="buffer-item buffer-item--danger">
                <span class="buffer-count">{{ telemetry()!.buffer.failedCount }}</span>
                <span class="buffer-label">Failed</span>
              </div>
            </div>
            <div class="stat-row" style="margin-top:0.5rem">
              <span class="stat-label">Total Records</span>
              <strong>{{ telemetry()!.buffer.totalRecords }}</strong>
            </div>
            @if (telemetry()!.buffer.oldestPendingAtUtc) {
              <div class="stat-row">
                <span class="stat-label">Oldest Pending</span>
                <span>{{ telemetry()!.buffer.oldestPendingAtUtc | utcDate:'short' }}</span>
              </div>
            }
          </p-card>

          <!-- Sync Status Card -->
          <p-card header="Sync Status" styleClass="detail-card">
            <div class="stat-row">
              <span class="stat-label">Sync Lag</span>
              <strong [class]="lagClass(telemetry()!.sync.syncLagSeconds)">
                {{ formatLag(telemetry()!.sync.syncLagSeconds) }}
              </strong>
            </div>
            <div class="stat-row">
              <span class="stat-label">Last Upload</span>
              <span>{{ telemetry()!.sync.lastSuccessfulSyncUtc | utcDate:'short' }}</span>
            </div>
            <div class="stat-row">
              <span class="stat-label">Last Attempt</span>
              <span>{{ telemetry()!.sync.lastSyncAttemptUtc | utcDate:'short' }}</span>
            </div>
            <p-divider />
            <div class="stat-row">
              <span class="stat-label">Config Version</span>
              <code>{{ telemetry()!.sync.configVersion ?? '—' }}</code>
            </div>
            <div class="stat-row">
              <span class="stat-label">Last Config Pull</span>
              <span>{{ telemetry()!.sync.lastConfigPullUtc | utcDate:'short' }}</span>
            </div>
            <div class="stat-row">
              <span class="stat-label">Batch Size</span>
              <span>{{ telemetry()!.sync.uploadBatchSize }}</span>
            </div>
            <p-divider />
            <div class="stat-label-top">Error Counts</div>
            <div class="error-counts-grid">
              <div class="ec-item" [class.ec-item--warn]="telemetry()!.errorCounts.fccConnectionErrors > 0">
                <span>FCC Conn</span><strong>{{ telemetry()!.errorCounts.fccConnectionErrors }}</strong>
              </div>
              <div class="ec-item" [class.ec-item--warn]="telemetry()!.errorCounts.cloudUploadErrors > 0">
                <span>Upload</span><strong>{{ telemetry()!.errorCounts.cloudUploadErrors }}</strong>
              </div>
              <div class="ec-item" [class.ec-item--warn]="telemetry()!.errorCounts.cloudAuthErrors > 0">
                <span>Auth</span><strong>{{ telemetry()!.errorCounts.cloudAuthErrors }}</strong>
              </div>
              <div class="ec-item" [class.ec-item--warn]="telemetry()!.errorCounts.bufferWriteErrors > 0">
                <span>Buffer</span><strong>{{ telemetry()!.errorCounts.bufferWriteErrors }}</strong>
              </div>
            </div>
          </p-card>
        </div>

        <!-- ── Connectivity Timeline ─── -->
        <p-card header="Connectivity Timeline (last 24 h)" styleClass="timeline-card">
          @if (connectivityEvents().length === 0) {
            <app-empty-state
              icon="pi-history"
              title="No connectivity events in the last 24 h"
              description="The agent has maintained a stable state."
            />
          } @else {
            <p-timeline [value]="connectivityEvents()" align="left" styleClass="conn-timeline">
              <ng-template pTemplate="marker" let-event>
                <span class="timeline-marker" [class]="event.cssClass">
                  <i [class]="event.icon"></i>
                </span>
              </ng-template>
              <ng-template pTemplate="content" let-event>
                <div class="timeline-content">
                  <span class="conn-badge" [class]="event.cssClass">{{ event.label }}</span>
                  <span class="timeline-time">{{ event.occurredAt | utcDate:'short' }}</span>
                  <p class="timeline-desc">{{ event.description }}</p>
                </div>
              </ng-template>
            </p-timeline>
          }
        </p-card>

        <!-- ── Recent Events ─── -->
        <p-card header="Recent Events (last 20)" styleClass="events-card">
          @if (recentEvents().length === 0) {
            <app-empty-state
              icon="pi-list"
              title="No recent events"
              description="No audit events found for this agent."
            />
          } @else {
            <p-table
              [value]="recentEvents()"
              styleClass="p-datatable-sm p-datatable-striped"
            >
              <ng-template pTemplate="header">
                <tr>
                  <th style="width:13rem">Time</th>
                  <th style="width:14rem">Event Type</th>
                  <th>Description</th>
                  <th style="width:9rem">Prev State</th>
                  <th style="width:9rem">New State</th>
                </tr>
              </ng-template>
              <ng-template pTemplate="body" let-event>
                <tr>
                  <td>{{ event.occurredAtUtc | utcDate:'short' }}</td>
                  <td><code>{{ event.eventType }}</code></td>
                  <td>{{ event.description }}</td>
                  <td>
                    @if (event.previousState) {
                      <span class="conn-badge" [class]="connClass(event.previousState)">
                        {{ connLabel(event.previousState) }}
                      </span>
                    } @else { — }
                  </td>
                  <td>
                    @if (event.newState) {
                      <span class="conn-badge" [class]="connClass(event.newState)">
                        {{ connLabel(event.newState) }}
                      </span>
                    } @else { — }
                  </td>
                </tr>
              </ng-template>
            </p-table>
          }
        </p-card>
      }
    </div>
  `,
  styles: [`
    :host { display: block; padding: 1.5rem; }

    .page-header {
      display: flex;
      align-items: flex-start;
      justify-content: space-between;
      margin-bottom: 1.5rem;
      flex-wrap: wrap;
      gap: 1rem;
    }
    .header-left {
      display: flex;
      align-items: flex-start;
      gap: 0.5rem;
    }
    .header-right {
      display: flex;
      align-items: center;
      gap: 0.5rem;
    }
    .page-title {
      font-size: 1.5rem;
      font-weight: 700;
      margin: 0 0 0.25rem;
      display: flex;
      align-items: center;
      gap: 0.5rem;
      color: var(--p-text-color, #1e293b);
    }
    .page-subtitle {
      margin: 0;
      font-size: 0.85rem;
      color: var(--p-text-muted-color, #64748b);
    }
    .refresh-note {
      font-size: 0.75rem;
      color: var(--p-text-muted-color, #64748b);
      display: flex;
      align-items: center;
      gap: 0.25rem;
    }

    /* ── Cards grid ─────────────────────────── */
    .cards-grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
      gap: 1rem;
      margin-bottom: 1rem;
    }
    .detail-card { height: 100%; }
    .timeline-card, .events-card { margin-bottom: 1rem; }

    /* ── Stat rows ──────────────────────────── */
    .stat-row {
      display: flex;
      justify-content: space-between;
      align-items: center;
      padding: 0.3rem 0;
      gap: 0.5rem;
    }
    .stat-label {
      font-size: 0.8rem;
      color: var(--p-text-muted-color, #64748b);
      font-weight: 500;
      white-space: nowrap;
    }
    .stat-label-top {
      font-size: 0.78rem;
      font-weight: 700;
      color: var(--p-text-muted-color, #64748b);
      text-transform: uppercase;
      letter-spacing: 0.04em;
      margin: 0.5rem 0 0.25rem;
    }

    /* ── Connectivity badges ────────────────── */
    .conn-badge {
      display: inline-flex;
      align-items: center;
      gap: 0.3rem;
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

    /* ── Battery ────────────────────────────── */
    .battery-row {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      margin-bottom: 0.25rem;
    }
    .battery-bar { flex: 1; }
    :host ::ng-deep .battery-low    .p-progressbar-value { background: #f59e0b !important; }
    :host ::ng-deep .battery-critical .p-progressbar-value { background: #ef4444 !important; }

    /* ── Buffer grid ────────────────────────── */
    .buffer-grid {
      display: grid;
      grid-template-columns: repeat(4, 1fr);
      gap: 0.5rem;
      margin: 0.25rem 0;
    }
    .buffer-item {
      display: flex;
      flex-direction: column;
      align-items: center;
      background: #f8fafc;
      border-radius: 0.375rem;
      padding: 0.4rem;
    }
    .buffer-item--danger { background: #fef2f2; }
    .buffer-item--danger .buffer-count { color: #dc2626; }
    .buffer-count { font-size: 1.1rem; font-weight: 700; }
    .buffer-label { font-size: 0.68rem; color: var(--p-text-muted-color, #64748b); }

    /* ── Error counts grid ──────────────────── */
    .error-counts-grid {
      display: grid;
      grid-template-columns: repeat(2, 1fr);
      gap: 0.25rem;
      margin-top: 0.25rem;
    }
    .ec-item {
      display: flex;
      justify-content: space-between;
      font-size: 0.8rem;
      padding: 0.2rem 0.4rem;
      border-radius: 0.25rem;
      background: #f8fafc;
    }
    .ec-item--warn { background: #fef2f2; color: #dc2626; }
    .ec-item--warn strong { color: #dc2626; }

    /* ── Timeline ───────────────────────────── */
    .conn-timeline { margin: 0 1rem; }
    .timeline-marker {
      display: flex;
      align-items: center;
      justify-content: center;
      width: 2rem;
      height: 2rem;
      border-radius: 50%;
      font-size: 0.85rem;
    }
    .timeline-content {
      display: flex;
      align-items: baseline;
      flex-wrap: wrap;
      gap: 0.5rem;
      padding-bottom: 0.75rem;
    }
    .timeline-time {
      font-size: 0.78rem;
      color: var(--p-text-muted-color, #64748b);
    }
    .timeline-desc {
      width: 100%;
      margin: 0.1rem 0 0;
      font-size: 0.8rem;
      color: var(--p-text-muted-color, #64748b);
    }

    /* ── Utilities ──────────────────────────── */
    code { font-family: monospace; font-size: 0.78rem; }
    .text-success { color: #16a34a; display: inline-flex; align-items: center; gap: 0.25rem; }
    .text-danger  { color: #dc2626; display: inline-flex; align-items: center; gap: 0.25rem; }
    .lag-warn   { color: #d97706; }
    .lag-danger { color: #dc2626; font-weight: 700; }
    .failure-warn   { color: #d97706; font-weight: 600; }
    .failure-danger { color: #dc2626; font-weight: 700; }
  `],
})
export class AgentDetailComponent implements OnInit {
  private readonly route       = inject(ActivatedRoute);
  private readonly router      = inject(Router);
  private readonly agentService = inject(AgentService);
  private readonly destroyRef  = inject(DestroyRef);

  readonly registration  = signal<AgentRegistration | null>(null);
  readonly telemetry     = signal<AgentTelemetry | null>(null);
  readonly events        = signal<AgentAuditEvent[]>([]);
  readonly loading       = signal(true);
  readonly error         = signal(false);

  private agentId = '';
  private readonly refresh$ = new Subject<void>();

  // ── Derived: connectivity timeline (events with state transitions) ────────
  readonly connectivityEvents = computed<TimelineEvent[]>(() =>
    this.events()
      .filter((e) => e.eventType === 'CONNECTIVITY_STATE_CHANGED')
      .map((e) => ({
        state:       e.newState ?? '',
        label:       connectivityLabel(e.newState),
        icon:        connectivityIcon(e.newState),
        cssClass:    connectivityCssClass(e.newState),
        occurredAt:  e.occurredAtUtc,
        description: e.description,
      })),
  );

  // ── All events for recent events panel ───────────────────────────────────
  readonly recentEvents = computed(() => this.events());

  ngOnInit(): void {
    this.agentId = this.route.snapshot.paramMap.get('id') ?? '';

    this.refresh$
      .pipe(
        switchMap(() => {
          this.loading.set(true);
          this.error.set(false);
          return forkJoin({
            registration: this.agentService.getAgentById(this.agentId),
            telemetry:    this.agentService.getAgentTelemetry(this.agentId),
            events:       this.agentService.getAgentEvents(this.agentId, 20),
          }).pipe(
            catchError(() => {
              this.error.set(true);
              this.loading.set(false);
              return EMPTY;
            }),
          );
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((data) => {
        this.registration.set(data.registration);
        this.telemetry.set(data.telemetry);
        this.events.set(data.events);
        this.loading.set(false);
      });

    // Initial load + auto-refresh every 30 seconds
    this.triggerRefresh();
    interval(30_000)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.triggerRefresh());
  }

  triggerRefresh(): void {
    this.refresh$.next();
  }

  goBack(): void {
    this.router.navigate(['/agents']);
  }

  // ── Template helpers ─────────────────────────────────────────────────────

  connClass(state: string | null): string    { return connectivityCssClass(state); }
  connLabel(state: string | null): string    { return connectivityLabel(state); }
  connIcon(state: string | null): string     { return connectivityIcon(state); }

  formatLag(seconds: number | null): string  { return formatLag(seconds); }
  formatUptime(seconds: number): string      { return formatUptime(seconds); }
  formatStorage(mb: number): string          { return formatStorage(mb); }

  batteryBarClass(pct: number): string {
    if (pct <= 10) return 'battery-critical';
    if (pct <= 25) return 'battery-low';
    return '';
  }

  lagClass(seconds: number | null): string {
    if (seconds === null) return '';
    if (seconds > 3600) return 'lag-danger';
    if (seconds > 300)  return 'lag-warn';
    return '';
  }

  heartbeatAgeClass(seconds: number | null): string {
    if (seconds === null) return '';
    if (seconds > 120) return 'lag-danger';
    if (seconds > 60)  return 'lag-warn';
    return '';
  }

  failureClass(count: number): string {
    if (count >= 3) return 'failure-danger';
    if (count > 0)  return 'failure-warn';
    return '';
  }
}
