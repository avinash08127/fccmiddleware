import {
  Component,
  DestroyRef,
  OnInit,
  inject,
  signal,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CardModule } from 'primeng/card';
import { DialogModule } from 'primeng/dialog';
import { TextareaModule } from 'primeng/textarea';
import { MessageService } from 'primeng/api';
import { ToastModule } from 'primeng/toast';

import { ReconciliationService } from '../../core/services/reconciliation.service';
import { ReconciliationRecord } from '../../core/models/reconciliation.model';
import { ReconciliationStatus } from '../../core/models/transaction.model';
import { StatusBadgeComponent } from '../../shared/components/status-badge/status-badge.component';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { CurrencyMinorUnitsPipe } from '../../shared/pipes/currency-minor-units.pipe';
import { UtcDatePipe } from '../../shared/pipes/utc-date.pipe';
import { StatusLabelPipe } from '../../shared/pipes/status-label.pipe';
import { RoleVisibleDirective } from '../../shared/directives/role-visible.directive';

type PrimeSeverity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';
type PendingAction = 'approve' | 'reject';

function statusSeverity(status: ReconciliationStatus | null): PrimeSeverity {
  switch (status) {
    case ReconciliationStatus.APPROVED: return 'success';
    case ReconciliationStatus.VARIANCE_FLAGGED: return 'danger';
    case ReconciliationStatus.UNMATCHED: return 'warn';
    case ReconciliationStatus.REJECTED: return 'secondary';
    case ReconciliationStatus.MATCHED:
    case ReconciliationStatus.VARIANCE_WITHIN_TOLERANCE: return 'success';
    default: return 'info';
  }
}

