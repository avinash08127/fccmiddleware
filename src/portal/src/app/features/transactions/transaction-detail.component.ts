import { Component, DestroyRef, inject, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CardModule } from 'primeng/card';
import { ButtonModule } from 'primeng/button';
import { DividerModule } from 'primeng/divider';
import { TimelineModule } from 'primeng/timeline';
import { PanelModule } from 'primeng/panel';
import { SkeletonModule } from 'primeng/skeleton';

import { TransactionService } from '../../core/services/transaction.service';
import { AuditService } from '../../core/services/audit.service';
import { TransactionDetail, TransactionStatus } from '../../core/models/transaction.model';
import { AuditEvent } from '../../core/models/audit.model';
import { StatusBadgeComponent } from '../../shared/components/status-badge/status-badge.component';
import { CurrencyMinorUnitsPipe } from '../../shared/pipes/currency-minor-units.pipe';
import { UtcDatePipe } from '../../shared/pipes/utc-date.pipe';
import { StatusLabelPipe } from '../../shared/pipes/status-label.pipe';

type PrimeSeverity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

function txSeverity(status: TransactionStatus): PrimeSeverity {
  switch (status) {
    case TransactionStatus.SYNCED_TO_ODOO:
    case TransactionStatus.SYNCED:
      return 'success';
    case TransactionStatus.PENDING:
      return 'warn';
    case TransactionStatus.STALE_PENDING:
      return 'danger';
    case TransactionStatus.DUPLICATE:
    case TransactionStatus.ARCHIVED:
      return 'secondary';
    default:
      return 'info';
  }
}

