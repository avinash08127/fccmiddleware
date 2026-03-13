import { Component, DestroyRef, OnInit, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ButtonModule } from 'primeng/button';
import { CardModule } from 'primeng/card';
import { DialogModule } from 'primeng/dialog';

import { DlqService } from '../../core/services/dlq.service';
import { DeadLetterDetail, DeadLetterStatus } from '../../core/models/dlq.model';
import { StatusBadgeComponent } from '../../shared/components/status-badge/status-badge.component';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { UtcDatePipe } from '../../shared/pipes/utc-date.pipe';
import { RoleVisibleDirective } from '../../shared/directives/role-visible.directive';

type PrimeSeverity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

function statusSeverity(status: DeadLetterStatus): PrimeSeverity {
  switch (status) {
    case DeadLetterStatus.PENDING:
      return 'warn';
    case DeadLetterStatus.REPLAY_QUEUED:
      return 'info';
    case DeadLetterStatus.RETRYING:
      return 'info';
    case DeadLetterStatus.RESOLVED:
      return 'success';
    case DeadLetterStatus.REPLAY_FAILED:
      return 'danger';
    case DeadLetterStatus.DISCARDED:
      return 'secondary';
    default:
      return 'contrast';
  }
}

@Component({
  selector: 'app-dlq-detail',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    ButtonModule,
    CardModule,
    DialogModule,
    StatusBadgeComponent,
    EmptyStateComponent,
    UtcDatePipe,
    RoleVisibleDirective,
  ],
  template: `
    <div class="page-container">
      <!-- Back button -->
      <div class="page-header">
        <p-button
          label="Back to DLQ"
          icon="pi pi-arrow-left"
          severity="secondary"
          size="small"
          (onClick)="goBack()"
        />
      </div>

      @if (loading()) {
        <app-empty-state icon="pi-spin pi-spinner" title="Loading item…" description="" />
      } @else if (!item()) {
        <app-empty-state
          icon="pi-exclamation-circle"
          title="Item not found"
          description="This dead-letter queue item could not be loaded."
        />
      } @else {
        <!-- Title row -->
        <div class="detail-title-row">
          <h1 class="detail-title">
            <i class="pi pi-inbox"></i>
            DLQ Item
            <span class="detail-id">{{ item()!.id | slice: 0 : 8 }}…</span>
          </h1>
          <app-status-badge
            [label]="item()!.status"
            [severity]="getStatusSeverity(item()!.status)"
          />
        </div>

        <!-- Action feedback -->
        @if (actionMessage()) {
          <div
            class="feedback-bar"
            [class.feedback-success]="actionSeverity() === 'success'"
            [class.feedback-error]="actionSeverity() === 'error'"
          >
            <i
              class="pi"
              [class.pi-check-circle]="actionSeverity() === 'success'"
              [class.pi-times-circle]="actionSeverity() === 'error'"
            ></i>
            {{ actionMessage() }}
          </div>
        }

        <!-- Error details -->
        <p-card header="Error Details" styleClass="detail-card">
          <div class="field-grid">
            <div class="field">
              <span class="field-label">ID</span>
              <span class="field-value code">{{ item()!.id }}</span>
            </div>
            <div class="field">
              <span class="field-label">Type</span>
              <span class="field-value">{{ item()!.type }}</span>
            </div>
            <div class="field">
              <span class="field-label">Site Code</span>
              <span class="field-value">{{ item()!.siteCode }}</span>
            </div>
            <div class="field">
              <span class="field-label">FCC Transaction ID</span>
              <span class="field-value code">{{ item()!.fccTransactionId ?? '—' }}</span>
            </div>
            <div class="field">
              <span class="field-label">Failure Reason</span>
              <span class="field-value">{{ item()!.failureReason }}</span>
            </div>
            <div class="field">
              <span class="field-label">Error Code</span>
              <span class="field-value code">{{ item()!.errorCode }}</span>
            </div>
            <div class="field field--wide">
              <span class="field-label">Error Message</span>
              <span class="field-value">{{ item()!.errorMessage }}</span>
            </div>
            <div class="field">
              <span class="field-label">Retry Count</span>
              <span class="field-value" [class.retry-high]="item()!.retryCount >= 3">
                {{ item()!.retryCount }}
              </span>
            </div>
            <div class="field">
              <span class="field-label">Last Retry At</span>
              <span class="field-value">
                {{ item()!.lastRetryAt ? (item()!.lastRetryAt | utcDate: 'long') : '—' }}
              </span>
            </div>
            <div class="field">
              <span class="field-label">Created At</span>
              <span class="field-value">{{ item()!.createdAt | utcDate: 'long' }}</span>
            </div>
            <div class="field">
              <span class="field-label">Legal Entity ID</span>
              <span class="field-value code">{{ item()!.legalEntityId }}</span>
            </div>
          </div>
        </p-card>

        <!-- Discard info (shown only when discarded) -->
        @if (item()!.status === DeadLetterStatus.DISCARDED) {
          <p-card styleClass="detail-card discard-card">
            <ng-template pTemplate="header">
              <div class="discard-card-header">
                <i class="pi pi-ban"></i>
                Discard Information
              </div>
            </ng-template>
            <div class="field-grid">
              <div class="field field--wide">
                <span class="field-label">Discard Reason</span>
                <span class="field-value">{{ item()!.discardReason ?? '—' }}</span>
              </div>
              <div class="field">
                <span class="field-label">Discarded By</span>
                <span class="field-value code">{{ item()!.discardedBy ?? '—' }}</span>
              </div>
              <div class="field">
                <span class="field-label">Discarded At</span>
                <span class="field-value">
                  {{ item()!.discardedAt ? (item()!.discardedAt | utcDate: 'long') : '—' }}
                </span>
              </div>
            </div>
          </p-card>
        }

        <!-- Original payload -->
        <p-card styleClass="detail-card payload-card">
          <ng-template pTemplate="header">
            <div class="payload-card-header">
              <span>Original Payload</span>
              @if (item()!.rawPayload) {
                <p-button
                  label="Copy"
                  icon="pi pi-copy"
                  severity="secondary"
                  size="small"
                  [text]="true"
                  (onClick)="copyPayload()"
                />
              }
            </div>
          </ng-template>
          @if (item()!.rawPayload) {
            <pre class="payload-json">{{ formattedPayload() }}</pre>
          } @else {
            <p class="payload-unavailable">
              <i class="pi pi-info-circle"></i>
              Original payload is not available for this item.
              @if (item()!.rawPayloadRef) {
                <small>(Ref: {{ item()!.rawPayloadRef }})</small>
              }
            </p>
          }
        </p-card>

        <!-- Retry history -->
        <p-card header="Retry History" styleClass="detail-card">
          @if (item()!.retryHistory.length === 0) {
            <p class="retry-empty">No retry attempts recorded.</p>
          } @else {
            <table class="retry-table">
              <thead>
                <tr>
                  <th style="width: 4rem">#</th>
                  <th style="width: 13rem">Attempted At</th>
                  <th style="width: 8rem">Outcome</th>
                  <th style="width: 14rem">Error Code</th>
                  <th>Error Message</th>
                </tr>
              </thead>
              <tbody>
                @for (entry of item()!.retryHistory; track entry.attemptNumber) {
                  <tr>
                    <td>{{ entry.attemptNumber }}</td>
                    <td>{{ entry.attemptedAt | utcDate: 'short' }}</td>
                    <td>
                      <span
                        class="outcome-badge"
                        [class.outcome-success]="entry.outcome === 'SUCCESS'"
                        [class.outcome-failed]="entry.outcome === 'FAILED'"
                      >
                        {{ entry.outcome }}
                      </span>
                    </td>
                    <td class="mono-sm">{{ entry.errorCode ?? '—' }}</td>
                    <td>{{ entry.errorMessage ?? '—' }}</td>
                  </tr>
                }
              </tbody>
            </table>
          }
        </p-card>

        <!-- Actions — OpsManager+ only, hidden for terminal states -->
        @if (
          item()!.status !== DeadLetterStatus.DISCARDED &&
          item()!.status !== DeadLetterStatus.RESOLVED
        ) {
          <ng-container *appRoleVisible="['FccAdmin', 'FccUser']">
            <p-card header="Actions" styleClass="detail-card">
              <div class="actions-row">
                <div class="action-block">
                  <p-button
                    label="Retry"
                    icon="pi pi-refresh"
                    severity="info"
                    [loading]="retryLoading()"
                    [disabled]="discardLoading()"
                    (onClick)="retry()"
                  />
                  <small class="action-hint">Re-queues this item for processing.</small>
                </div>
                <div class="action-block">
                  <p-button
                    label="Discard"
                    icon="pi pi-ban"
                    severity="danger"
                    [loading]="discardLoading()"
                    [disabled]="retryLoading()"
                    (onClick)="openDiscardDialog()"
                  />
                  <small class="action-hint">Permanently mark as failed. Requires reason.</small>
                </div>
              </div>
            </p-card>
          </ng-container>
        }
      }
    </div>

    <!-- Discard dialog -->
    <p-dialog
      header="Discard Item"
      [(visible)]="discardDialogVisible"
      [modal]="true"
      [style]="{ width: '480px' }"
      [closable]="!discardLoading()"
    >
      <p class="dialog-body-text">
        This action cannot be undone. Please provide a mandatory reason.
      </p>
      <textarea
        class="reason-textarea"
        [(ngModel)]="discardReason"
        rows="4"
        placeholder="Reason for discarding..."
        maxlength="500"
      ></textarea>
      <small class="reason-hint">{{ discardReason.length }}/500 characters</small>
      <ng-template pTemplate="footer">
        <p-button
          label="Cancel"
          severity="secondary"
          [disabled]="discardLoading()"
          (onClick)="discardDialogVisible = false"
        />
        <p-button
          label="Confirm Discard"
          severity="danger"
          icon="pi pi-ban"
          [disabled]="!discardReason.trim() || discardLoading()"
          [loading]="discardLoading()"
          (onClick)="confirmDiscard()"
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
      .detail-id {
        font-family: monospace;
        font-size: 1.1rem;
        color: var(--p-text-muted-color, #64748b);
      }
      .feedback-bar {
        display: flex;
        align-items: center;
        gap: 0.5rem;
        padding: 0.65rem 1rem;
        border-radius: 6px;
        margin-bottom: 0.75rem;
        font-size: 0.875rem;
        border: 1px solid;
      }
      .feedback-success {
        background: var(--p-green-50, #f0fdf4);
        border-color: var(--p-green-300, #86efac);
        color: var(--p-green-800, #166534);
      }
      .feedback-error {
        background: var(--p-red-50, #fef2f2);
        border-color: var(--p-red-300, #fca5a5);
        color: var(--p-red-800, #991b1b);
      }
      .detail-card {
        margin-bottom: 1rem;
      }
      .field-grid {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(260px, 1fr));
        gap: 0.75rem 1.25rem;
      }
      .field {
        display: flex;
        flex-direction: column;
        gap: 0.2rem;
      }
      .field--wide {
        grid-column: span 2;
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
      .retry-high {
        color: var(--p-red-600, #dc2626);
        font-weight: 700;
      }
      .discard-card-header {
        display: flex;
        align-items: center;
        gap: 0.5rem;
        padding: 0.75rem 1rem 0;
        font-weight: 600;
        color: var(--p-red-700, #b91c1c);
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
      .payload-unavailable {
        display: flex;
        align-items: center;
        gap: 0.5rem;
        color: var(--p-text-muted-color, #94a3b8);
        font-size: 0.875rem;
        margin: 0;
        flex-wrap: wrap;
      }
      .retry-empty {
        color: var(--p-text-muted-color, #94a3b8);
        font-size: 0.875rem;
        margin: 0;
      }
      .retry-table {
        width: 100%;
        border-collapse: collapse;
        font-size: 0.85rem;
      }
      .retry-table th {
        text-align: left;
        font-size: 0.72rem;
        font-weight: 600;
        text-transform: uppercase;
        letter-spacing: 0.04em;
        color: var(--p-text-muted-color, #64748b);
        padding: 0.4rem 0.5rem;
        border-bottom: 1px solid var(--p-surface-200, #e2e8f0);
      }
      .retry-table td {
        padding: 0.45rem 0.5rem;
        border-bottom: 1px solid var(--p-surface-100, #f1f5f9);
        color: var(--p-text-color, #1e293b);
        vertical-align: top;
      }
      .mono-sm {
        font-family: monospace;
        font-size: 0.8rem;
      }
      .outcome-badge {
        display: inline-block;
        padding: 0.12rem 0.45rem;
        border-radius: 4px;
        font-size: 0.72rem;
        font-weight: 700;
        text-transform: uppercase;
      }
      .outcome-success {
        background: var(--p-green-100, #dcfce7);
        color: var(--p-green-700, #15803d);
      }
      .outcome-failed {
        background: var(--p-red-100, #fee2e2);
        color: var(--p-red-700, #b91c1c);
      }
      .actions-row {
        display: flex;
        gap: 2rem;
        flex-wrap: wrap;
      }
      .action-block {
        display: flex;
        flex-direction: column;
        align-items: flex-start;
        gap: 0.4rem;
      }
      .action-hint {
        color: var(--p-text-muted-color, #94a3b8);
        font-size: 0.78rem;
      }
      .dialog-body-text {
        margin: 0 0 1rem;
        font-size: 0.9rem;
        line-height: 1.5;
        color: var(--p-text-color, #1e293b);
      }
      .reason-textarea {
        width: 100%;
        box-sizing: border-box;
        padding: 0.5rem 0.75rem;
        border: 1px solid var(--p-inputtext-border-color, #cbd5e1);
        border-radius: 4px;
        font-size: 0.9rem;
        resize: vertical;
        font-family: inherit;
        color: var(--p-text-color, #1e293b);
        background: var(--p-inputtext-background, #fff);
      }
      .reason-textarea:focus {
        outline: none;
        border-color: var(--p-primary-color, #3b82f6);
        box-shadow: 0 0 0 2px var(--p-primary-50, #eff6ff);
      }
      .reason-hint {
        display: block;
        margin-top: 0.25rem;
        color: var(--p-text-muted-color, #94a3b8);
        font-size: 0.75rem;
      }
    `,
  ],
})
export class DlqDetailComponent implements OnInit {
  protected readonly DeadLetterStatus = DeadLetterStatus;

  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly dlqService = inject(DlqService);
  private readonly destroyRef = inject(DestroyRef);

