import { Component, DestroyRef, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subject, EMPTY } from 'rxjs';
import { switchMap, catchError, map } from 'rxjs/operators';
import { TableModule, TableLazyLoadEvent } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { CardModule } from 'primeng/card';
import { SelectModule } from 'primeng/select';
import { FormsModule } from '@angular/forms';

import { TransactionService } from '../../core/services/transaction.service';
import { MasterDataService } from '../../core/services/master-data.service';
import { SiteService } from '../../core/services/site.service';
import {
  Transaction,
  TransactionStatus,
  TransactionQueryParams,
} from '../../core/models/transaction.model';
import { LegalEntity } from '../../core/models/master-data.model';
import { StatusBadgeComponent } from '../../shared/components/status-badge/status-badge.component';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { CurrencyMinorUnitsPipe } from '../../shared/pipes/currency-minor-units.pipe';
import { UtcDatePipe } from '../../shared/pipes/utc-date.pipe';
import { StatusLabelPipe } from '../../shared/pipes/status-label.pipe';
import {
  TransactionFiltersComponent,
  TransactionFilters,
  EMPTY_FILTERS,
} from './transaction-filters.component';

type PrimeSeverity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

function txSeverity(status: TransactionStatus): PrimeSeverity {
  switch (status) {
    case TransactionStatus.SYNCED_TO_ODOO:
      return 'success';
    case TransactionStatus.PENDING:
      return 'warn';
    case TransactionStatus.DUPLICATE:
    case TransactionStatus.ARCHIVED:
      return 'secondary';
    default:
      return 'info';
  }
}

interface LoadRequest {
  entityId: string;
  cursor: string | undefined;
  pageSize: number;
  filters: TransactionFilters;
  sortField: string | null;
  sortOrder: 'asc' | 'desc';
}

