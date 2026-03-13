import { Component, DestroyRef, inject, signal, computed, OnInit } from '@angular/core';
import { CommonModule, DOCUMENT } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { forkJoin, interval, EMPTY, of, fromEvent } from 'rxjs';
import { catchError, filter, map, switchMap } from 'rxjs/operators';
import { Subject } from 'rxjs';
import { CardModule } from 'primeng/card';
import { ButtonModule } from 'primeng/button';
import { DividerModule } from 'primeng/divider';
import { ProgressBarModule } from 'primeng/progressbar';
import { TimelineModule } from 'primeng/timeline';
import { TagModule } from 'primeng/tag';
import { TableModule } from 'primeng/table';
import { SkeletonModule } from 'primeng/skeleton';

import { AgentService, DiagnosticLogBatch } from '../../core/services/agent.service';
import {
  AgentAuditEvent,
  AgentRegistration,
  AgentTelemetry,
  ConnectivityState,
} from '../../core/models/agent.model';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { UtcDatePipe } from '../../shared/pipes/utc-date.pipe';
import { RoleVisibleDirective } from '../../shared/directives/role-visible.directive';

// ── GUID validation ─────────────────────────────────────────────────────────

const GUID_REGEX = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function isValidGuid(value: string): boolean {
  return GUID_REGEX.test(value);
}

// ── FM-S05: Defense-in-depth HTML sanitization for untrusted log entries ─────

const HTML_TAG_REGEX = /<[^>]*>/g;

function sanitizeLogEntry(entry: string): string {
  return entry.replace(HTML_TAG_REGEX, '');
}

// ── Redaction detection (F08-09) ─────────────────────────────────────────────

const REDACTED_HOST = '***';

