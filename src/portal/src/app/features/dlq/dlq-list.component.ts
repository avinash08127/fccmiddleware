import { Component, DestroyRef, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subject, EMPTY } from 'rxjs';
import { switchMap, catchError } from 'rxjs/operators';
import { TableModule, TableLazyLoadEvent } from 'primeng/table';
import { CardModule } from 'primeng/card';
import { PanelModule } from 'primeng/panel';
import { ButtonModule } from 'primeng/button';
import { SelectModule } from 'primeng/select';
import { InputTextModule } from 'primeng/inputtext';
import { DialogModule } from 'primeng/dialog';
import { TooltipModule } from 'primeng/tooltip';

import { MsalService } from '@azure/msal-angular';
import { getCurrentAccount, hasAnyRequiredRole } from '../../core/auth/auth-state';
import { DlqService } from '../../core/services/dlq.service';
import { MasterDataService } from '../../core/services/master-data.service';
import {
  DeadLetter,
  DeadLetterQueryParams,
  DeadLetterStatus,
  DeadLetterReason,
  DeadLetterType,
} from '../../core/models/dlq.model';
import { LegalEntity } from '../../core/models/master-data.model';
import { StatusBadgeComponent } from '../../shared/components/status-badge/status-badge.component';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { UtcDatePipe } from '../../shared/pipes/utc-date.pipe';
import {
  DateRangePickerComponent,
  DateRange,
} from '../../shared/components/date-range-picker/date-range-picker.component';
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

interface ListState {
  data: DeadLetter[];
  loading: boolean;
  totalRecords: number;
  tableFirst: number;
  cursors: (string | null)[];
  currentPage: number;
}

function emptyListState(): ListState {
  return { data: [], loading: false, totalRecords: 0, tableFirst: 0, cursors: [null], currentPage: 0 };
}

interface LoadRequest {
  params: DeadLetterQueryParams;
  page: number;
}