@Component({
  selector: 'app-transaction-list',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    TableModule,
    ButtonModule,
    CardModule,
    SelectModule,
    TransactionFiltersComponent,
    StatusBadgeComponent,
    EmptyStateComponent,
    CurrencyMinorUnitsPipe,
    UtcDatePipe,
    StatusLabelPipe,
  ],
  template: `
    <div class="page-container">
      <!-- Header -->
      <div class="page-header">
        <h1 class="page-title"><i class="pi pi-list"></i> Transaction Browser</h1>
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
          description="Choose a legal entity above to browse transactions."
        />
      } @else {
        <!-- Filters -->
        <app-transaction-filters
          [siteOptions]="siteOptions()"
          (filtersChange)="onFiltersChange($event)"
        />

        <!-- Table -->
        <p-card styleClass="table-card">
          <p-table
            [value]="transactions()"
            [lazy]="true"
            [loading]="loading()"
            [paginator]="true"
            [rows]="pageSize"
            [first]="tableFirst()"
            [totalRecords]="totalRecords()"
            [rowsPerPageOptions]="[20, 50, 100]"
            sortMode="single"
            (onLazyLoad)="onLazyLoad($event)"
            styleClass="p-datatable-sm p-datatable-striped p-datatable-hoverable-rows"
            [tableStyle]="{ 'min-width': '1000px' }"
          >
            <ng-template pTemplate="header">
              <tr>
                <th pSortableColumn="fccTransactionId" style="width:14rem">
                  Transaction ID <p-sortIcon field="fccTransactionId" />
                </th>
                <th pSortableColumn="siteCode">Site <p-sortIcon field="siteCode" /></th>
                <th style="width:5rem">Pump</th>
                <th>Product</th>
                <th pSortableColumn="volumeMicrolitres" style="width:8rem">
                  Volume <p-sortIcon field="volumeMicrolitres" />
                </th>
                <th pSortableColumn="amountMinorUnits" style="width:10rem">
                  Amount <p-sortIcon field="amountMinorUnits" />
                </th>
                <th pSortableColumn="status" style="width:9rem">
                  Status <p-sortIcon field="status" />
                </th>
                <th pSortableColumn="startedAt" style="width:11rem">
                  Started At <p-sortIcon field="startedAt" />
                </th>
                <th>Source</th>
              </tr>
            </ng-template>

            <ng-template pTemplate="body" let-tx>
              <tr class="clickable-row" tabindex="0" (click)="onRowClick(tx)" (keydown.enter)="onRowClick(tx)">
                <td><code class="tx-id">{{ tx.fccTransactionId }}</code></td>
                <td>{{ tx.siteCode }}</td>
                <td>{{ tx.pumpNumber }}</td>
                <td>{{ tx.productCode }}</td>
                <td>{{ formatVolume(tx.volumeMicrolitres) }}</td>
                <td>{{ tx.amountMinorUnits | currencyMinorUnits: tx.currencyCode }}</td>
                <td>
                  <app-status-badge
                    [label]="tx.status | statusLabel"
                    [severity]="getSeverity(tx.status)"
                  />
                </td>
                <td>{{ tx.startedAt | utcDate: 'short' }}</td>
                <td>{{ tx.ingestionSource | statusLabel }}</td>
              </tr>
            </ng-template>

            <ng-template pTemplate="emptymessage">
              <tr>
                <td colspan="9">
                  <app-empty-state
                    icon="pi-filter-slash"
                    title="No transactions found"
                    description="Try adjusting your filters or selecting a wider date range."
                  />
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
      app-transaction-filters {
        display: block;
        margin-bottom: 1rem;
      }
      .table-card {
        margin-top: 0;
      }
      .clickable-row {
        cursor: pointer;
      }
      .tx-id {
        font-family: monospace;
        font-size: 0.78rem;
        background: var(--p-surface-100, #f1f5f9);
        padding: 0.1rem 0.35rem;
        border-radius: 4px;
      }
    `,
  ],
})
export class TransactionListComponent {
  private readonly txService = inject(TransactionService);
  private readonly masterDataService = inject(MasterDataService);
  private readonly siteService = inject(SiteService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly pageSize = 20;

  // ── Legal entity ──────────────────────────────────────────────────────────
  private readonly legalEntities = signal<LegalEntity[]>([]);
  readonly selectedLegalEntityId = signal<string | null>(null);
  readonly legalEntityOptions = computed(() =>
    this.legalEntities().map((e) => ({ label: `${e.name} (${e.code})`, value: e.id })),
  );

  // ── Site filter options ───────────────────────────────────────────────────
  readonly siteOptions = signal<{ label: string; value: string }[]>([]);

  // ── Table state ───────────────────────────────────────────────────────────
  readonly transactions = signal<Transaction[]>([]);
  readonly totalRecords = signal(0);
  readonly loading = signal(false);
  readonly tableFirst = signal(0);

  // ── Cursor stack (index = page number) ───────────────────────────────────
  private cursors: (string | null)[] = [null];
  private currentPage = 0;

  // ── Active filters & sort ─────────────────────────────────────────────────
  private currentFilters: TransactionFilters = {
    ...EMPTY_FILTERS,
    dateRange: { from: null, to: null },
  };
  private sortField: string | null = null;
  private sortOrder: 'asc' | 'desc' = 'desc';

  // ── Load trigger (switchMap cancels in-flight requests) ───────────────────
  private readonly load$ = new Subject<LoadRequest>();
  private readonly loadSites$ = new Subject<string>();

  constructor() {
    this.masterDataService
      .getLegalEntities()
      .pipe(takeUntilDestroyed())
      .subscribe({ next: (entities) => this.legalEntities.set(entities) });

    this.loadSites$
      .pipe(
        switchMap((entityId) =>
          this.siteService.getSites({ legalEntityId: entityId, pageSize: 500 }).pipe(
            map((result) =>
              result.data.map((s) => ({ label: `${s.siteName} (${s.siteCode})`, value: s.siteCode })),
            ),
            catchError(() => {
              this.siteOptions.set([]);
              return EMPTY;
            }),
          ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((options) => this.siteOptions.set(options));

    this.load$
      .pipe(
        switchMap((req) => {
          this.loading.set(true);
          const params = this.buildParams(req);
          return this.txService.getTransactions(params).pipe(
            catchError(() => {
              this.transactions.set([]);
              this.totalRecords.set(0);
              this.loading.set(false);
              return EMPTY;
            }),
          );
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((result) => {
        this.transactions.set(result.data);

        // Store next-page cursor
        if (result.meta.nextCursor != null) {
          this.cursors[this.currentPage + 1] = result.meta.nextCursor;
        }

        // Estimate total records for paginator
        this.totalRecords.set(
          result.meta.totalCount ??
            (result.meta.hasMore
              ? (this.currentPage + 2) * this.pageSize
              : this.currentPage * this.pageSize + result.data.length),
        );
        this.loading.set(false);
      });
  }

  // ── Event handlers ────────────────────────────────────────────────────────

  onLegalEntityChange(entityId: string | null): void {
    this.selectedLegalEntityId.set(entityId);
    if (!entityId) return;

    this.loadSitesForEntity(entityId);
    this.resetState();
    // Table becomes visible → onLazyLoad fires automatically with first=0
  }

  onFiltersChange(filters: TransactionFilters): void {
    this.currentFilters = filters;
    this.resetState();
    this.triggerLoad(undefined);
  }

  onLazyLoad(event: TableLazyLoadEvent): void {
    const entityId = this.selectedLegalEntityId();
    if (!entityId) return;

    const page = Math.floor((event.first ?? 0) / (event.rows ?? this.pageSize));
    const rows = event.rows ?? this.pageSize;

    if (typeof event.sortField === 'string' && event.sortField) {
      this.sortField = event.sortField;
      this.sortOrder = event.sortOrder === 1 ? 'asc' : 'desc';
    }

    this.currentPage = page;
    const cursor = page < this.cursors.length ? (this.cursors[page] ?? undefined) : undefined;
    this.triggerLoad(cursor, rows);
  }

  onRowClick(tx: Transaction): void {
    this.router.navigate(['/transactions', tx.id]);
  }

  getSeverity(status: TransactionStatus): PrimeSeverity {
    return txSeverity(status);
  }

  formatVolume(microlitres: number): string {
    return `${(microlitres / 1_000_000).toFixed(3)} L`;
  }

  // ── Private helpers ───────────────────────────────────────────────────────

  private resetState(): void {
    this.cursors = [null];
    this.currentPage = 0;
    this.tableFirst.set(0);
    this.transactions.set([]);
    this.totalRecords.set(0);
  }

  private triggerLoad(cursor: string | undefined, rows = this.pageSize): void {
    const entityId = this.selectedLegalEntityId();
    if (!entityId) return;

    this.load$.next({
      entityId,
      cursor,
      pageSize: rows,
      filters: this.currentFilters,
      sortField: this.sortField,
      sortOrder: this.sortOrder,
    });
  }

  private buildParams(req: LoadRequest): TransactionQueryParams {
    const p: TransactionQueryParams = {
      legalEntityId: req.entityId,
      pageSize: req.pageSize,
    };

    if (req.cursor) p.cursor = req.cursor;
    if (req.filters.siteCode) p.siteCode = req.filters.siteCode;
    if (req.filters.status) p.status = req.filters.status;
    if (req.filters.fccVendor) p.fccVendor = req.filters.fccVendor;
    if (req.filters.ingestionSource) p.ingestionSource = req.filters.ingestionSource;
    if (req.filters.fccTransactionId) p.fccTransactionId = req.filters.fccTransactionId;
    if (req.filters.odooOrderId) p.odooOrderId = req.filters.odooOrderId;
    if (req.filters.pumpNumber != null) p.pumpNumber = req.filters.pumpNumber;
    if (req.filters.isStale) p.isStale = true;
    if (req.filters.dateRange.from) p.from = req.filters.dateRange.from.toISOString();
    if (req.filters.dateRange.to) p.to = req.filters.dateRange.to.toISOString();
    if (req.sortField) {
      p.sortField = req.sortField;
      p.sortOrder = req.sortOrder;
    }

    return p;
  }

  private loadSitesForEntity(entityId: string): void {
    this.siteOptions.set([]);
    this.loadSites$.next(entityId);
  }
}
