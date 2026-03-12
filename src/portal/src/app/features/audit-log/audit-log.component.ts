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
import { MultiSelectModule } from 'primeng/multiselect';
import { InputTextModule } from 'primeng/inputtext';

import { AuditService } from '../../core/services/audit.service';
import { MasterDataService } from '../../core/services/master-data.service';
import { AuditEvent, AuditEventQueryParams, EventType } from '../../core/models/audit.model';
import { LegalEntity } from '../../core/models/master-data.model';
import { StatusBadgeComponent } from '../../shared/components/status-badge/status-badge.component';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { UtcDatePipe } from '../../shared/pipes/utc-date.pipe';
import {
  DateRangePickerComponent,
  DateRange,
} from '../../shared/components/date-range-picker/date-range-picker.component';

type PrimeSeverity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

const MAX_DATE_RANGE_DAYS = 30;

function eventTypeSeverity(eventType: EventType): PrimeSeverity {
  if (eventType.startsWith('Transaction')) return 'info';
  if (eventType.startsWith('PreAuth')) return 'secondary';
  if (eventType.startsWith('Reconciliation')) return 'warn';
  if (eventType.startsWith('Agent')) return 'success';
  if (eventType === 'ConnectivityChanged' || eventType === 'BufferThresholdExceeded') return 'danger';
  return 'contrast';
}

interface ListState {
  data: AuditEvent[];
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
  params: AuditEventQueryParams;
  page: number;
}