@Component({
  selector: 'app-dlq-list',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    TableModule,
    CardModule,
    PanelModule,
    ButtonModule,
    SelectModule,
    InputTextModule,
    DialogModule,
    TooltipModule,
    DateRangePickerComponent,
    StatusBadgeComponent,
    EmptyStateComponent,
    UtcDatePipe,
    RoleVisibleDirective,
  ],
  template: `
    <div class="page-container">
      <!-- Header -->
      <div class="page-header">
        <h1 class="page-title"><i class="pi pi-inbox"></i> Dead-Letter Queue</h1>
        <div class="header-right">
          <p-select
            [options]="legalEntityOptions()"
            [ngModel]="selectedLegalEntityId()"
            (ngModelChange)="onLegalEntityChange($event)"
            optionLabel="label"
            optionValue="value"
            placeholder="Select Legal Entity"
            styleClass="entity-selector"
          />
        </div>
      </div>

      @if (!selectedLegalEntityId()) {
        <app-empty-state
          icon="pi-building"
          title="Select a Legal Entity"
          description="Choose a legal entity above to view the dead-letter queue."
        />
      } @else {
        <!-- Feedback banner -->
        @if (feedbackMessage()) {
          <div
            class="feedback-bar"
            [class.feedback-success]="feedbackSeverity() === 'success'"
            [class.feedback-error]="feedbackSeverity() === 'error'"
          >
            <i
              class="pi"
              [class.pi-check-circle]="feedbackSeverity() === 'success'"
              [class.pi-times-circle]="feedbackSeverity() === 'error'"
            ></i>
            {{ feedbackMessage() }}
          </div>
        }

        <!-- Filters -->
        <p-panel header="Filters" [toggleable]="true" styleClass="filters-panel">
          <div class="filters-grid">
            <div class="filter-field">
              <label for="dlq-filter-error-category">Error Category</label>
              <p-select
                inputId="dlq-filter-error-category"
                [options]="reasonOptions"
                [(ngModel)]="filterReason"
                optionLabel="label"
                optionValue="value"
                placeholder="All categories"
                [showClear]="true"
              />
            </div>

            <div class="filter-field">
              <label for="dlq-filter-site-code">Site Code</label>
              <input pInputText id="dlq-filter-site-code" [(ngModel)]="filterSiteCode" placeholder="e.g. MW-001" />
            </div>

            <div class="filter-field">
              <label for="dlq-filter-status">Status</label>
              <p-select
                inputId="dlq-filter-status"
                [options]="statusOptions"
                [(ngModel)]="filterStatus"
                optionLabel="label"
                optionValue="value"
                placeholder="All statuses"
                [showClear]="true"
              />
            </div>

            <div class="filter-field filter-field--wide">
              <label for="dlq-filter-date-range">Date Range</label>
              <app-date-range-picker
                id="dlq-filter-date-range"
                placeholder="Select date range"
                [(ngModel)]="filterDateRange"
              />
            </div>

            <div class="filter-field filter-field--actions">
              <p-button label="Search" icon="pi pi-search" (onClick)="search()" />
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

        <!-- Batch action bar (OpsManager+ only) -->
        <ng-container *appRoleVisible="['FccAdmin', 'FccUser']">
          @if (selectedItems.length > 0) {
            <div class="batch-actions">
              <span class="batch-count">{{ selectedItems.length }} item(s) selected</span>
              <p-button
                label="Retry Selected"
                icon="pi pi-refresh"
                severity="info"
                size="small"
                [loading]="batchActionLoading()"
                (onClick)="batchRetry()"
              />
              <p-button
                label="Discard Selected"
                icon="pi pi-ban"
                severity="danger"
                size="small"
                [loading]="batchActionLoading()"
                (onClick)="openBatchDiscardDialog()"
              />
            </div>
          }
        </ng-container>

        <!-- Table -->
        <p-card styleClass="table-card">
          <p-table
            [value]="listState().data"
            [lazy]="true"
            [loading]="listState().loading"
            [paginator]="true"
            [rows]="pageSize"
            [first]="listState().tableFirst"
            [totalRecords]="listState().totalRecords"
            [rowsPerPageOptions]="[20, 50, 100]"
            (onLazyLoad)="onLazyLoad($event)"
            [(selection)]="selectedItems"
            [selectionMode]="canBatchAction ? 'multiple' : undefined"
            dataKey="id"
            styleClass="p-datatable-sm p-datatable-striped p-datatable-hoverable-rows"
            [tableStyle]="{ 'min-width': '1100px' }"
          >
            <ng-template pTemplate="header">
              <tr>
                @if (canBatchAction) {
                  <th style="width: 3rem">
                    <p-tableHeaderCheckbox />
                  </th>
                }
                <th style="width: 9rem">Type</th>
                <th style="width: 13rem">Error Code</th>
                <th>Error Message</th>
                <th style="width: 8rem">Site Code</th>
                <th style="width: 11rem">Created At</th>
                <th style="width: 6rem">Retries</th>
                <th style="width: 11rem">Last Retry</th>
                <th style="width: 9rem">Status</th>
                <th style="width: 5rem"></th>
              </tr>
            </ng-template>

            <ng-template pTemplate="body" let-item>
              <tr class="clickable-row" tabindex="0" (click)="viewDetail(item.id)" (keydown.enter)="viewDetail(item.id)">
                @if (canBatchAction) {
                  <td (click)="$event.stopPropagation()">
                    <p-tableCheckbox [value]="item" />
                  </td>
                }
                <td>
                  <span class="type-badge" [class]="'type-badge--' + (item.type | lowercase)">
                    {{ formatType(item.type) }}
                  </span>
                </td>
                <td class="mono-sm">{{ item.errorCode }}</td>
                <td class="message-cell">{{ truncate(item.errorMessage, 80) }}</td>
                <td>{{ item.siteCode }}</td>
                <td>{{ item.createdAt | utcDate: 'short' }}</td>
                <td class="retry-cell">
                  <span [class.retry-high]="item.retryCount >= 3">{{ item.retryCount }}</span>
                </td>
                <td>{{ item.lastRetryAt ? (item.lastRetryAt | utcDate: 'short') : '—' }}</td>
                <td>
                  <app-status-badge
                    [label]="item.status"
                    [severity]="getStatusSeverity(item.status)"
                  />
                </td>
                <td (click)="$event.stopPropagation()">
                  <p-button
                    icon="pi pi-external-link"
                    severity="secondary"
                    size="small"
                    [text]="true"
                    pTooltip="View detail"
                    (onClick)="viewDetail(item.id)"
                  />
                </td>
              </tr>
            </ng-template>

            <ng-template pTemplate="emptymessage">
              <tr>
                <td colspan="10">
                  @if (searched()) {
                    <app-empty-state
                      icon="pi-check-circle"
                      title="No failed items"
                      description="No dead-letter queue entries match your filters."
                    />
                  } @else {
                    <app-empty-state
                      icon="pi-search"
                      title="Run a search"
                      description="Set your filters above and click Search to view failed items."
                    />
                  }
                </td>
              </tr>
            </ng-template>
          </p-table>
        </p-card>
      }

      <!-- Batch discard dialog -->
      <p-dialog
        header="Discard Selected Items"
        [(visible)]="batchDiscardDialogVisible"
        [modal]="true"
        [style]="{ width: '480px' }"
        [closable]="!batchActionLoading()"
      >
        <p class="dialog-body-text">
          You are about to permanently discard
          <strong>{{ selectedItems.length }} item(s)</strong>.
          This cannot be undone. Please provide a reason.
        </p>
        <textarea
          class="reason-textarea"
          [(ngModel)]="batchDiscardReason"
          rows="4"
          placeholder="Reason for discarding..."
          maxlength="500"
        ></textarea>
        <small class="reason-hint">{{ batchDiscardReason.length }}/500 characters (min. 10)</small>
        <ng-template pTemplate="footer">
          <p-button
            label="Cancel"
            severity="secondary"
            [disabled]="batchActionLoading()"
            (onClick)="batchDiscardDialogVisible = false"
          />
          <p-button
            label="Discard All"
            severity="danger"
            icon="pi pi-ban"
            [disabled]="batchDiscardReason.trim().length < 10 || batchActionLoading()"
            [loading]="batchActionLoading()"
            (onClick)="confirmBatchDiscard()"
          />
        </ng-template>
      </p-dialog>
    </div>
  `,
  styles: [
    `
      :host {
        display: block;
        padding: 1.5rem;
      }
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
      .entity-selector {
        min-width: 240px;
      }
      .filters-panel {
        margin-bottom: 1rem;
      }
      .filters-grid {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
        gap: 0.75rem 1rem;
        align-items: end;
      }
      .filter-field {
        display: flex;
        flex-direction: column;
        gap: 0.25rem;
      }
      .filter-field label {
        font-size: 0.78rem;
        font-weight: 600;
        color: var(--p-text-muted-color, #64748b);
        text-transform: uppercase;
        letter-spacing: 0.04em;
      }
      .filter-field--wide {
        grid-column: span 2;
      }
      .filter-field--actions {
        display: flex;
        flex-direction: row;
        align-items: flex-end;
        gap: 0.5rem;
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
      .batch-actions {
        display: flex;
        align-items: center;
        gap: 0.75rem;
        background: var(--p-blue-50, #eff6ff);
        border: 1px solid var(--p-blue-200, #bfdbfe);
        border-radius: 6px;
        padding: 0.6rem 1rem;
        margin-bottom: 0.75rem;
      }
      .batch-count {
        font-size: 0.875rem;
        font-weight: 600;
        color: var(--p-blue-800, #1e40af);
        margin-right: auto;
      }
      .table-card {
        margin-top: 0;
      }
      .clickable-row {
        cursor: pointer;
      }
      .mono-sm {
        font-family: monospace;
        font-size: 0.8rem;
      }
      .message-cell {
        font-size: 0.85rem;
        color: var(--p-text-color, #1e293b);
        max-width: 300px;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .retry-cell {
        text-align: center;
        font-weight: 600;
      }
      .retry-high {
        color: var(--p-red-600, #dc2626);
      }
      .type-badge {
        display: inline-block;
        padding: 0.15rem 0.5rem;
        border-radius: 4px;
        font-size: 0.75rem;
        font-weight: 600;
        text-transform: uppercase;
        background: var(--p-surface-200, #e2e8f0);
        color: var(--p-text-muted-color, #475569);
      }
      .type-badge--transaction {
        background: var(--p-blue-100, #dbeafe);
        color: var(--p-blue-700, #1d4ed8);
      }
      .type-badge--pre_auth {
        background: var(--p-purple-100, #f3e8ff);
        color: var(--p-purple-700, #7e22ce);
      }
      .type-badge--telemetry {
        background: var(--p-green-100, #dcfce7);
        color: var(--p-green-700, #15803d);
      }
      .dialog-body-text {
        margin: 0 0 1rem;
        font-size: 0.9rem;
        color: var(--p-text-color, #1e293b);
        line-height: 1.5;
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
export class DlqListComponent {
  private readonly dlqService = inject(DlqService);
  private readonly masterDataService = inject(MasterDataService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly msalService = inject(MsalService);

  readonly canBatchAction = hasAnyRequiredRole(
    null,
    ['FccAdmin', 'FccUser'],
  );

  pageSize = 20;

  // ── Legal entity ──────────────────────────────────────────────────────────
  private readonly legalEntities = signal<LegalEntity[]>([]);
  readonly selectedLegalEntityId = signal<string | null>(null);
  readonly legalEntityOptions = computed(() =>
    this.legalEntities().map((e) => ({ label: `${e.name} (${e.code})`, value: e.id })),
  );

  // ── Filter state ──────────────────────────────────────────────────────────
  filterReason: DeadLetterReason | null = null;
  filterSiteCode = '';
  filterStatus: DeadLetterStatus | null = null;
  filterDateRange: DateRange = { from: null, to: null };

  // ── Filter options ────────────────────────────────────────────────────────
  readonly reasonOptions = Object.values(DeadLetterReason).map((v) => ({
    label: v.replace(/_/g, ' '),
    value: v,
  }));
  readonly statusOptions = Object.values(DeadLetterStatus).map((v) => ({
    label: v.charAt(0) + v.slice(1).toLowerCase(),
    value: v,
  }));

  // ── List state ────────────────────────────────────────────────────────────
  readonly listState = signal<ListState>(emptyListState());
  readonly searched = signal(false);

  // ── Table selection ───────────────────────────────────────────────────────
  selectedItems: DeadLetter[] = [];

  // ── Batch actions ─────────────────────────────────────────────────────────
  readonly batchActionLoading = signal(false);
  batchDiscardDialogVisible = false;
  batchDiscardReason = '';

  // ── Feedback ──────────────────────────────────────────────────────────────
  readonly feedbackMessage = signal<string | null>(null);
  readonly feedbackSeverity = signal<'success' | 'error'>('success');

  // ── Load subject ──────────────────────────────────────────────────────────
  private readonly load$ = new Subject<LoadRequest>();

  constructor() {
    this.destroyRef.onDestroy(() => {
      if (this.feedbackTimer !== null) {
        clearTimeout(this.feedbackTimer);
      }
    });

    this.masterDataService
      .getLegalEntities()
      .pipe(takeUntilDestroyed())
      .subscribe({ next: (entities) => this.legalEntities.set(entities) });

    this.load$
      .pipe(
        switchMap((req) => {
          this.listState.update((s) => ({ ...s, loading: true }));
          return this.dlqService.getDeadLetters(req.params).pipe(
            catchError(() => {
              this.listState.update((s) => ({ ...s, loading: false, data: [] }));
              return EMPTY;
            }),
          );
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((result) => {
        this.listState.update((s) => {
          const next = { ...s, loading: false, data: result.data };
          if (result.meta.nextCursor != null) next.cursors[s.currentPage + 1] = result.meta.nextCursor;
          next.totalRecords =
            result.meta.totalCount ??
            (result.meta.hasMore
              ? (s.currentPage + 2) * this.pageSize
              : s.currentPage * this.pageSize + result.data.length);
          return next;
        });
        this.selectedItems = [];
        this.searched.set(true);
      });
  }

  // ── Event handlers ────────────────────────────────────────────────────────

  onLegalEntityChange(entityId: string | null): void {
    this.selectedLegalEntityId.set(entityId);
    this.listState.set(emptyListState());
    this.selectedItems = [];
    this.searched.set(false);
    this.feedbackMessage.set(null);
    if (entityId) {
      this.triggerLoad(entityId, 0);
    }
  }

  search(): void {
    const entityId = this.selectedLegalEntityId();
    if (!entityId) return;
    this.searched.set(false);
    this.listState.set(emptyListState());
    this.selectedItems = [];
    this.triggerLoad(entityId, 0);
  }

  clearFilters(): void {
    this.filterReason = null;
    this.filterSiteCode = '';
    this.filterStatus = null;
    this.filterDateRange = { from: null, to: null };
    const entityId = this.selectedLegalEntityId();
    if (!entityId) return;
    this.listState.set(emptyListState());
    this.selectedItems = [];
    this.triggerLoad(entityId, 0);
  }

  onLazyLoad(event: TableLazyLoadEvent): void {
    const entityId = this.selectedLegalEntityId();
    if (!entityId || !this.searched()) return;
    const rows = event.rows ?? this.pageSize;

    // When the user changes page size, cursors from the old page size are invalid.
    // Reset to page 0 and reload with the new page size.
    if (rows !== this.pageSize) {
      this.pageSize = rows;
      this.listState.update((s) => ({ ...s, currentPage: 0, tableFirst: 0, cursors: [null] }));
      this.triggerLoad(entityId, 0, rows);
      return;
    }

    const page = Math.floor((event.first ?? 0) / rows);
    this.listState.update((s) => ({ ...s, currentPage: page, tableFirst: event.first ?? 0 }));
    const cursor = this.listState().cursors[page] ?? undefined;
    this.triggerLoad(entityId, page, rows, cursor);
  }

  viewDetail(id: string): void {
    this.router.navigate(['/dlq/items', id]);
  }

  batchRetry(): void {
    const ids = this.selectedItems.map((i) => i.id);
    if (!ids.length) return;
    this.batchActionLoading.set(true);
    this.feedbackMessage.set(null);
    this.dlqService
      .retryBatch(ids)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.batchActionLoading.set(false);
          const s = result.succeeded.length;
          const f = result.failed.length;
          if (f === 0) {
            this.setFeedback('success', `${s} item(s) queued for retry successfully.`);
          } else {
            this.setFeedback('error', `${s} retried, ${f} failed. Review items for details.`);
          }
          this.selectedItems = [];
          this.refreshCurrentPage();
        },
        error: () => {
          this.batchActionLoading.set(false);
          this.setFeedback('error', 'Batch retry failed. Please try again.');
        },
      });
  }

  openBatchDiscardDialog(): void {
    this.batchDiscardReason = '';
    this.batchDiscardDialogVisible = true;
  }

  confirmBatchDiscard(): void {
    const reason = this.batchDiscardReason.trim();
    if (reason.length < 10) return;
    const items = this.selectedItems.map((i) => ({ id: i.id, reason }));
    this.batchActionLoading.set(true);
    this.feedbackMessage.set(null);
    this.dlqService
      .discardBatch(items)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.batchActionLoading.set(false);
          this.batchDiscardDialogVisible = false;
          const s = result.succeeded.length;
          const f = result.failed.length;
          if (f === 0) {
            this.setFeedback('success', `${s} item(s) discarded successfully.`);
          } else {
            this.setFeedback('error', `${s} discarded, ${f} failed. Review items for details.`);
          }
          this.selectedItems = [];
          this.refreshCurrentPage();
        },
        error: () => {
          this.batchActionLoading.set(false);
          this.setFeedback('error', 'Batch discard failed. Please try again.');
        },
      });
  }

  getStatusSeverity(status: DeadLetterStatus): PrimeSeverity {
    return statusSeverity(status);
  }

  formatType(type: DeadLetterType): string {
    switch (type) {
      case DeadLetterType.TRANSACTION:
        return 'Transaction';
      case DeadLetterType.PRE_AUTH:
        return 'Pre-Auth';
      case DeadLetterType.TELEMETRY:
        return 'Telemetry';
      default:
        return 'Unknown';
    }
  }

  truncate(text: string, max: number): string {
    return text.length > max ? text.slice(0, max) + '…' : text;
  }

  // ── Private helpers ───────────────────────────────────────────────────────

  private buildParams(
    entityId: string,
    pageSize: number,
    cursor?: string,
  ): DeadLetterQueryParams {
    const params: DeadLetterQueryParams = { legalEntityId: entityId, pageSize };
    if (cursor) params.cursor = cursor;
    if (this.filterReason) params.failureReason = this.filterReason;
    if (this.filterSiteCode.trim()) params.siteCode = this.filterSiteCode.trim();
    if (this.filterStatus) params.status = this.filterStatus;
    if (this.filterDateRange.from) params.from = this.filterDateRange.from.toISOString();
    if (this.filterDateRange.to) params.to = this.filterDateRange.to.toISOString();
    return params;
  }

  private triggerLoad(entityId: string, page: number, pageSize = this.pageSize, cursor?: string): void {
    const params = this.buildParams(entityId, pageSize, cursor);
    this.load$.next({ params, page });
  }

  private refreshCurrentPage(): void {
    const entityId = this.selectedLegalEntityId();
    if (!entityId) return;
    const state = this.listState();
    const cursor = state.cursors[state.currentPage] ?? undefined;
    this.triggerLoad(entityId, state.currentPage, this.pageSize, cursor);
  }

  private feedbackTimer: ReturnType<typeof setTimeout> | null = null;

  private setFeedback(severity: 'success' | 'error', message: string): void {
    if (this.feedbackTimer !== null) {
      clearTimeout(this.feedbackTimer);
      this.feedbackTimer = null;
    }
    this.feedbackSeverity.set(severity);
    this.feedbackMessage.set(message);
    // Only auto-dismiss success messages; errors stay until the user acts.
    if (severity === 'success') {
      this.feedbackTimer = setTimeout(() => {
        this.feedbackMessage.set(null);
        this.feedbackTimer = null;
      }, 5000);
    }
  }
}