  readonly loading = signal(false);
  readonly item = signal<DeadLetterDetail | null>(null);
  readonly retryLoading = signal(false);
  readonly discardLoading = signal(false);

  discardDialogVisible = false;
  discardReason = '';

  readonly actionMessage = signal<string | null>(null);
  readonly actionSeverity = signal<'success' | 'error'>('success');

  readonly formattedPayload = computed(() => {
    const p = this.item()?.rawPayload;
    if (!p) return '';
    if (typeof p === 'string') return p;
    try {
      return JSON.stringify(p, null, 2);
    } catch {
      return String(p);
    }
  });

  ngOnInit(): void {
    const id = this.route.snapshot.paramMap.get('id');
    if (!id) {
      this.router.navigate(['/dlq/list']);
      return;
    }
    this.loadItem(id);
  }

  goBack(): void {
    this.router.navigate(['/dlq/list']);
  }

  retry(): void {
    const id = this.item()?.id;
    if (!id) return;
    this.retryLoading.set(true);
    this.actionMessage.set(null);
    this.dlqService
      .retry(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.retryLoading.set(false);
          if (result.queued) {
            this.setAction('success', 'Item queued for retry. Status will update shortly.');
            this.loadItem(id);
          } else {
            this.setAction('error', result.error?.message ?? 'Retry failed.');
          }
        },
        error: () => {
          this.retryLoading.set(false);
          this.setAction('error', 'Retry request failed. Please try again.');
        },
      });
  }

  openDiscardDialog(): void {
    this.discardReason = '';
    this.discardDialogVisible = true;
  }

  confirmDiscard(): void {
    const reason = this.discardReason.trim();
    if (!reason) return;
    const id = this.item()?.id;
    if (!id) return;
    this.discardLoading.set(true);
    this.actionMessage.set(null);
    this.dlqService
      .discard(id, reason)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.discardLoading.set(false);
          this.discardDialogVisible = false;
          this.setAction('success', 'Item discarded successfully.');
          this.loadItem(id);
        },
        error: () => {
          this.discardLoading.set(false);
          this.setAction('error', 'Discard failed. Please try again.');
        },
      });
  }

  copyPayload(): void {
    navigator.clipboard.writeText(this.formattedPayload()).catch(() => {});
  }

  getStatusSeverity(status: DeadLetterStatus): PrimeSeverity {
    return statusSeverity(status);
  }

  private loadItem(id: string): void {
    this.loading.set(true);
    this.dlqService
      .getDeadLetterById(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (item) => {
          this.item.set(item);
          this.loading.set(false);
        },
        error: () => {
          this.item.set(null);
          this.loading.set(false);
        },
      });
  }

  private setAction(severity: 'success' | 'error', message: string): void {
    this.actionSeverity.set(severity);
    this.actionMessage.set(message);
    setTimeout(() => this.actionMessage.set(null), 5000);
  }
}