@Component({
  selector: 'app-audit-log',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    TableModule,
    CardModule,
    PanelModule,
    ButtonModule,
    SelectModule,
    MultiSelectModule,
    InputTextModule,
    DateRangePickerComponent,
    StatusBadgeComponent,
    EmptyStateComponent,
    UtcDatePipe,
  ],
  template: `
    <div class="page-container">
      <!-- Header -->
      <div class="page-header">
        <h1 class="page-title"><i class="pi pi-list"></i> Audit Log</h1>
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
          description="Choose a legal entity above to query the audit log."
        />
      } @else {
        <!-- Filters -->
        <p-panel header="Filters" [toggleable]="true" styleClass="filters-panel">
          <div class="filters-grid">
            <div class="filter-field filter-field--wide">
              <label for="audit-filter-correlation-id">Correlation ID <small class="hint">(exact match — primary trace key)</small></label>
              <input
                pInputText
                id="audit-filter-correlation-id"
                [(ngModel)]="filterCorrelationId"
                placeholder="UUID e.g. 550e8400-e29b-41d4-a716-446655440000"
              />
            </div>

            <div class="filter-field filter-field--wide">
              <label for="audit-filter-event-types">Event Types</label>
              <p-multiselect
                inputId="audit-filter-event-types"
                [options]="eventTypeOptions"
                [(ngModel)]="filterEventTypes"
                placeholder="All event types"
                [showClear]="true"
                [filter]="true"
                display="chip"
              />
            </div>

            <div class="filter-field">
              <label for="audit-filter-site-code">Site Code</label>
              <input pInputText id="audit-filter-site-code" [(ngModel)]="filterSiteCode" placeholder="e.g. MW-001" />
            </div>

            <div class="filter-field filter-field--wide">
              <label for="audit-filter-date-range">
                Date Range
                <small class="hint">(required for non-correlationId search; max {{ maxDays }} days)</small>
              </label>
              <app-date-range-picker
                id="audit-filter-date-range"
                placeholder="Select date range"
                [(ngModel)]="filterDateRange"
                (rangeSelected)="onDateRangeSelected($event)"
              />
              @if (dateRangeError()) {
                <small class="validation-error">{{ dateRangeError() }}</small>
              }
            </div>

            <div class="filter-field filter-field--actions">
              <p-button
                label="Search"
                icon="pi pi-search"
                (onClick)="search()"
              />
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

        <!-- Correlation trace banner -->
        @if (traceMode()) {
          <div class="trace-banner">
            <i class="pi pi-filter-fill"></i>
            Showing full lifecycle trace for correlation ID
            <code>{{ activeCorrelationId() }}</code>
            — events in chronological order
          </div>
        }

        <!-- Results table -->
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
            [expandedRowKeys]="expandedRowKeys"
            dataKey="eventId"
            styleClass="p-datatable-sm p-datatable-striped p-datatable-hoverable-rows"
            [tableStyle]="{ 'min-width': '900px' }"
          >
            <ng-template pTemplate="header">
              <tr>
                <th style="width: 3rem"></th>
                <th style="width: 12rem">Timestamp</th>
                <th style="width: 16rem">Event Type</th>
                <th style="width: 8rem">Site Code</th>
                <th>Source</th>
                <th style="width: 12rem">Correlation ID</th>
                <th style="width: 5rem"></th>
              </tr>
            </ng-template>

            <ng-template pTemplate="body" let-event let-expanded="expanded">
              <tr class="clickable-row" tabindex="0" (click)="toggleRow(event)" (keydown.enter)="toggleRow(event)">
                <td>
                  <i [class]="'pi ' + (expanded ? 'pi-chevron-down' : 'pi-chevron-right')"></i>
                </td>
                <td class="mono-sm">{{ event.timestamp | utcDate: 'medium' }}</td>
                <td>
                  <app-status-badge
                    [label]="event.eventType"
                    [severity]="getEventSeverity(event.eventType)"
                  />
                </td>
                <td>{{ event.siteCode ?? '—' }}</td>
                <td class="source-cell">{{ event.source }}</td>
                <td class="mono-sm">
                  <span [title]="event.correlationId">{{ event.correlationId | slice: 0 : 8 }}…</span>
                </td>
                <td>
                  <p-button
                    icon="pi pi-external-link"
                    severity="secondary"
                    size="small"
                    [text]="true"
                    pTooltip="View full detail"
                    (onClick)="viewDetail($event, event.eventId)"
                  />
                </td>
              </tr>
            </ng-template>

            <ng-template pTemplate="rowexpansion" let-event>
              <tr class="payload-row">
                <td colspan="7">
                  <div class="payload-container">
                    <div class="payload-header">
                      <span class="payload-title">Event Payload</span>
                      <span class="payload-schema">schema v{{ event.schemaVersion }}</span>
                    </div>
                    <pre class="payload-json">{{ formatPayload(event.payload) }}</pre>
                  </div>
                </td>
              </tr>
            </ng-template>

            <ng-template pTemplate="emptymessage">
              <tr>
                <td colspan="7">
                  @if (searched()) {
                    <app-empty-state
                      icon="pi-list"
                      title="No audit events found"
                      description="Try adjusting your filters or expanding the date range."
                    />
                  } @else {
                    <app-empty-state
                      icon="pi-search"
                      title="Run a search"
                      description="Set your filters above and click Search to query the audit log."
                    />
                  }
                </td>
              </tr>
            </ng-template>
          </p-table>
        </p-card>
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
      .filter-field .hint {
        font-weight: 400;
        text-transform: none;
        letter-spacing: 0;
        color: var(--p-text-muted-color, #94a3b8);
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
      .validation-error {
        color: var(--p-red-600, #dc2626);
        font-size: 0.78rem;
      }
      .trace-banner {
        display: flex;
        align-items: center;
        gap: 0.5rem;
        background: var(--p-blue-50, #eff6ff);
        border: 1px solid var(--p-blue-300, #93c5fd);
        border-radius: 6px;
        padding: 0.65rem 1rem;
        margin-bottom: 0.75rem;
        font-size: 0.875rem;
        color: var(--p-blue-800, #1e40af);
      }
      .trace-banner code {
        font-family: monospace;
        font-size: 0.82rem;
        background: var(--p-blue-100, #dbeafe);
        padding: 0.1rem 0.35rem;
        border-radius: 3px;
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
      .source-cell {
        font-size: 0.82rem;
        color: var(--p-text-muted-color, #475569);
      }
      .payload-row td {
        background: var(--p-surface-50, #f8fafc);
        padding: 0 !important;
      }
      .payload-container {
        padding: 0.75rem 1rem;
        border-top: 1px solid var(--p-surface-200, #e2e8f0);
      }
      .payload-header {
        display: flex;
        align-items: center;
        gap: 0.75rem;
        margin-bottom: 0.5rem;
      }
      .payload-title {
        font-size: 0.78rem;
        font-weight: 700;
        text-transform: uppercase;
        letter-spacing: 0.04em;
        color: var(--p-text-muted-color, #64748b);
      }
      .payload-schema {
        font-size: 0.72rem;
        background: var(--p-surface-200, #e2e8f0);
        padding: 0.1rem 0.4rem;
        border-radius: 3px;
        color: var(--p-text-muted-color, #64748b);
      }
      .payload-json {
        margin: 0;
        font-family: monospace;
        font-size: 0.8rem;
        white-space: pre-wrap;
        word-break: break-all;
        background: var(--p-surface-100, #f1f5f9);
        border: 1px solid var(--p-surface-200, #e2e8f0);
        border-radius: 4px;
        padding: 0.75rem;
        max-height: 320px;
        overflow-y: auto;
        color: var(--p-text-color, #1e293b);
      }
    `,
  ],
})
export class AuditLogComponent {
  private readonly auditService = inject(AuditService);
  private readonly masterDataService = inject(MasterDataService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly pageSize = 20;
  readonly maxDays = MAX_DATE_RANGE_DAYS;

  // ── Legal entity ──────────────────────────────────────────────────────────
  private readonly legalEntities = signal<LegalEntity[]>([]);
  readonly selectedLegalEntityId = signal<string | null>(null);
  readonly legalEntityOptions = computed(() =>
    this.legalEntities().map((e) => ({ label: `${e.name} (${e.code})`, value: e.id })),
  );

  // ── Filter state ──────────────────────────────────────────────────────────
  filterCorrelationId = '';
  filterEventTypes: EventType[] = [];
  filterSiteCode = '';
  filterDateRange: DateRange = { from: null, to: null };

  readonly dateRangeError = signal<string | null>(null);

  // ── Active search state (reflects the last executed search) ───────────────
  readonly activeCorrelationId = signal<string>('');
  readonly traceMode = signal(false);
  readonly searched = signal(false);

  // ── List state ────────────────────────────────────────────────────────────
  readonly listState = signal<ListState>(emptyListState());

  // ── Row expansion ─────────────────────────────────────────────────────────
  expandedRowKeys: { [key: string]: boolean } = {};

  // ── Event type options ────────────────────────────────────────────────────
  readonly eventTypeOptions = Object.values(EventType).map((v) => ({ label: v, value: v }));

  // ── Load subject ──────────────────────────────────────────────────────────
  private readonly load$ = new Subject<LoadRequest>();

  constructor() {
    this.masterDataService
      .getLegalEntities()
      .pipe(takeUntilDestroyed())
      .subscribe({ next: (entities) => this.legalEntities.set(entities) });

    this.load$
      .pipe(
        switchMap((req) => {
          this.listState.update((s) => ({ ...s, loading: true }));
          return this.auditService.getAuditEvents(req.params).pipe(
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
        this.expandedRowKeys = {};
        this.searched.set(true);
      });
  }

  // ── Event handlers ────────────────────────────────────────────────────────

  onLegalEntityChange(entityId: string | null): void {
    this.selectedLegalEntityId.set(entityId);
    this.listState.set(emptyListState());
    this.expandedRowKeys = {};
    this.searched.set(false);
    this.traceMode.set(false);
  }

  onDateRangeSelected(range: DateRange): void {
    this.filterDateRange = range;
    this.dateRangeError.set(null);
    if (range.from && range.to) {
      const diffDays = (range.to.getTime() - range.from.getTime()) / 86_400_000;
      if (diffDays > MAX_DATE_RANGE_DAYS) {
        this.dateRangeError.set(`Date range exceeds ${MAX_DATE_RANGE_DAYS} days. Please narrow the range.`);
      }
    }
  }

  search(): void {
    const entityId = this.selectedLegalEntityId();
    if (!entityId) return;

    const hasCorrelationId = this.filterCorrelationId.trim().length > 0;
    const hasDateRange = !!(this.filterDateRange.from && this.filterDateRange.to);

    // Date range required if no correlationId provided
    if (!hasCorrelationId && !hasDateRange) {
      this.dateRangeError.set('Date range is required when not searching by Correlation ID.');
      return;
    }

    // Enforce 30-day max when date range is provided
    if (hasDateRange) {
      const diffDays =
        (this.filterDateRange.to!.getTime() - this.filterDateRange.from!.getTime()) / 86_400_000;
      if (diffDays > MAX_DATE_RANGE_DAYS) {
        this.dateRangeError.set(`Date range exceeds ${MAX_DATE_RANGE_DAYS} days. Please narrow the range.`);
        return;
      }
    }

    this.dateRangeError.set(null);
    this.activeCorrelationId.set(this.filterCorrelationId.trim());
    this.traceMode.set(hasCorrelationId && !hasDateRange && this.filterEventTypes.length === 0 && !this.filterSiteCode.trim());

    const params: AuditEventQueryParams = { legalEntityId: entityId, pageSize: this.pageSize };
    if (this.filterCorrelationId.trim()) params.correlationId = this.filterCorrelationId.trim();
    if (this.filterEventTypes.length) params.eventTypes = this.filterEventTypes;
    if (this.filterSiteCode.trim()) params.siteCode = this.filterSiteCode.trim();
    if (hasDateRange) {
      params.from = this.filterDateRange.from!.toISOString();
      params.to = this.filterDateRange.to!.toISOString();
    }

    this.listState.set(emptyListState());
    this.expandedRowKeys = {};
    this.load$.next({ params, page: 0 });
  }

  clearFilters(): void {
    this.filterCorrelationId = '';
    this.filterEventTypes = [];
    this.filterSiteCode = '';
    this.filterDateRange = { from: null, to: null };
    this.dateRangeError.set(null);
    this.listState.set(emptyListState());
    this.expandedRowKeys = {};
    this.traceMode.set(false);
    this.searched.set(false);
  }

  onLazyLoad(event: TableLazyLoadEvent): void {
    const entityId = this.selectedLegalEntityId();
    if (!entityId || !this.searched()) return;

    const page = Math.floor((event.first ?? 0) / (event.rows ?? this.pageSize));
    this.listState.update((s) => ({ ...s, currentPage: page, tableFirst: event.first ?? 0 }));
    const cursor = this.listState().cursors[page] ?? undefined;

    const params: AuditEventQueryParams = { legalEntityId: entityId, pageSize: event.rows ?? this.pageSize };
    if (cursor) params.cursor = cursor;
    if (this.activeCorrelationId()) params.correlationId = this.activeCorrelationId();
    if (this.filterEventTypes.length) params.eventTypes = this.filterEventTypes;
    if (this.filterSiteCode.trim()) params.siteCode = this.filterSiteCode.trim();
    if (this.filterDateRange.from && this.filterDateRange.to) {
      params.from = this.filterDateRange.from.toISOString();
      params.to = this.filterDateRange.to.toISOString();
    }

    this.load$.next({ params, page });
  }

  toggleRow(event: AuditEvent): void {
    if (this.expandedRowKeys[event.eventId]) {
      const next = { ...this.expandedRowKeys };
      delete next[event.eventId];
      this.expandedRowKeys = next;
    } else {
      this.expandedRowKeys = { ...this.expandedRowKeys, [event.eventId]: true };
    }
  }

  viewDetail(mouseEvent: MouseEvent, eventId: string): void {
    mouseEvent.stopPropagation();
    this.router.navigate(['/audit/events', eventId]);
  }

  getEventSeverity(eventType: EventType): PrimeSeverity {
    return eventTypeSeverity(eventType);
  }

  formatPayload(payload: Record<string, unknown>): string {
    try {
      return JSON.stringify(payload, null, 2);
    } catch {
      return String(payload);
    }
  }
}
