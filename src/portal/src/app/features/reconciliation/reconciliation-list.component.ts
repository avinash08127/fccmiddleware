import { Component, DestroyRef, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subject, EMPTY } from 'rxjs';
import { switchMap, catchError, map } from 'rxjs/operators';
import { TableModule, TableLazyLoadEvent } from 'primeng/table';
import { TabsModule } from 'primeng/tabs';
import { ButtonModule } from 'primeng/button';
import { CardModule } from 'primeng/card';
import { SelectModule } from 'primeng/select';
import { BadgeModule } from 'primeng/badge';
import { FormsModule } from '@angular/forms';

import { ReconciliationService } from '../../core/services/reconciliation.service';
import { MasterDataService } from '../../core/services/master-data.service';
import { SiteService } from '../../core/services/site.service';
import {
  ReconciliationException,
  ReconciliationQueryParams,
} from '../../core/models/reconciliation.model';
import { ReconciliationStatus } from '../../core/models/transaction.model';
import { LegalEntity } from '../../core/models/master-data.model';
import { StatusBadgeComponent } from '../../shared/components/status-badge/status-badge.component';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { CurrencyMinorUnitsPipe } from '../../shared/pipes/currency-minor-units.pipe';
import { UtcDatePipe } from '../../shared/pipes/utc-date.pipe';
import { StatusLabelPipe } from '../../shared/pipes/status-label.pipe';
import {
  ReconciliationFiltersComponent,
  ReconciliationFilters,
  EMPTY_RECON_FILTERS,
} from './reconciliation-filters.component';

type PrimeSeverity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

function statusSeverity(status: ReconciliationStatus): PrimeSeverity {
  switch (status) {
    case ReconciliationStatus.APPROVED:
      return 'success';
    case ReconciliationStatus.VARIANCE_FLAGGED:
      return 'danger';
    case ReconciliationStatus.UNMATCHED:
      return 'warn';
    case ReconciliationStatus.REJECTED:
      return 'secondary';
    case ReconciliationStatus.REVIEW_FUZZY_MATCH:
      return 'info';
    case ReconciliationStatus.MATCHED:
    case ReconciliationStatus.VARIANCE_WITHIN_TOLERANCE:
      return 'success';
    default:
      return 'info';
  }
}

function resolveVariancePercent(
  variancePercent: number | null | undefined,
  varianceBps: number | null | undefined,
): number | null {
  if (variancePercent != null) {
    return variancePercent;
  }

  return varianceBps == null ? null : varianceBps / 100;
}

// F04-06: error field surfaces API failures in the empty template
// F04-04: pageSize field detects when user changes rows-per-page
interface TabState {
  data: ReconciliationException[];
  loading: boolean;
  totalRecords: number;
  tableFirst: number;
  cursors: (string | null)[];
  currentPage: number;
  error: string | null;
  pageSize: number;
}

function emptyTabState(pageSize = 20): TabState {
  return {
    data: [],
    loading: false,
    totalRecords: 0,
    tableFirst: 0,
    cursors: [null],
    currentPage: 0,
    error: null,
    pageSize,
  };
}

interface LoadRequest {
  entityId: string;
  cursor: string | undefined;
  pageSize: number;
  filters: ReconciliationFilters;
}