function isFccHostRedacted(host: string | null | undefined): boolean {
  return host === REDACTED_HOST;
}

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
  if (seconds === null) return '\u2014';
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
    RoleVisibleDirective,
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
              {{ registration()?.siteCode ?? telemetry()?.siteCode ?? 'Loading\u2026' }}
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
          <!-- F08-08: Decommission button for admin roles -->
          <ng-container *appRoleVisible="['FccAdmin', 'FccUser']">
            @if (registration() && registration()!.status !== 'DEACTIVATED') {
              <p-button
                label="Decommission"
                icon="pi pi-power-off"
                severity="danger"
                [outlined]="true"
                [loading]="decommissioning()"
                (onClick)="confirmDecommission()"
              />
            }
            @if (registration()?.status === 'DEACTIVATED') {
              <span class="decommissioned-badge">
                <i class="pi pi-ban"></i> Decommissioned
              </span>
            }
          </ng-container>
          <span class="refresh-note"><i class="pi pi-refresh"></i> Telemetry: 30s | Full: 3 min</span>
          <p-button
            icon="pi pi-refresh"
            severity="secondary"
            [rounded]="true"
            [text]="true"
            (onClick)="triggerRefresh()"
          />
        </div>
      </div>

      @if (loading() && !telemetry() && !registration()) {
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
      } @else if (registration() || telemetry()) {

        <!-- ── Row 1: Status + FCC Connection ─── -->
        <div class="cards-grid">

          <!-- Current Status Card -->
          @if (telemetry()) {
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
          }

          <!-- FCC Connection Card (F08-09: detect redacted data) -->
          @if (telemetry()) {
            @if (isFccRedacted()) {
              <p-card header="FCC Connection" styleClass="detail-card">
                <div class="restricted-notice">
                  <i class="pi pi-lock"></i>
                  <span>Restricted</span>
                  <p>FCC connection details are restricted for your role. Contact an administrator for full access.</p>
                </div>
                <p-divider />
                <div class="stat-row">
                  <span class="stat-label">Vendor</span>
                  <strong>{{ telemetry()!.fccHealth.fccVendor }}</strong>
                </div>
                <div class="stat-row">
                  <span class="stat-label">FCC Reachable</span>
                  @if (telemetry()!.fccHealth.isReachable) {
                    <span class="text-success"><i class="pi pi-check-circle"></i> Yes</span>
                  } @else {
                    <span class="text-danger"><i class="pi pi-times-circle"></i> No</span>
                  }
                </div>
              </p-card>
            } @else {
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
            }
          }

          <!-- Telemetry Card -->
          @if (telemetry()) {
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
          }

          <!-- Sync Status Card -->
          @if (telemetry()) {
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
                <code>{{ telemetry()!.sync.configVersion ?? '\u2014' }}</code>
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
          }

          <!-- No telemetry notice (F08-05: partial data display) -->
          @if (!telemetry() && registration()) {
            <p-card header="Telemetry" styleClass="detail-card">
              <app-empty-state
                icon="pi-chart-bar"
                title="No telemetry yet"
                description="This agent has not reported telemetry data yet. It may have just been registered."
              />
            </p-card>
          }
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
                    } @else { \u2014 }
                  </td>
                  <td>
                    @if (event.newState) {
                      <span class="conn-badge" [class]="connClass(event.newState)">
                        {{ connLabel(event.newState) }}
                      </span>
                    } @else { \u2014 }
                  </td>
                </tr>
              </ng-template>
            </p-table>
          }
        </p-card>

        <!-- ── Diagnostic Logs (from remote upload) ─── -->
        <p-card header="Diagnostic Logs (Remote)" styleClass="diag-logs-card">
          @if (diagnosticLogs().length === 0) {
            <app-empty-state
              icon="pi-file"
              title="No diagnostic logs"
              description="No diagnostic log batches uploaded from this device. Enable includeDiagnosticsLogs in config."
            />
          } @else {
            @for (batch of diagnosticLogs(); track batch.id) {
              <div class="diag-batch">
                <div class="diag-batch-header">
                  <strong>Batch {{ batch.id | slice:0:8 }}</strong>
                  <span class="text-muted"> uploaded {{ batch.uploadedAtUtc | utcDate:'short' }}</span>
                  <span class="text-muted"> ({{ batch.logEntries.length }} entries)</span>
                </div>
                <pre class="diag-log-entries">{{ batch.logEntries.join('\\n') }}</pre>
              </div>
            }
          }
          <p-button
            label="Refresh Logs"
            icon="pi pi-refresh"
            severity="secondary"
            [text]="true"
            (onClick)="loadDiagnosticLogs()"
          />
        </p-card>
      }
    </div>
  `,
  styles: [`
    :host { display: block; padding: 1.5rem; }

    .diag-batch { margin-bottom: 1rem; }
    .diag-batch-header { margin-bottom: 0.25rem; font-size: 0.85rem; }
    .diag-log-entries {
      background: #f8f9fa;
      border: 1px solid #e9ecef;
      border-radius: 4px;
      padding: 0.5rem;
      font-size: 0.72rem;
      max-height: 300px;
      overflow-y: auto;
      white-space: pre-wrap;
      word-break: break-all;
    }
    .text-muted { color: var(--p-text-muted-color, #64748b); }

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

    /* ── Decommission badge ──────────────────── */
    .decommissioned-badge {
      display: inline-flex;
      align-items: center;
      gap: 0.3rem;
      padding: 0.3rem 0.75rem;
      border-radius: 9999px;
      font-size: 0.8rem;
      font-weight: 600;
      background: #fee2e2;
      color: #dc2626;
    }

    /* ── Restricted notice (F08-09) ──────────── */
    .restricted-notice {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 0.4rem;
      padding: 0.5rem 0.75rem;
      border-radius: 0.375rem;
      background: #fef9c3;
      color: #a16207;
      font-size: 0.8rem;
      font-weight: 600;
    }
    .restricted-notice p {
      width: 100%;
      margin: 0.2rem 0 0;
      font-weight: 400;
      font-size: 0.75rem;
    }

    /* ── Utilities ──────────────────────────── */
    .env-badge {
      display: inline-block;
      font-size: 0.7rem;
      font-weight: 600;
      text-transform: uppercase;
      letter-spacing: 0.04em;
      background: var(--p-surface-100, #f1f5f9);
      color: var(--p-text-muted-color, #475569);
      padding: 0.1rem 0.4rem;
      border-radius: 4px;
    }
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
  private readonly document    = inject(DOCUMENT);

  readonly registration  = signal<AgentRegistration | null>(null);
  readonly telemetry     = signal<AgentTelemetry | null>(null);
  readonly events        = signal<AgentAuditEvent[]>([]);
  readonly diagnosticLogs = signal<DiagnosticLogBatch[]>([]);
  readonly loading       = signal(true);
  readonly error         = signal(false);
  readonly decommissioning = signal(false);

  private agentId = '';
  private readonly refresh$ = new Subject<void>();
  private readonly telemetryRefresh$ = new Subject<void>();

  // ── F08-09: Detect redacted FCC host ───────────────────────────────────────
  readonly isFccRedacted = computed(() => {
    const t = this.telemetry();
    return t ? isFccHostRedacted(t.fccHealth.fccHost) : false;
  });

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

    // F08-02: Validate GUID before making API calls
    if (!isValidGuid(this.agentId)) {
      this.router.navigate(['/agents']);
      return;
    }

    // F08-05: Handle each observable's error independently
    this.refresh$
      .pipe(
        switchMap(() => {
          this.loading.set(true);
          this.error.set(false);
          return forkJoin({
            registration: this.loadRegistration(),
            telemetry: this.loadTelemetry(),
            events: this.loadEvents(),
          });
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((data) => {
        if (data.registration === null && data.telemetry === null && !this.registration()) {
          this.error.set(true);
        } else {
          this.registration.set(data.registration);
          this.telemetry.set(data.telemetry);
          this.events.set(data.events);
        }
        this.loading.set(false);
      });

    // FM-P02: Telemetry-only refresh — avoids redundant registration/events fetches on the 30s cycle
    this.telemetryRefresh$
      .pipe(
        switchMap(() => this.loadTelemetry()),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((telemetry) => {
        if (telemetry !== null) {
          this.telemetry.set(telemetry);
        }
      });

    // Initial load; diagnostic logs are on-demand only (not refreshed on the auto-refresh cycle)
    this.triggerRefresh();
    this.loadDiagnosticLogs();

    // FM-P02: 30s interval — telemetry only when tab is visible
    interval(30_000)
      .pipe(
        filter(() => this.document.visibilityState === 'visible'),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => this.telemetryRefresh$.next());

    // FM-P02: 3-minute interval — full refresh (registration + telemetry + events) when tab is visible
    interval(3 * 60_000)
      .pipe(
        filter(() => this.document.visibilityState === 'visible'),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => this.triggerRefresh());

    // F08-04: Resume with a full refresh immediately when tab becomes visible again
    fromEvent(this.document, 'visibilitychange')
      .pipe(
        filter(() => this.document.visibilityState === 'visible'),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => this.triggerRefresh());
  }

  triggerRefresh(): void {
    this.refresh$.next();
  }

  goBack(): void {
    this.router.navigate(['/agents']);
  }

  // F08-07: Managed subscription with takeUntilDestroyed
  loadDiagnosticLogs(): void {
    this.agentService.getAgentDiagnosticLogs(this.agentId)
      .pipe(
        catchError(() => EMPTY),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((response) => {
        // FM-S05: Defense-in-depth — strip HTML tags from untrusted edge device log entries
        const sanitized = response.batches.map(batch => ({
          ...batch,
          logEntries: batch.logEntries.map(sanitizeLogEntry),
        }));
        this.diagnosticLogs.set(sanitized);
      });
  }

  // F08-08 + FM-S04: Decommission with confirmation and required reason
  confirmDecommission(): void {
    const reg = this.registration();
    if (!reg) return;

    const reason = window.prompt(
      `Decommissioning device "${reg.deviceId}" (${reg.siteCode}) is irreversible.\n\nPlease provide a reason (at least 10 characters):`,
    );
    if (reason === null) return; // cancelled
    if (reason.trim().length < 10) {
      window.alert('Reason must be at least 10 characters.');
      return;
    }

    this.decommissioning.set(true);
    this.agentService.decommissionAgent(reg.deviceId, reason.trim())
      .pipe(
        catchError(() => {
          this.decommissioning.set(false);
          return EMPTY;
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => {
        this.decommissioning.set(false);
        this.triggerRefresh();
      });
  }

  private loadRegistration() {
    return this.agentService.getAgentById(this.agentId).pipe(
      catchError(() => of(this.registration())),
    );
  }

  private loadTelemetry() {
    return this.agentService.getAgentTelemetry(this.agentId).pipe(
      catchError((error: HttpErrorResponse) => {
        if (error.status === 404) {
          return of(null);
        }

        return of(this.telemetry());
      }),
    );
  }

  private loadEvents() {
    return this.agentService.getAgentEvents(this.agentId, 20).pipe(
      map((events) => events ?? []),
      catchError(() => of(this.events())),
    );
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