@Component({
  selector: 'app-reconciliation-detail',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    ButtonModule,
    CardModule,
    DialogModule,
    TextareaModule,
    ToastModule,
    StatusBadgeComponent,
    EmptyStateComponent,
    CurrencyMinorUnitsPipe,
    UtcDatePipe,
    StatusLabelPipe,
    RoleVisibleDirective,
  ],
  providers: [MessageService],
  template: `
    <p-toast />

    <div class="page-container">
      <!-- Back button -->
      <div class="page-header">
        <p-button
          label="Back to Exceptions"
          icon="pi pi-arrow-left"
          severity="secondary"
          size="small"
          (onClick)="goBack()"
        />
      </div>

      @if (loading()) {
        <app-empty-state icon="pi-spin pi-spinner" title="Loading..." description="" />
      } @else if (!record()) {
        <app-empty-state
          icon="pi-exclamation-circle"
          title="Record not found"
          description="This reconciliation record could not be loaded."
        />
      } @else {
        <!-- Ambiguity warning -->
        @if (record()!.ambiguityFlag) {
          <div class="ambiguity-banner">
            <i class="pi pi-exclamation-triangle"></i>
            <strong>Ambiguous Match:</strong> Multiple transaction candidates were found for this
            pre-auth. A tie-break was applied using the most recent authorisation timestamp. Review
            carefully before approving.
          </div>
        }

        <!-- Title row -->
        <div class="detail-title-row">
          <h1 class="detail-title">
            <i class="pi pi-sync"></i>
            Reconciliation #<code>{{ record()!.id | slice: 0 : 8 }}</code>
          </h1>
          <app-status-badge
            [label]="(record()!.reconciliationStatus | statusLabel)"
            [severity]="getSeverity(record()!.reconciliationStatus)"
          />
        </div>

        <!-- Detail grid -->
        <div class="detail-grid">
          <!-- Pre-auth card -->
          <p-card header="Pre-Auth Details" styleClass="detail-card">
            <div class="field-grid">
              <div class="field">
                <span class="field-label">Odoo Order ID</span>
                <span class="field-value code">{{ record()!.odooOrderId || '—' }}</span>
              </div>
              <div class="field">
                <span class="field-label">Site</span>
                <span class="field-value">{{ record()!.siteCode }}</span>
              </div>
              <div class="field">
                <span class="field-label">Pump / Nozzle</span>
                <span class="field-value">{{ record()!.pumpNumber }} / {{ record()!.nozzleNumber }}</span>
              </div>
              <div class="field">
                <span class="field-label">Product</span>
                <span class="field-value">{{ record()!.productCode || '—' }}</span>
              </div>
              <div class="field">
                <span class="field-label">Authorised Amount</span>
                <span class="field-value amount">
                  {{ record()!.requestedAmount != null
                      ? (record()!.requestedAmount! | currencyMinorUnits: record()!.currencyCode ?? '')
                      : '—' }}
                </span>
              </div>
              <div class="field">
                <span class="field-label">Pre-Auth Status</span>
                <span class="field-value">{{ (record()!.preAuthStatus | statusLabel) || '—' }}</span>
              </div>
              @if (record()!.preAuthSummary; as pa) {
                @if (pa.vehicleNumber) {
                  <div class="field">
                    <span class="field-label">Vehicle Number</span>
                    <span class="field-value">{{ pa.vehicleNumber }}</span>
                  </div>
                }
                @if (pa.customerBusinessName) {
                  <div class="field">
                    <span class="field-label">Customer Business</span>
                    <span class="field-value">{{ pa.customerBusinessName }}</span>
                  </div>
                }
                @if (pa.attendantId) {
                  <div class="field">
                    <span class="field-label">Attendant ID</span>
                    <span class="field-value code">{{ pa.attendantId }}</span>
                  </div>
                }
                @if (pa.fccCorrelationId) {
                  <div class="field">
                    <span class="field-label">FCC Correlation ID</span>
                    <span class="field-value code">{{ pa.fccCorrelationId }}</span>
                  </div>
                }
                @if (pa.requestedAt) {
                  <div class="field">
                    <span class="field-label">Requested At</span>
                    <span class="field-value">{{ pa.requestedAt | utcDate: 'medium' }}</span>
                  </div>
                }
              }
            </div>
          </p-card>

          <!-- Transaction card -->
          <p-card header="Transaction Details" styleClass="detail-card">
            @if (record()!.transactionId) {
              <div class="field-grid">
                <div class="field">
                  <span class="field-label">Transaction ID</span>
                  <span class="field-value code">{{ record()!.transactionId }}</span>
                </div>
                <div class="field">
                  <span class="field-label">Actual Amount</span>
                  <span class="field-value amount">
                    {{ record()!.actualAmount != null
                        ? (record()!.actualAmount! | currencyMinorUnits: record()!.currencyCode)
                        : '—' }}
                  </span>
                </div>
                @if (record()!.transactionSummary; as tx) {
                  @if (tx.volumeMicrolitres != null) {
                    <div class="field">
                      <span class="field-label">Volume Dispensed</span>
                      <span class="field-value">{{ formatVolume(tx.volumeMicrolitres!) }} L</span>
                    </div>
                  }
                  @if (tx.startedAt) {
                    <div class="field">
                      <span class="field-label">Started At</span>
                      <span class="field-value">{{ tx.startedAt | utcDate: 'medium' }}</span>
                    </div>
                  }
                  @if (tx.completedAt) {
                    <div class="field">
                      <span class="field-label">Completed At</span>
                      <span class="field-value">{{ tx.completedAt | utcDate: 'medium' }}</span>
                    </div>
                  }
                }
              </div>
            } @else {
              <app-empty-state
                icon="pi-link-slash"
                title="No transaction matched"
                description="This pre-auth record has not been linked to a dispense transaction."
              />
            }
          </p-card>

          <!-- Variance card -->
          <p-card header="Variance Breakdown" styleClass="detail-card">
            <div class="field-grid">
              <div class="field">
                <span class="field-label">Authorised Amount</span>
                <span class="field-value amount">
                  {{ record()!.requestedAmount != null
                      ? (record()!.requestedAmount! | currencyMinorUnits: record()!.currencyCode ?? '')
                      : '—' }}
                </span>
              </div>
              <div class="field">
                <span class="field-label">Actual Amount</span>
                <span class="field-value amount">
                  {{ record()!.actualAmount != null
                      ? (record()!.actualAmount! | currencyMinorUnits: record()!.currencyCode)
                      : '—' }}
                </span>
              </div>
              <div class="field">
                <span class="field-label">Variance (amount)</span>
                <span class="field-value" [class]="varianceClass()">{{ formatVariance() }}</span>
              </div>
              <div class="field">
                <span class="field-label">Variance (%)</span>
                <span class="field-value" [class]="varianceClass()">{{ formatVariancePct() }}</span>
              </div>
              <div class="field">
                <span class="field-label">Match Method</span>
                <span class="field-value">{{ matchMethodLabel() }}</span>
              </div>
              @if (record()!.decidedAt) {
                <div class="field">
                  <span class="field-label">Reviewed By</span>
                  <span class="field-value">{{ record()!.decidedBy ?? '—' }}</span>
                </div>
                <div class="field">
                  <span class="field-label">Reviewed At</span>
                  <span class="field-value">{{ record()!.decidedAt | utcDate: 'medium' }}</span>
                </div>
                @if (record()!.decisionReason) {
                  <div class="field field--full">
                    <span class="field-label">Review Reason</span>
                    <span class="field-value reason-text">{{ record()!.decisionReason }}</span>
                  </div>
                }
              }
            </div>
          </p-card>
        </div>

        <!-- Approve / Reject actions — only for authorized roles and VARIANCE_FLAGGED status -->
        @if (record()!.reconciliationStatus === ReconciliationStatus.VARIANCE_FLAGGED) {
          <ng-container *appRoleVisible="['SystemAdmin', 'OperationsManager']">
            <p-card header="Review Action" styleClass="action-card">
              <p class="action-hint">
                This record has a variance that exceeds the configured tolerance. Review the details
                above and either approve or reject.
              </p>
              <div class="action-buttons">
                <p-button
                  label="Approve Variance"
                  icon="pi pi-check"
                  severity="success"
                  (onClick)="openDialog('approve')"
                />
                <p-button
                  label="Reject"
                  icon="pi pi-times"
                  severity="danger"
                  (onClick)="openDialog('reject')"
                />
              </div>
            </p-card>
          </ng-container>
        }
      }
    </div>

    <!-- Approve / Reject confirmation dialog -->
    <p-dialog
      [(visible)]="showDialog"
      [header]="pendingAction() === 'approve' ? 'Approve Variance' : 'Reject Record'"
      [modal]="true"
      [style]="{ width: '480px' }"
      [closable]="!submitting()"
    >
      <div class="dialog-body">
        <p class="dialog-description">
          @if (pendingAction() === 'approve') {
            You are about to <strong>approve</strong> this variance exception. This action will
            mark the record as reviewed and cannot be undone.
          } @else {
            You are about to <strong>reject</strong> this reconciliation record. The record will be
            marked as rejected and cannot be undone.
          }
        </p>
        <div class="form-field">
          <label for="reason">
            Reason <span class="required">*</span>
            <small class="char-count">({{ reason().length }} / min. 10)</small>
          </label>
          <textarea
            id="reason"
            pTextarea
            [ngModel]="reason()"
            (ngModelChange)="reason.set($event)"
            rows="4"
            placeholder="Explain the reason for this decision…"
            style="width: 100%"
          ></textarea>
          @if (reason().length > 0 && reason().length < 10) {
            <small class="validation-error">Reason must be at least 10 characters.</small>
          }
        </div>
      </div>
      <ng-template pTemplate="footer">
        <p-button
          label="Cancel"
          severity="secondary"
          [disabled]="submitting()"
          (onClick)="closeDialog()"
        />
        <p-button
          [label]="pendingAction() === 'approve' ? 'Approve' : 'Reject'"
          [severity]="pendingAction() === 'approve' ? 'success' : 'danger'"
          [icon]="pendingAction() === 'approve' ? 'pi pi-check' : 'pi pi-times'"
          [disabled]="reason().length < 10 || submitting()"
          [loading]="submitting()"
          (onClick)="submitAction()"
        />
      </ng-template>
    </p-dialog>
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
      .ambiguity-banner {
        display: flex;
        align-items: center;
        gap: 0.6rem;
        background: var(--p-orange-50, #fff7ed);
        border: 1px solid var(--p-orange-300, #fdba74);
        border-radius: 6px;
        padding: 0.75rem 1rem;
        margin-bottom: 1.25rem;
        color: var(--p-orange-800, #9a3412);
        font-size: 0.9rem;
      }
      .ambiguity-banner .pi {
        font-size: 1.1rem;
        flex-shrink: 0;
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
      .detail-title code {
        font-family: monospace;
        font-size: 1rem;
        background: var(--p-surface-100, #f1f5f9);
        padding: 0.1rem 0.4rem;
        border-radius: 4px;
      }
      .detail-grid {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(320px, 1fr));
        gap: 1rem;
        margin-bottom: 1rem;
      }
      .field-grid {
        display: grid;
        grid-template-columns: 1fr 1fr;
        gap: 0.75rem 1rem;
      }
      .field {
        display: flex;
        flex-direction: column;
        gap: 0.2rem;
      }
      .field--full {
        grid-column: 1 / -1;
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
        font-size: 0.8rem;
        background: var(--p-surface-100, #f1f5f9);
        padding: 0.1rem 0.35rem;
        border-radius: 4px;
      }
      .field-value.amount {
        font-weight: 600;
      }
      .field-value.reason-text {
        white-space: pre-wrap;
        font-style: italic;
      }
      .variance-positive {
        color: var(--p-red-600, #dc2626);
        font-weight: 600;
      }
      .variance-negative {
        color: var(--p-orange-500, #f97316);
        font-weight: 600;
      }
      .variance-zero {
        color: var(--p-green-600, #16a34a);
      }
      .variance-null {
        color: var(--p-text-muted-color, #94a3b8);
      }
      .action-card {
        margin-top: 0.5rem;
      }
      .action-hint {
        color: var(--p-text-muted-color, #64748b);
        font-size: 0.875rem;
        margin: 0 0 1rem;
      }
      .action-buttons {
        display: flex;
        gap: 0.75rem;
        flex-wrap: wrap;
      }
      .dialog-body {
        display: flex;
        flex-direction: column;
        gap: 1rem;
      }
      .dialog-description {
        font-size: 0.9rem;
        color: var(--p-text-muted-color, #475569);
        margin: 0;
      }
      .form-field {
        display: flex;
        flex-direction: column;
        gap: 0.4rem;
      }
      .form-field label {
        font-size: 0.82rem;
        font-weight: 600;
        color: var(--p-text-color);
      }
      .required {
        color: var(--p-red-600, #dc2626);
        margin-left: 2px;
      }
      .char-count {
        font-weight: 400;
        color: var(--p-text-muted-color, #64748b);
        margin-left: 0.3rem;
      }
      .validation-error {
        color: var(--p-red-600, #dc2626);
        font-size: 0.78rem;
      }
    `,
  ],
})
export class ReconciliationDetailComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly reconService = inject(ReconciliationService);
  private readonly messageService = inject(MessageService);
  private readonly destroyRef = inject(DestroyRef);

  readonly ReconciliationStatus = ReconciliationStatus;

  // ── State ─────────────────────────────────────────────────────────────────
  readonly loading = signal(false);
  readonly record = signal<ReconciliationRecord | null>(null);
  readonly submitting = signal(false);

  // ── Dialog ────────────────────────────────────────────────────────────────
  showDialog = false;
  readonly pendingAction = signal<PendingAction>('approve');
  readonly reason = signal('');

  ngOnInit(): void {
    const id = this.route.snapshot.paramMap.get('id');
    if (!id) {
      this.router.navigate(['/reconciliation/exceptions']);
      return;
    }
    this.loadRecord(id);
  }

  goBack(): void {
    this.router.navigate(['/reconciliation/exceptions']);
  }

  openDialog(action: PendingAction): void {
    this.pendingAction.set(action);
    this.reason.set('');
    this.showDialog = true;
  }

  closeDialog(): void {
    this.showDialog = false;
  }

  submitAction(): void {
    const rec = this.record();
    if (!rec || this.reason().length < 10) return;

    this.submitting.set(true);
    const action$ =
      this.pendingAction() === 'approve'
        ? this.reconService.approve(rec.id, this.reason())
        : this.reconService.reject(rec.id, this.reason());

    action$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (updated) => {
        this.record.set(updated);
        this.showDialog = false;
        this.submitting.set(false);
        const label = this.pendingAction() === 'approve' ? 'approved' : 'rejected';
        this.messageService.add({
          severity: 'success',
          summary: `Record ${label}`,
          detail: `Reconciliation record has been ${label} successfully.`,
          life: 4000,
        });
      },
      error: () => {
        this.submitting.set(false);
        this.messageService.add({
          severity: 'error',
          summary: 'Action failed',
          detail: 'Could not complete the action. Please try again.',
          life: 5000,
        });
      },
    });
  }

  getSeverity(status: ReconciliationStatus | null): PrimeSeverity {
    return statusSeverity(status);
  }

  formatVariance(): string {
    const rec = this.record();
    if (!rec || rec.amountVariance == null) return '—';
    const sign = rec.amountVariance >= 0 ? '+' : '';
    return `${sign}${(rec.amountVariance / 100).toFixed(2)} ${rec.currencyCode}`;
  }

  formatVariancePct(): string {
    const rec = this.record();
    if (!rec || rec.varianceBps == null) return '—';
    const sign = rec.varianceBps >= 0 ? '+' : '';
    return `${sign}${(rec.varianceBps / 100).toFixed(2)}%`;
  }

  varianceClass(): string {
    const rec = this.record();
    if (!rec || rec.amountVariance == null) return 'variance-null';
    if (rec.amountVariance === 0) return 'variance-zero';
    return rec.amountVariance > 0 ? 'variance-positive' : 'variance-negative';
  }

  matchMethodLabel(): string {
    const method = this.record()?.matchMethod;
    if (!method) return '—';
    const labels: Record<string, string> = {
      EXACT_CORRELATION_ID: 'Exact Correlation ID',
      PUMP_NOZZLE_TIME_WINDOW: 'Pump + Nozzle + Time Window',
      ODOO_ORDER_ID: 'Odoo Order ID',
    };
    return labels[method] ?? method.replace(/_/g, ' ');
  }

  formatVolume(microlitres: number): string {
    return (microlitres / 1_000_000).toFixed(3);
  }

  // ── Private ───────────────────────────────────────────────────────────────

  private loadRecord(id: string): void {
    this.loading.set(true);
    this.reconService
      .getById(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (rec) => {
          this.record.set(rec);
          this.loading.set(false);
        },
        error: () => {
          this.record.set(null);
          this.loading.set(false);
          this.messageService.add({
            severity: 'error',
            summary: 'Load failed',
            detail: 'Could not load reconciliation record.',
            life: 5000,
          });
        },
      });
  }
}