@Component({
  selector: 'app-reconciliation-list',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    TableModule,
    TabsModule,
    ButtonModule,
    CardModule,
    SelectModule,
    BadgeModule,
    ReconciliationFiltersComponent,
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
        <h1 class="page-title"><i class="pi pi-sync"></i> Reconciliation Workbench</h1>
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
          description="Choose a legal entity above to view reconciliation exceptions."
        />
      } @else {
        <!-- Filters -->
        <app-reconciliation-filters
          [siteOptions]="siteOptions()"
          (filtersChange)="onFiltersChange($event)"
        />

        <!-- Tabs -->
        <p-tabs [value]="activeTab()" (valueChange)="onTabChange($event)">
          <p-tablist>
            <p-tab value="variance">
              Variance Flagged
              @if (varianceTab().totalRecords > 0) {
                <!-- F04-05: pass string to p-badge [value] -->
                <p-badge [value]="varianceTab().totalRecords.toString()" severity="danger" styleClass="tab-badge" />
              }
            </p-tab>
            <p-tab value="unmatched">
              Unmatched
              @if (unmatchedTab().totalRecords > 0) {
                <!-- F04-05: pass string to p-badge [value] -->
                <p-badge [value]="unmatchedTab().totalRecords.toString()" severity="warn" styleClass="tab-badge" />
              }
            </p-tab>
            <!-- F04-01: Fuzzy Match tab so REVIEW_FUZZY_MATCH records are visible -->
            <p-tab value="fuzzy">
              Fuzzy Match
              @if (fuzzyTab().totalRecords > 0) {
                <p-badge [value]="fuzzyTab().totalRecords.toString()" severity="info" styleClass="tab-badge" />
              }
            </p-tab>
            <p-tab value="reviewed">Reviewed</p-tab>
          </p-tablist>

          <p-tabpanels>
            <!-- ── Variance Flagged ── -->
            <p-tabpanel value="variance">
              <p-card styleClass="table-card">
                <p-table
                  [value]="varianceTab().data"
                  [lazy]="true"
                  [loading]="varianceTab().loading"
                  [paginator]="true"
                  [rows]="varianceTab().pageSize"
                  [first]="varianceTab().tableFirst"
                  [totalRecords]="varianceTab().totalRecords"
                  [rowsPerPageOptions]="[20, 50, 100]"
                  (onLazyLoad)="onVarianceLazyLoad($event)"
                  styleClass="p-datatable-sm p-datatable-striped p-datatable-hoverable-rows"
                  [tableStyle]="{ 'min-width': '1100px' }"
                >
                  <ng-template pTemplate="header">
                    <tr>
                      <th>Site</th>
                      <th style="width:5rem">Pump</th>
                      <th style="width:5rem">Nozzle</th>
                      <th style="width:10rem">Authorised Amt</th>
                      <th style="width:10rem">Actual Amt</th>
                      <th style="width:10rem">Variance</th>
                      <th style="width:7rem">Var %</th>
                      <th>Match Method</th>
                      <th style="width:9rem">Status</th>
                      <th style="width:11rem">Created At</th>
                    </tr>
                  </ng-template>
                  <ng-template pTemplate="body" let-ex>
                    <tr class="clickable-row" tabindex="0" (click)="onRowClick(ex)" (keydown.enter)="onRowClick(ex)">
                      <td>{{ ex.siteCode }}</td>
                      <td>{{ ex.pumpNumber }}</td>
                      <td>{{ ex.nozzleNumber }}</td>
                      <td>{{ ex.requestedAmount | currencyMinorUnits: ex.currencyCode }}</td>
                      <td>{{ ex.actualAmount !== null ? (ex.actualAmount | currencyMinorUnits: ex.currencyCode) : '—' }}</td>
                      <td>
                        <span [class]="varianceClass(ex)">
                          {{ formatVariance(ex) }}
                        </span>
                      </td>
                      <td [class]="variancePctClass(ex)">{{ formatVariancePct(ex) }}</td>
                      <td>{{ ex.matchMethod ?? '—' }}</td>
                      <td>
                        <app-status-badge
                          [label]="ex.status | statusLabel"
                          [severity]="getSeverity(ex.status)"
                        />
                        @if (ex.ambiguityFlag) {
                          <i class="pi pi-exclamation-triangle ambiguity-icon" title="Ambiguous match"></i>
                        }
                      </td>
                      <td>{{ ex.createdAt | utcDate: 'short' }}</td>
                    </tr>
                  </ng-template>
                  <!-- F04-06: show error message instead of generic empty state on API failure -->
                  <ng-template pTemplate="emptymessage">
                    <tr>
                      <td colspan="10">
                        @if (varianceTab().error) {
                          <app-empty-state
                            icon="pi-exclamation-circle"
                            title="Failed to load data"
                            [description]="varianceTab().error!"
                          />
                        } @else {
                          <app-empty-state
                            icon="pi-check-circle"
                            title="No variance flagged exceptions"
                            description="All variances are within tolerance or have been reviewed."
                          />
                        }
                      </td>
                    </tr>
                  </ng-template>
                </p-table>
              </p-card>
            </p-tabpanel>

            <!-- ── Unmatched ── -->
            <p-tabpanel value="unmatched">
              <p-card styleClass="table-card">
                <p-table
                  [value]="unmatchedTab().data"
                  [lazy]="true"
                  [loading]="unmatchedTab().loading"
                  [paginator]="true"
                  [rows]="unmatchedTab().pageSize"
                  [first]="unmatchedTab().tableFirst"
                  [totalRecords]="unmatchedTab().totalRecords"
                  [rowsPerPageOptions]="[20, 50, 100]"
                  (onLazyLoad)="onUnmatchedLazyLoad($event)"
                  styleClass="p-datatable-sm p-datatable-striped p-datatable-hoverable-rows"
                  [tableStyle]="{ 'min-width': '1100px' }"
                >
                  <ng-template pTemplate="header">
                    <tr>
                      <th>Site</th>
                      <th style="width:5rem">Pump</th>
                      <th style="width:5rem">Nozzle</th>
                      <th style="width:10rem">Authorised Amt</th>
                      <th style="width:10rem">Actual Amt</th>
                      <th style="width:10rem">Variance</th>
                      <th style="width:7rem">Var %</th>
                      <th>Match Method</th>
                      <th style="width:9rem">Status</th>
                      <th style="width:11rem">Created At</th>
                    </tr>
                  </ng-template>
                  <ng-template pTemplate="body" let-ex>
                    <tr class="clickable-row" tabindex="0" (click)="onRowClick(ex)" (keydown.enter)="onRowClick(ex)">
                      <td>{{ ex.siteCode }}</td>
                      <td>{{ ex.pumpNumber }}</td>
                      <td>{{ ex.nozzleNumber }}</td>
                      <td>{{ ex.requestedAmount | currencyMinorUnits: ex.currencyCode }}</td>
                      <td>{{ ex.actualAmount !== null ? (ex.actualAmount | currencyMinorUnits: ex.currencyCode) : '—' }}</td>
                      <td>
                        <span [class]="varianceClass(ex)">
                          {{ formatVariance(ex) }}
                        </span>
                      </td>
                      <td [class]="variancePctClass(ex)">{{ formatVariancePct(ex) }}</td>
                      <td>{{ ex.matchMethod ?? '—' }}</td>
                      <td>
                        <app-status-badge
                          [label]="ex.status | statusLabel"
                          [severity]="getSeverity(ex.status)"
                        />
                      </td>
                      <td>{{ ex.createdAt | utcDate: 'short' }}</td>
                    </tr>
                  </ng-template>
                  <!-- F04-06: show error message instead of generic empty state on API failure -->
                  <ng-template pTemplate="emptymessage">
                    <tr>
                      <td colspan="10">
                        @if (unmatchedTab().error) {
                          <app-empty-state
                            icon="pi-exclamation-circle"
                            title="Failed to load data"
                            [description]="unmatchedTab().error!"
                          />
                        } @else {
                          <app-empty-state
                            icon="pi-link"
                            title="No unmatched records"
                            description="All pre-auth records have been matched to a transaction."
                          />
                        }
                      </td>
                    </tr>
                  </ng-template>
                </p-table>
              </p-card>
            </p-tabpanel>

            <!-- ── Fuzzy Match (F04-01) ── -->
            <p-tabpanel value="fuzzy">
              <p-card styleClass="table-card">
                <p-table
                  [value]="fuzzyTab().data"
                  [lazy]="true"
                  [loading]="fuzzyTab().loading"
                  [paginator]="true"
                  [rows]="fuzzyTab().pageSize"
                  [first]="fuzzyTab().tableFirst"
                  [totalRecords]="fuzzyTab().totalRecords"
                  [rowsPerPageOptions]="[20, 50, 100]"
                  (onLazyLoad)="onFuzzyLazyLoad($event)"
                  styleClass="p-datatable-sm p-datatable-striped p-datatable-hoverable-rows"
                  [tableStyle]="{ 'min-width': '1100px' }"
                >
                  <ng-template pTemplate="header">
                    <tr>
                      <th>Site</th>
                      <th style="width:5rem">Pump</th>
                      <th style="width:5rem">Nozzle</th>
                      <th style="width:10rem">Authorised Amt</th>
                      <th style="width:10rem">Actual Amt</th>
                      <th style="width:10rem">Variance</th>
                      <th style="width:7rem">Var %</th>
                      <th>Match Method</th>
                      <th style="width:9rem">Status</th>
                      <th style="width:11rem">Created At</th>
                    </tr>
                  </ng-template>
                  <ng-template pTemplate="body" let-ex>
                    <tr class="clickable-row" tabindex="0" (click)="onRowClick(ex)" (keydown.enter)="onRowClick(ex)">
                      <td>{{ ex.siteCode }}</td>
                      <td>{{ ex.pumpNumber }}</td>
                      <td>{{ ex.nozzleNumber }}</td>
                      <td>{{ ex.requestedAmount | currencyMinorUnits: ex.currencyCode }}</td>
                      <td>{{ ex.actualAmount !== null ? (ex.actualAmount | currencyMinorUnits: ex.currencyCode) : '—' }}</td>
                      <td>
                        <span [class]="varianceClass(ex)">
                          {{ formatVariance(ex) }}
                        </span>
                      </td>
                      <td [class]="variancePctClass(ex)">{{ formatVariancePct(ex) }}</td>
                      <td>{{ ex.matchMethod ?? '—' }}</td>
                      <td>
                        <app-status-badge
                          [label]="ex.status | statusLabel"
                          [severity]="getSeverity(ex.status)"
                        />
                        @if (ex.ambiguityFlag) {
                          <i class="pi pi-exclamation-triangle ambiguity-icon" title="Ambiguous match"></i>
                        }
                      </td>
                      <td>{{ ex.createdAt | utcDate: 'short' }}</td>
                    </tr>
                  </ng-template>
                  <!-- F04-06: show error message instead of generic empty state on API failure -->
                  <ng-template pTemplate="emptymessage">
                    <tr>
                      <td colspan="10">
                        @if (fuzzyTab().error) {
                          <app-empty-state
                            icon="pi-exclamation-circle"
                            title="Failed to load data"
                            [description]="fuzzyTab().error!"
                          />
                        } @else {
                          <app-empty-state
                            icon="pi-search"
                            title="No fuzzy match records"
                            description="No records are pending fuzzy match review."
                          />
                        }
                      </td>
                    </tr>
                  </ng-template>
                </p-table>
              </p-card>
            </p-tabpanel>

            <!-- ── Reviewed ── -->
            <p-tabpanel value="reviewed">
              <div class="reviewed-status-filter">
                <p-select
                  [options]="reviewedStatusOptions"
                  [ngModel]="reviewedStatusFilter()"
                  (ngModelChange)="onReviewedStatusChange($event)"
                  optionLabel="label"
                  optionValue="value"
                  styleClass="reviewed-status-select"
                />
              </div>
              <p-card styleClass="table-card">
                <p-table
                  [value]="reviewedTab().data"
                  [lazy]="true"
                  [loading]="reviewedTab().loading"
                  [paginator]="true"
                  [rows]="reviewedTab().pageSize"
                  [first]="reviewedTab().tableFirst"
                  [totalRecords]="reviewedTab().totalRecords"
                  [rowsPerPageOptions]="[20, 50, 100]"
                  (onLazyLoad)="onReviewedLazyLoad($event)"
                  styleClass="p-datatable-sm p-datatable-striped p-datatable-hoverable-rows"
                  [tableStyle]="{ 'min-width': '1100px' }"
                >
                  <ng-template pTemplate="header">
                    <tr>
                      <th>Site</th>
                      <th style="width:5rem">Pump</th>
                      <th style="width:5rem">Nozzle</th>
                      <th style="width:10rem">Authorised Amt</th>
                      <th style="width:10rem">Actual Amt</th>
                      <th style="width:10rem">Variance</th>
                      <th style="width:7rem">Var %</th>
                      <th>Match Method</th>
                      <th style="width:9rem">Status</th>
                      <th style="width:11rem">Reviewed At</th>
                    </tr>
                  </ng-template>
                  <ng-template pTemplate="body" let-ex>
                    <tr class="clickable-row" tabindex="0" (click)="onRowClick(ex)" (keydown.enter)="onRowClick(ex)">
                      <td>{{ ex.siteCode }}</td>
                      <td>{{ ex.pumpNumber }}</td>
                      <td>{{ ex.nozzleNumber }}</td>
                      <td>{{ ex.requestedAmount | currencyMinorUnits: ex.currencyCode }}</td>
                      <td>{{ ex.actualAmount !== null ? (ex.actualAmount | currencyMinorUnits: ex.currencyCode) : '—' }}</td>
                      <td>
                        <span [class]="varianceClass(ex)">
                          {{ formatVariance(ex) }}
                        </span>
                      </td>
                      <td [class]="variancePctClass(ex)">{{ formatVariancePct(ex) }}</td>
                      <td>{{ ex.matchMethod ?? '—' }}</td>
                      <td>
                        <app-status-badge
                          [label]="ex.status | statusLabel"
                          [severity]="getSeverity(ex.status)"
                        />
                      </td>
                      <td>{{ (ex.decidedAt ?? ex.updatedAt) | utcDate: 'short' }}</td>
                    </tr>
                  </ng-template>
                  <!-- F04-06: show error message instead of generic empty state on API failure -->
                  <ng-template pTemplate="emptymessage">
                    <tr>
                      <td colspan="10">
                        @if (reviewedTab().error) {
                          <app-empty-state
                            icon="pi-exclamation-circle"
                            title="Failed to load data"
                            [description]="reviewedTab().error!"
                          />
                        } @else {
                          <app-empty-state
                            icon="pi-history"
                            title="No reviewed records"
                            description="Approved and rejected exceptions will appear here."
                          />
                        }
                      </td>
                    </tr>
                  </ng-template>
                </p-table>
              </p-card>
            </p-tabpanel>
          </p-tabpanels>
        </p-tabs>
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
      app-reconciliation-filters {
        display: block;
        margin-bottom: 1rem;
      }
      .clickable-row {
        cursor: pointer;
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
      .tab-badge {
        margin-left: 0.4rem;
        vertical-align: middle;
      }
      .ambiguity-icon {
        margin-left: 0.35rem;
        color: var(--p-orange-500, #f97316);
        font-size: 0.8rem;
        vertical-align: middle;
      }
      .reviewed-status-filter {
        margin-bottom: 0.75rem;
      }
      .reviewed-status-select {
        min-width: 180px;
      }
    `,
  ],
})
export class ReconciliationListComponent {
  private readonly reconService = inject(ReconciliationService);
  private readonly masterDataService = inject(MasterDataService);
  private readonly siteService = inject(SiteService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  // F04-03: single shared instance instead of new CurrencyMinorUnitsPipe() per cell render
  private readonly currencyPipe = new CurrencyMinorUnitsPipe();

  readonly pageSize = 20;

  // ── Legal entity ──────────────────────────────────────────────────────────
  private readonly legalEntities = signal<LegalEntity[]>([]);
  readonly selectedLegalEntityId = signal<string | null>(null);
  readonly legalEntityOptions = computed(() =>
    this.legalEntities().map((e) => ({ label: `${e.name} (${e.code})`, value: e.id })),
  );

  // ── Site filter options ───────────────────────────────────────────────────
  readonly siteOptions = signal<{ label: string; value: string }[]>([]);
  // RC-P05: cache prevents re-fetching when the user switches back to a previously loaded entity
  private readonly siteCache = new Map<string, { label: string; value: string }[]>();
  private readonly loadSites$ = new Subject<string>();

  // ── Tab state ─────────────────────────────────────────────────────────────
  readonly activeTab = signal<string>('variance');
  readonly varianceTab = signal<TabState>(emptyTabState());
  readonly unmatchedTab = signal<TabState>(emptyTabState());
  // F04-01: state for the new Fuzzy Match tab
  readonly fuzzyTab = signal<TabState>(emptyTabState());
  readonly reviewedTab = signal<TabState>(emptyTabState());

  // ── Reviewed tab status filter ──────────────────────────────────────────
  readonly reviewedStatusFilter = signal<ReconciliationStatus>(ReconciliationStatus.APPROVED);
  readonly reviewedStatusOptions = [
    { label: 'Approved', value: ReconciliationStatus.APPROVED },
    { label: 'Rejected', value: ReconciliationStatus.REJECTED },
  ];

  private currentFilters: ReconciliationFilters = {
    ...EMPTY_RECON_FILTERS,
    dateRange: { from: null, to: null },
  };

  // ── Load subjects (switchMap cancels in-flight) ───────────────────────────
  private readonly loadVariance$ = new Subject<LoadRequest>();
  private readonly loadUnmatched$ = new Subject<LoadRequest>();
  // F04-01: subject for the new Fuzzy Match tab
  private readonly loadFuzzy$ = new Subject<LoadRequest>();
  private readonly loadReviewed$ = new Subject<LoadRequest>();

  constructor() {
    this.masterDataService
      .getLegalEntities()
      .pipe(takeUntilDestroyed())
      .subscribe({ next: (entities) => this.legalEntities.set(entities) });

    // Variance Flagged subscription
    this.loadVariance$
      .pipe(
        switchMap((req) => {
          this.varianceTab.update((s) => ({ ...s, loading: true, error: null }));
          const params = this.buildParams(req, ReconciliationStatus.VARIANCE_FLAGGED);
          return this.reconService.getExceptions(params).pipe(
            // F04-06: surface error in tab state so the template can show feedback
            catchError(() => {
              this.varianceTab.update((s) => ({
                ...s,
                loading: false,
                data: [],
                error: 'Failed to load data. Please try again.',
              }));
              return EMPTY;
            }),
          );
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((result) => {
        this.varianceTab.update((s) => {
          const next = { ...s, loading: false, data: result.data, error: null };
          if (result.meta.nextCursor != null) next.cursors[s.currentPage + 1] = result.meta.nextCursor;
          // F04-07: Math.max prevents totalRecords from shrinking when navigating later pages
          const estimate = result.meta.hasMore
            ? (s.currentPage + 2) * s.pageSize
            : s.currentPage * s.pageSize + result.data.length;
          next.totalRecords = result.meta.totalCount ?? Math.max(s.totalRecords, estimate);
          return next;
        });
      });

    // Unmatched subscription
    this.loadUnmatched$
      .pipe(
        switchMap((req) => {
          this.unmatchedTab.update((s) => ({ ...s, loading: true, error: null }));
          const params = this.buildParams(req, ReconciliationStatus.UNMATCHED);
          return this.reconService.getExceptions(params).pipe(
            catchError(() => {
              this.unmatchedTab.update((s) => ({
                ...s,
                loading: false,
                data: [],
                error: 'Failed to load data. Please try again.',
              }));
              return EMPTY;
            }),
          );
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((result) => {
        this.unmatchedTab.update((s) => {
          const next = { ...s, loading: false, data: result.data, error: null };
          if (result.meta.nextCursor != null) next.cursors[s.currentPage + 1] = result.meta.nextCursor;
          const estimate = result.meta.hasMore
            ? (s.currentPage + 2) * s.pageSize
            : s.currentPage * s.pageSize + result.data.length;
          next.totalRecords = result.meta.totalCount ?? Math.max(s.totalRecords, estimate);
          return next;
        });
      });

    // Fuzzy Match subscription (F04-01)
    this.loadFuzzy$
      .pipe(
        switchMap((req) => {
          this.fuzzyTab.update((s) => ({ ...s, loading: true, error: null }));
          const params = this.buildParams(req, ReconciliationStatus.REVIEW_FUZZY_MATCH);
          return this.reconService.getExceptions(params).pipe(
            catchError(() => {
              this.fuzzyTab.update((s) => ({
                ...s,
                loading: false,
                data: [],
                error: 'Failed to load data. Please try again.',
              }));
              return EMPTY;
            }),
          );
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((result) => {
        this.fuzzyTab.update((s) => {
          const next = { ...s, loading: false, data: result.data, error: null };
          if (result.meta.nextCursor != null) next.cursors[s.currentPage + 1] = result.meta.nextCursor;
          const estimate = result.meta.hasMore
            ? (s.currentPage + 2) * s.pageSize
            : s.currentPage * s.pageSize + result.data.length;
          next.totalRecords = result.meta.totalCount ?? Math.max(s.totalRecords, estimate);
          return next;
        });
      });

    // Reviewed subscription
    this.loadReviewed$
      .pipe(
        switchMap((req) => {
          this.reviewedTab.update((s) => ({ ...s, loading: true, error: null }));
          const params = this.buildParams(req, this.reviewedStatusFilter());
          return this.reconService.getExceptions(params).pipe(
            catchError(() => {
              this.reviewedTab.update((s) => ({
                ...s,
                loading: false,
                data: [],
                error: 'Failed to load data. Please try again.',
              }));
              return EMPTY;
            }),
          );
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((result) => {
        this.reviewedTab.update((s) => {
          const next = { ...s, loading: false, data: result.data, error: null };
          if (result.meta.nextCursor != null) next.cursors[s.currentPage + 1] = result.meta.nextCursor;
          const estimate = result.meta.hasMore
            ? (s.currentPage + 2) * s.pageSize
            : s.currentPage * s.pageSize + result.data.length;
          next.totalRecords = result.meta.totalCount ?? Math.max(s.totalRecords, estimate);
          return next;
        });
      });

    // RC-P05: switchMap cancels any in-flight site request when a new entity is selected;
    // siteCache prevents redundant fetches on returning to a previously loaded entity.
    this.loadSites$
      .pipe(
        switchMap((entityId) => {
          const cached = this.siteCache.get(entityId);
          if (cached) {
            this.siteOptions.set(cached);
            return EMPTY;
          }
          return this.siteService.getSites({ legalEntityId: entityId, pageSize: 1000 }).pipe(
            map((result) => ({ entityId, result })),
            catchError(() => {
              this.siteOptions.set([]);
              return EMPTY;
            }),
          );
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(({ entityId, result }) => {
        const opts = result.data.map((s) => ({ label: `${s.siteName} (${s.siteCode})`, value: s.siteCode }));
        this.siteCache.set(entityId, opts);
        this.siteOptions.set(opts);
      });
  }

  // ── Event handlers ────────────────────────────────────────────────────────

  onLegalEntityChange(entityId: string | null): void {
    this.selectedLegalEntityId.set(entityId);
    if (!entityId) return;
    this.loadSitesForEntity(entityId);
    this.resetAllTabs();
    this.triggerTabLoad(this.activeTab());
  }

  onFiltersChange(filters: ReconciliationFilters): void {
    this.currentFilters = filters;
    this.resetAllTabs();
    this.triggerTabLoad(this.activeTab());
  }

  onTabChange(tab: string | number | undefined): void {
    if (tab === undefined) return;
    const key = String(tab);
    this.activeTab.set(key);
    const state = this.getTabState(key);
    if (state().data.length === 0 && !state().loading) {
      this.triggerTabLoad(key);
    }
  }

  onVarianceLazyLoad(event: TableLazyLoadEvent): void {
    const entityId = this.selectedLegalEntityId();
    if (!entityId) return;
    const newRows = event.rows ?? this.pageSize;
    // F04-04: cursor array was built for the old page size — reset state and reload from page 0
    if (newRows !== this.varianceTab().pageSize) {
      this.varianceTab.set(emptyTabState(newRows));
      this.loadVariance$.next({ entityId, cursor: undefined, pageSize: newRows, filters: this.currentFilters });
      return;
    }
    const page = Math.floor((event.first ?? 0) / newRows);
    this.varianceTab.update((s) => ({ ...s, currentPage: page, tableFirst: event.first ?? 0 }));
    const cursor = this.varianceTab().cursors[page] ?? undefined;
    this.loadVariance$.next({ entityId, cursor, pageSize: newRows, filters: this.currentFilters });
  }

  onUnmatchedLazyLoad(event: TableLazyLoadEvent): void {
    const entityId = this.selectedLegalEntityId();
    if (!entityId) return;
    const newRows = event.rows ?? this.pageSize;
    // F04-04: cursor array was built for the old page size — reset state and reload from page 0
    if (newRows !== this.unmatchedTab().pageSize) {
      this.unmatchedTab.set(emptyTabState(newRows));
      this.loadUnmatched$.next({ entityId, cursor: undefined, pageSize: newRows, filters: this.currentFilters });
      return;
    }
    const page = Math.floor((event.first ?? 0) / newRows);
    this.unmatchedTab.update((s) => ({ ...s, currentPage: page, tableFirst: event.first ?? 0 }));
    const cursor = this.unmatchedTab().cursors[page] ?? undefined;
    this.loadUnmatched$.next({ entityId, cursor, pageSize: newRows, filters: this.currentFilters });
  }

  // F04-01: lazy load handler for the new Fuzzy Match tab
  onFuzzyLazyLoad(event: TableLazyLoadEvent): void {
    const entityId = this.selectedLegalEntityId();
    if (!entityId) return;
    const newRows = event.rows ?? this.pageSize;
    // F04-04: cursor array was built for the old page size — reset state and reload from page 0
    if (newRows !== this.fuzzyTab().pageSize) {
      this.fuzzyTab.set(emptyTabState(newRows));
      this.loadFuzzy$.next({ entityId, cursor: undefined, pageSize: newRows, filters: this.currentFilters });
      return;
    }
    const page = Math.floor((event.first ?? 0) / newRows);
    this.fuzzyTab.update((s) => ({ ...s, currentPage: page, tableFirst: event.first ?? 0 }));
    const cursor = this.fuzzyTab().cursors[page] ?? undefined;
    this.loadFuzzy$.next({ entityId, cursor, pageSize: newRows, filters: this.currentFilters });
  }

  onReviewedStatusChange(status: ReconciliationStatus): void {
    this.reviewedStatusFilter.set(status);
    this.reviewedTab.set(emptyTabState());
    this.triggerTabLoad('reviewed');
  }

  onReviewedLazyLoad(event: TableLazyLoadEvent): void {
    const entityId = this.selectedLegalEntityId();
    if (!entityId) return;
    const newRows = event.rows ?? this.pageSize;
    // F04-04: cursor array was built for the old page size — reset state and reload from page 0
    if (newRows !== this.reviewedTab().pageSize) {
      this.reviewedTab.set(emptyTabState(newRows));
      this.loadReviewed$.next({ entityId, cursor: undefined, pageSize: newRows, filters: this.currentFilters });
      return;
    }
    const page = Math.floor((event.first ?? 0) / newRows);
    this.reviewedTab.update((s) => ({ ...s, currentPage: page, tableFirst: event.first ?? 0 }));
    const cursor = this.reviewedTab().cursors[page] ?? undefined;
    this.loadReviewed$.next({ entityId, cursor, pageSize: newRows, filters: this.currentFilters });
  }

  onRowClick(ex: ReconciliationException): void {
    this.router.navigate(['/reconciliation/exceptions', ex.id]);
  }

  getSeverity(status: ReconciliationStatus): PrimeSeverity {
    return statusSeverity(status);
  }

  formatVariance(ex: ReconciliationException): string {
    if (ex.amountVariance == null) return '—';
    const sign = ex.amountVariance > 0 ? '+' : ex.amountVariance < 0 ? '-' : '';
    // F04-03: use the shared pipe instance
    const formatted = this.currencyPipe.transform(Math.abs(ex.amountVariance), ex.currencyCode ?? '');
    return `${sign}${formatted}`;
  }

  formatVariancePct(ex: ReconciliationException): string {
    const variancePercent = resolveVariancePercent(ex.variancePercent, ex.varianceBps);
    if (variancePercent == null) return '—';
    const sign = variancePercent >= 0 ? '+' : '';
    return `${sign}${variancePercent.toFixed(2)}%`;
  }

  varianceClass(ex: ReconciliationException): string {
    if (ex.amountVariance == null) return 'variance-null';
    if (ex.amountVariance === 0) return 'variance-zero';
    return ex.amountVariance > 0 ? 'variance-positive' : 'variance-negative';
  }

  variancePctClass(ex: ReconciliationException): string {
    const variancePercent = resolveVariancePercent(ex.variancePercent, ex.varianceBps);
    if (variancePercent == null) return 'variance-null';
    if (variancePercent === 0) return 'variance-zero';
    return ex.status === ReconciliationStatus.VARIANCE_FLAGGED
      ? 'variance-positive'
      : 'variance-zero';
  }

  // ── Private helpers ───────────────────────────────────────────────────────

  private getTabState(tab: string): typeof this.varianceTab {
    if (tab === 'unmatched') return this.unmatchedTab;
    if (tab === 'fuzzy') return this.fuzzyTab;
    if (tab === 'reviewed') return this.reviewedTab;
    return this.varianceTab;
  }

  private triggerTabLoad(tab: string): void {
    const entityId = this.selectedLegalEntityId();
    if (!entityId) return;
    const req: LoadRequest = {
      entityId,
      cursor: undefined,
      pageSize: this.pageSize,
      filters: this.currentFilters,
    };
    if (tab === 'variance') this.loadVariance$.next(req);
    else if (tab === 'unmatched') this.loadUnmatched$.next(req);
    else if (tab === 'fuzzy') this.loadFuzzy$.next(req);
    else if (tab === 'reviewed') this.loadReviewed$.next(req);
  }

  private resetAllTabs(): void {
    this.varianceTab.set(emptyTabState());
    this.unmatchedTab.set(emptyTabState());
    this.fuzzyTab.set(emptyTabState());
    this.reviewedTab.set(emptyTabState());
  }

  private buildParams(
    req: LoadRequest,
    status: ReconciliationStatus,
    pageSize = req.pageSize,
  ): ReconciliationQueryParams {
    const p: ReconciliationQueryParams = {
      legalEntityId: req.entityId,
      pageSize,
      reconciliationStatus: status,
    };
    if (req.cursor) p.cursor = req.cursor;
    if (req.filters.siteCode) p.siteCode = req.filters.siteCode;
    if (req.filters.dateRange.from) p.from = req.filters.dateRange.from.toISOString();
    if (req.filters.dateRange.to) p.to = req.filters.dateRange.to.toISOString();
    return p;
  }

  private loadSitesForEntity(entityId: string): void {
    this.loadSites$.next(entityId);
  }
}
