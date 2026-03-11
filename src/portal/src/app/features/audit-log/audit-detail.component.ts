import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ButtonModule } from 'primeng/button';
import { CardModule } from 'primeng/card';

import { AuditService } from '../../core/services/audit.service';
import { AuditEvent, EventType } from '../../core/models/audit.model';
import { StatusBadgeComponent } from '../../shared/components/status-badge/status-badge.component';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { UtcDatePipe } from '../../shared/pipes/utc-date.pipe';

type PrimeSeverity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

function eventTypeSeverity(eventType: EventType): PrimeSeverity {
  if (eventType.startsWith('Transaction')) return 'info';
  if (eventType.startsWith('PreAuth')) return 'secondary';
  if (eventType.startsWith('Reconciliation')) return 'warn';
  if (eventType.startsWith('Agent')) return 'success';
  if (eventType === 'ConnectivityChanged' || eventType === 'BufferThresholdExceeded') return 'danger';
  return 'contrast';
}

@Component({
  selector: 'app-audit-detail',
  standalone: true,
  imports: [
    CommonModule,
    ButtonModule,
    CardModule,
    StatusBadgeComponent,
    EmptyStateComponent,
    UtcDatePipe,
  ],
  template: `
    <div class="page-container">
      <!-- Back button -->
      <div class="page-header">
        <p-button
          label="Back to Audit Log"
          icon="pi pi-arrow-left"
          severity="secondary"
          size="small"
          (onClick)="goBack()"
        />
      </div>

      @if (loading()) {
        <app-empty-state icon="pi-spin pi-spinner" title="Loading event…" description="" />
      } @else if (!event()) {
        <app-empty-state
          icon="pi-exclamation-circle"
          title="Event not found"
          description="This audit event could not be loaded."
        />
      } @else {
        <!-- Title row -->
        <div class="detail-title-row">
          <h1 class="detail-title">
            <i class="pi pi-list"></i>
            Audit Event
          </h1>
          <app-status-badge
            [label]="event()!.eventType"
            [severity]="getEventSeverity(event()!.eventType)"
          />
        </div>

        <!-- Envelope fields -->
        <p-card header="Event Envelope" styleClass="detail-card">
          <div class="field-grid">
            <div class="field">
              <span class="field-label">Event ID</span>
              <span class="field-value code">{{ event()!.eventId }}</span>
            </div>
            <div class="field">
              <span class="field-label">Event Type</span>
              <span class="field-value">{{ event()!.eventType }}</span>
            </div>
            <div class="field">
              <span class="field-label">Timestamp</span>
              <span class="field-value">{{ event()!.timestamp | utcDate: 'long' }}</span>
            </div>
            <div class="field">
              <span class="field-label">Schema Version</span>
              <span class="field-value">v{{ event()!.schemaVersion }}</span>
            </div>
            <div class="field">
              <span class="field-label">Source</span>
              <span class="field-value code">{{ event()!.source }}</span>
            </div>
            <div class="field">
              <span class="field-label">Correlation ID</span>
              <span class="field-value code">{{ event()!.correlationId }}</span>
            </div>
            <div class="field">
              <span class="field-label">Legal Entity ID</span>
              <span class="field-value code">{{ event()!.legalEntityId }}</span>
            </div>
            <div class="field">
              <span class="field-label">Site Code</span>
              <span class="field-value">{{ event()!.siteCode ?? '—' }}</span>
            </div>
          </div>
        </p-card>

        <!-- Payload -->
        <p-card styleClass="detail-card payload-card">
          <ng-template pTemplate="header">
            <div class="payload-card-header">
              <span>Event Payload</span>
              <p-button
                label="Copy"
                icon="pi pi-copy"
                severity="secondary"
                size="small"
                [text]="true"
                (onClick)="copyPayload()"
              />
            </div>
          </ng-template>
          <pre class="payload-json">{{ formattedPayload() }}</pre>
        </p-card>

        <!-- Correlation trace link -->
        <div class="trace-link">
          <p-button
            label="View Full Correlation Trace"
            icon="pi pi-filter-fill"
            severity="info"
            [text]="true"
            (onClick)="viewTrace()"
          />
          <small class="trace-hint">Shows all events sharing this Correlation ID</small>
        </div>
      }
    </div>
  `,
  styles: [
    `
      :host {
        display: block;
        padding: 1.5rem;
      }
      .page-header {
        margin-bottom: 1.25rem;
      }
      .detail-title-row {
        display: flex;
        align-items: center;
        gap: 1rem;
        margin-bottom: 1.25rem;
        flex-wrap: wrap;
      }
      .detail-title {
        font-size: 1.4rem;
        font-weight: 700;
        margin: 0;
        display: flex;
        align-items: center;
        gap: 0.5rem;
        color: var(--p-text-color, #1e293b);
      }
      .detail-card {
        margin-bottom: 1rem;
      }
      .field-grid {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
        gap: 0.75rem 1.25rem;
      }
      .field {
        display: flex;
        flex-direction: column;
        gap: 0.2rem;
      }
      .field-label {
        font-size: 0.72rem;
        font-weight: 600;
        text-transform: uppercase;
        letter-spacing: 0.04em;
        color: var(--p-text-muted-color, #64748b);
      }
      .field-value {
        font-size: 0.9rem;
        color: var(--p-text-color, #1e293b);
      }
      .field-value.code {
        font-family: monospace;
        font-size: 0.82rem;
        background: var(--p-surface-100, #f1f5f9);
        padding: 0.15rem 0.4rem;
        border-radius: 4px;
        word-break: break-all;
      }
      .payload-card-header {
        display: flex;
        align-items: center;
        justify-content: space-between;
        padding: 0.75rem 1rem 0;
        font-weight: 600;
      }
      .payload-json {
        margin: 0;
        font-family: monospace;
        font-size: 0.82rem;
        white-space: pre-wrap;
        word-break: break-all;
        background: var(--p-surface-50, #f8fafc);
        border: 1px solid var(--p-surface-200, #e2e8f0);
        border-radius: 4px;
        padding: 1rem;
        max-height: 60vh;
        overflow-y: auto;
        color: var(--p-text-color, #1e293b);
      }
      .trace-link {
        display: flex;
        align-items: center;
        gap: 1rem;
        margin-top: 0.5rem;
      }
      .trace-hint {
        color: var(--p-text-muted-color, #94a3b8);
        font-size: 0.8rem;
      }
    `,
  ],
})
export class AuditDetailComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly auditService = inject(AuditService);
  private readonly destroyRef = inject(DestroyRef);

  readonly loading = signal(false);
  readonly event = signal<AuditEvent | null>(null);

  readonly formattedPayload = () => {
    const e = this.event();
    if (!e) return '';
    try {
      return JSON.stringify(e.payload, null, 2);
    } catch {
      return String(e.payload);
    }
  };

  ngOnInit(): void {
    const id = this.route.snapshot.paramMap.get('id');
    if (!id) {
      this.router.navigate(['/audit/list']);
      return;
    }
    this.loadEvent(id);
  }

  goBack(): void {
    this.router.navigate(['/audit/list']);
  }

  viewTrace(): void {
    const correlationId = this.event()?.correlationId;
    if (!correlationId) return;
    this.router.navigate(['/audit/list'], {
      queryParams: { correlationId },
    });
  }

  copyPayload(): void {
    const text = this.formattedPayload();
    navigator.clipboard.writeText(text).catch(() => {});
  }

  getEventSeverity(eventType: EventType): PrimeSeverity {
    return eventTypeSeverity(eventType);
  }

  private loadEvent(id: string): void {
    this.loading.set(true);
    this.auditService
      .getAuditEventById(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (event) => {
          this.event.set(event);
          this.loading.set(false);
        },
        error: () => {
          this.event.set(null);
          this.loading.set(false);
        },
      });
  }
}