@Component({
  selector: 'app-transaction-detail',
  standalone: true,
  imports: [
    CommonModule,
    RouterLink,
    CardModule,
    ButtonModule,
    DividerModule,
    TimelineModule,
    PanelModule,
    SkeletonModule,
    StatusBadgeComponent,
    CurrencyMinorUnitsPipe,
    UtcDatePipe,
    StatusLabelPipe,
  ],
  template: `
    <div class="page-container">
      <!-- Back nav -->
      <div class="page-nav">
        <p-button
          icon="pi pi-arrow-left"
          label="Back to Transactions"
          severity="secondary"
          [text]="true"
          (onClick)="goBack()"
        />
      </div>

      @if (loading()) {
        <div class="skeleton-layout">
          <p-skeleton height="2.5rem" width="50%" styleClass="mb-3" />
          <div class="detail-grid">
            <p-skeleton height="420px" />
            <p-skeleton height="420px" />
          </div>
        </div>
      } @else if (error()) {
        <div class="error-state">
          <i class="pi pi-exclamation-triangle"></i>
          <p>{{ error() }}</p>
          <p-button label="Retry" icon="pi pi-refresh" severity="secondary" (onClick)="load()" />
        </div>
      } @else if (tx()) {
        <!-- Title row -->
        <div class="detail-header">
          <h1 class="detail-title">
            <i class="pi pi-receipt"></i>
            Transaction&nbsp;<code class="tx-id">{{ tx()!.fccTransactionId }}</code>
          </h1>
          <app-status-badge
            [label]="tx()!.status | statusLabel"
            [severity]="getSeverity(tx()!.status)"
          />
        </div>

        <!-- Notices -->
        @if (tx()!.isDuplicate || tx()!.duplicateOfId) {
          <div class="notice notice--warn">
            <i class="pi pi-copy"></i>
            <span>
              This record is a duplicate.
              @if (tx()!.duplicateOfId) {
                See original:
                <a [routerLink]="['/transactions', tx()!.duplicateOfId]">
                  {{ tx()!.duplicateOfId }}
                </a>
              }
            </span>
          </div>
        }

        @if (tx()!.reconciliationStatus) {
          <div class="notice notice--info">
            <i class="pi pi-check-circle"></i>
            <span>
              Reconciliation:
              <strong>{{ tx()!.reconciliationStatus | statusLabel }}</strong>
              @if (tx()!.preAuthId) {
                &mdash; Pre-Auth
                <a [routerLink]="['/reconciliation']" [queryParams]="{ preAuthId: tx()!.preAuthId }">
                  {{ tx()!.preAuthId }}
                </a>
              }
            </span>
          </div>
        }

        <!-- Main grid: details card + timeline card -->
        <div class="detail-grid">
          <!-- ── Transaction details ── -->
          <p-card header="Transaction Details">
            <section class="field-section">
              <h4 class="section-heading">Identifiers</h4>
              <div class="field-grid">
                <div class="field-item">
                  <span class="field-label">Internal ID</span>
                  <code class="field-value mono">{{ tx()!.id }}</code>
                </div>
                <div class="field-item">
                  <span class="field-label">FCC Transaction ID</span>
                  <code class="field-value mono">{{ tx()!.fccTransactionId }}</code>
                </div>
                <div class="field-item">
                  <span class="field-label">Correlation ID</span>
                  <code class="field-value mono">{{ tx()!.correlationId }}</code>
                </div>
                <div class="field-item">
                  <span class="field-label">Legal Entity ID</span>
                  <code class="field-value mono">{{ tx()!.legalEntityId }}</code>
                </div>
                @if (tx()!.odooOrderId) {
                  <div class="field-item">
                    <span class="field-label">Odoo Order ID</span>
                    <span class="field-value">{{ tx()!.odooOrderId }}</span>
                  </div>
                }
                @if (tx()!.preAuthId) {
                  <div class="field-item">
                    <span class="field-label">Pre-Auth ID</span>
                    <span class="field-value">{{ tx()!.preAuthId }}</span>
                  </div>
                }
                @if (tx()!.fiscalReceiptNumber) {
                  <div class="field-item">
                    <span class="field-label">Fiscal Receipt #</span>
                    <span class="field-value">{{ tx()!.fiscalReceiptNumber }}</span>
                  </div>
                }
                @if (tx()!.attendantId) {
                  <div class="field-item">
                    <span class="field-label">Attendant ID</span>
                    <span class="field-value">{{ tx()!.attendantId }}</span>
                  </div>
                }
              </div>
            </section>

            <p-divider />

            <section class="field-section">
              <h4 class="section-heading">Fuel Dispensing</h4>
              <div class="field-grid">
                <div class="field-item">
                  <span class="field-label">Site</span>
                  <span class="field-value">{{ tx()!.siteCode }}</span>
                </div>
                <div class="field-item">
                  <span class="field-label">Pump / Nozzle</span>
                  <span class="field-value">{{ tx()!.pumpNumber }} / {{ tx()!.nozzleNumber }}</span>
                </div>
                <div class="field-item">
                  <span class="field-label">Product</span>
                  <span class="field-value">{{ tx()!.productCode }}</span>
                </div>
                <div class="field-item">
                  <span class="field-label">Volume</span>
                  <span class="field-value">{{ formatVolume(tx()!.volumeMicrolitres) }}</span>
                </div>
                <div class="field-item">
                  <span class="field-label">Amount</span>
                  <span class="field-value">
                    {{ tx()!.amountMinorUnits | currencyMinorUnits: tx()!.currencyCode }}
                  </span>
                </div>
                <div class="field-item">
                  <span class="field-label">Unit Price / L</span>
                  <span class="field-value">
                    {{ tx()!.unitPriceMinorPerLitre | currencyMinorUnits: tx()!.currencyCode }}
                  </span>
                </div>
                <div class="field-item">
                  <span class="field-label">Currency</span>
                  <span class="field-value">{{ tx()!.currencyCode }}</span>
                </div>
              </div>
            </section>

            <p-divider />

            <section class="field-section">
              <h4 class="section-heading">Status &amp; Ingestion</h4>
              <div class="field-grid">
                <div class="field-item">
                  <span class="field-label">Status</span>
                  <app-status-badge
                    [label]="tx()!.status | statusLabel"
                    [severity]="getSeverity(tx()!.status)"
                  />
                </div>
                <div class="field-item">
                  <span class="field-label">Reconciliation</span>
                  <span class="field-value">
                    {{ (tx()!.reconciliationStatus | statusLabel) || '—' }}
                  </span>
                </div>
                <div class="field-item">
                  <span class="field-label">FCC Vendor</span>
                  <span class="field-value">{{ tx()!.fccVendor }}</span>
                </div>
                <div class="field-item">
                  <span class="field-label">Ingestion Source</span>
                  <span class="field-value">{{ tx()!.ingestionSource | statusLabel }}</span>
                </div>
                <div class="field-item">
                  <span class="field-label">Schema Version</span>
                  <span class="field-value">{{ tx()!.schemaVersion }}</span>
                </div>
              </div>
            </section>

            <p-divider />

            <section class="field-section">
              <h4 class="section-heading">Timestamps</h4>
              <div class="field-grid">
                <div class="field-item">
                  <span class="field-label">Started At</span>
                  <span class="field-value">{{ tx()!.startedAt | utcDate }}</span>
                </div>
                <div class="field-item">
                  <span class="field-label">Completed At</span>
                  <span class="field-value">{{ tx()!.completedAt | utcDate }}</span>
                </div>
                <div class="field-item">
                  <span class="field-label">Ingested At</span>
                  <span class="field-value">{{ tx()!.ingestedAt | utcDate }}</span>
                </div>
                <div class="field-item">
                  <span class="field-label">Updated At</span>
                  <span class="field-value">{{ tx()!.updatedAt | utcDate }}</span>
                </div>
              </div>
            </section>
          </p-card>

          <!-- ── Audit event timeline ── -->
          <p-card header="Event Trail">
            @if (eventsLoading()) {
              <p-skeleton height="240px" />
            } @else if (auditEvents().length === 0) {
              <div class="no-events">
                <i class="pi pi-history"></i>
                <span>No audit events found for this correlation ID.</span>
              </div>
            } @else {
              <p-timeline [value]="auditEvents()" align="left" styleClass="event-timeline">
                <ng-template pTemplate="marker">
                  <span class="timeline-dot"><i class="pi pi-circle-fill"></i></span>
                </ng-template>
                <ng-template pTemplate="content" let-event>
                  <div class="timeline-event">
                    <span class="event-type">{{ event.eventType }}</span>
                    <span class="event-time">{{ event.timestamp | utcDate: 'short' }}</span>
                    @if (event.source) {
                      <span class="event-source">{{ event.source }}</span>
                    }
                  </div>
                </ng-template>
              </p-timeline>
            }
          </p-card>
        </div>

        <!-- Raw FCC payload (collapsible) -->
        @if (tx()!.rawPayloadJson) {
          <p-panel
            header="Raw FCC Payload"
            [toggleable]="true"
            [collapsed]="true"
            styleClass="raw-panel"
          >
            <pre class="raw-payload">{{ formatJson(tx()!.rawPayloadJson!) }}</pre>
          </p-panel>
        }
      }
    </div>
  `,
  styles: [
    `
      :host {
        display: block;
        padding: 1.5rem;
      }
      .page-nav {
        margin-bottom: 1rem;
      }
      .skeleton-layout {
        display: flex;
        flex-direction: column;
        gap: 1rem;
      }
      .error-state {
        display: flex;
        flex-direction: column;
        align-items: center;
        gap: 1rem;
        padding: 3rem;
        text-align: center;
        color: var(--p-red-500, #ef4444);
      }
      .error-state i {
        font-size: 2rem;
      }
      .detail-header {
        display: flex;
        align-items: center;
        gap: 1rem;
        margin-bottom: 0.75rem;
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
      .tx-id {
        font-family: monospace;
        font-size: 0.85em;
        background: var(--p-surface-100, #f1f5f9);
        padding: 0.15rem 0.4rem;
        border-radius: 4px;
      }
      .notice {
        display: flex;
        align-items: flex-start;
        gap: 0.5rem;
        padding: 0.6rem 1rem;
        border-radius: 6px;
        margin-bottom: 0.75rem;
        font-size: 0.875rem;
        line-height: 1.4;
      }
      .notice i {
        margin-top: 0.1rem;
        flex-shrink: 0;
      }
      .notice a {
        font-weight: 600;
        text-decoration: underline;
      }
      .notice--warn {
        background: var(--p-yellow-50, #fefce8);
        color: var(--p-yellow-900, #713f12);
      }
      .notice--info {
        background: var(--p-blue-50, #eff6ff);
        color: var(--p-blue-900, #1e3a8a);
      }
      .detail-grid {
        display: grid;
        grid-template-columns: 1fr 1fr;
        gap: 1.25rem;
        margin-bottom: 1.25rem;
      }
      @media (max-width: 900px) {
        .detail-grid {
          grid-template-columns: 1fr;
        }
      }
      .field-section {
        margin-bottom: 0.25rem;
      }
      .section-heading {
        font-size: 0.7rem;
        font-weight: 700;
        text-transform: uppercase;
        letter-spacing: 0.08em;
        color: var(--p-text-muted-color, #94a3b8);
        margin: 0 0 0.6rem;
      }
      .field-grid {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
        gap: 0.6rem 1.25rem;
      }
      .field-item {
        display: flex;
        flex-direction: column;
        gap: 0.1rem;
      }
      .field-label {
        font-size: 0.72rem;
        font-weight: 600;
        text-transform: uppercase;
        letter-spacing: 0.05em;
        color: var(--p-text-muted-color, #64748b);
      }
      .field-value {
        font-size: 0.875rem;
        color: var(--p-text-color, #1e293b);
        word-break: break-all;
      }
      .field-value.mono {
        font-family: monospace;
        font-size: 0.8rem;
      }
      .no-events {
        display: flex;
        align-items: center;
        gap: 0.5rem;
        color: var(--p-text-muted-color, #64748b);
        padding: 1rem 0;
        font-size: 0.875rem;
      }
      .timeline-dot {
        color: var(--p-primary-color, #3b82f6);
        font-size: 0.6rem;
      }
      .timeline-event {
        display: flex;
        flex-direction: column;
        gap: 0.1rem;
        padding-bottom: 0.75rem;
      }
      .event-type {
        font-size: 0.875rem;
        font-weight: 600;
        color: var(--p-text-color, #1e293b);
      }
      .event-time {
        font-size: 0.75rem;
        color: var(--p-text-muted-color, #64748b);
      }
      .event-source {
        font-size: 0.7rem;
        color: var(--p-text-muted-color, #94a3b8);
        font-style: italic;
      }
      .raw-panel {
        margin-top: 0;
      }
      .raw-payload {
        font-family: monospace;
        font-size: 0.8rem;
        line-height: 1.5;
        background: var(--p-surface-50, #f8fafc);
        padding: 1rem;
        border-radius: 6px;
        overflow: auto;
        max-height: 420px;
        white-space: pre-wrap;
        word-break: break-all;
        margin: 0;
      }
    `,
  ],
})
export class TransactionDetailComponent implements OnInit {
  private readonly txService = inject(TransactionService);
  private readonly auditService = inject(AuditService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly tx = signal<TransactionDetail | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly auditEvents = signal<AuditEvent[]>([]);
  readonly eventsLoading = signal(false);

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    const id = this.route.snapshot.paramMap.get('id');
    if (!id) {
      this.error.set('Invalid transaction ID.');
      this.loading.set(false);
      return;
    }

    this.loading.set(true);
    this.error.set(null);

    this.txService
      .getTransactionById(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (transaction) => {
          this.tx.set(transaction);
          this.loading.set(false);
          this.loadAuditEvents(transaction);
        },
        error: (err) => {
          this.error.set(
            err?.status === 404
              ? 'Transaction not found.'
              : 'Failed to load transaction. Please try again.',
          );
          this.loading.set(false);
        },
      });
  }

  goBack(): void {
    this.router.navigate(['/transactions', 'list']);
  }

  getSeverity(status: TransactionStatus): PrimeSeverity {
    return txSeverity(status);
  }

  formatVolume(microlitres: number): string {
    return `${(microlitres / 1_000_000).toFixed(3)} L`;
  }

  formatJson(json: string): string {
    try {
      return JSON.stringify(JSON.parse(json), null, 2);
    } catch {
      return json;
    }
  }

  private loadAuditEvents(transaction: TransactionDetail): void {
    this.eventsLoading.set(true);
    this.auditService
      .getAuditEvents({
        legalEntityId: transaction.legalEntityId,
        correlationId: transaction.correlationId,
        pageSize: 50,
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.auditEvents.set(result.data);
          this.eventsLoading.set(false);
        },
        error: () => this.eventsLoading.set(false),
      });
  }
}
