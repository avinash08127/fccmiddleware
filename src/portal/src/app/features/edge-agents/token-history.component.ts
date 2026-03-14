import { Component, DestroyRef, effect, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subject, EMPTY, merge } from 'rxjs';
import { switchMap, catchError, debounceTime, exhaustMap } from 'rxjs/operators';
import { TableModule } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { CardModule } from 'primeng/card';
import { SelectModule } from 'primeng/select';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';

import { BootstrapTokenService } from '../../core/services/bootstrap-token.service';
import { SiteService } from '../../core/services/site.service';
import { LegalEntityStateService } from '../../core/services/legal-entity-state.service';
import {
  BootstrapTokenHistoryRow,
  BootstrapTokenEffectiveStatus,
} from '../../core/models/bootstrap-token.model';
import { Site } from '../../core/models/site.model';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { UtcDatePipe } from '../../shared/pipes/utc-date.pipe';
import { DateRangePickerComponent, DateRange } from '../../shared/components/date-range-picker/date-range-picker.component';
import { hasAnyRequiredRole } from '../../core/auth/auth-state';

const PAGE_SIZE = 20;

type TagSeverity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

function statusSeverity(status: BootstrapTokenEffectiveStatus): TagSeverity {
  switch (status) {
    case 'ACTIVE':  return 'success';
    case 'USED':    return 'info';
    case 'EXPIRED': return 'warn';
    case 'REVOKED': return 'danger';
    default:        return 'secondary';
  }
}

@Component({
  selector: 'app-token-history',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    TableModule,
    ButtonModule,
    CardModule,
    SelectModule,
    TagModule,
    TooltipModule,
    EmptyStateComponent,
    UtcDatePipe,
    DateRangePickerComponent,
  ],
  template: `
    <div class="page-container">
      <div class="page-header">
        <h1 class="page-title"><i class="pi pi-history"></i> Bootstrap Token History</h1>
        <div class="header-actions">
          <p-select
            [options]="legalEntityOptions()"
            [ngModel]="selectedLegalEntityId()"
            (ngModelChange)="onLegalEntityChange($event)"
            optionLabel="label"
            optionValue="value"
            placeholder="Select Legal Entity"
            styleClass="entity-selector"
          />
          @if (isWriter()) {
            <p-button
              label="Generate Token"
              icon="pi pi-key"
              severity="secondary"
              size="small"
              (onClick)="navigateToGenerate()"
            />
          }
          <p-button
            icon="pi pi-refresh"
            severity="secondary"
            [rounded]="true"
            [text]="true"
            pTooltip="Refresh"
            (onClick)="manualRefresh()"
          />
        </div>
      </div>

      @if (!selectedLegalEntityId()) {
        <app-empty-state
          icon="pi-building"
          title="Select a Legal Entity"
          description="Choose a legal entity above to view token history."
        />
      } @else {

        <!-- Filters -->
        <p-card styleClass="filters-card">
          <div class="filters-row">
            <div class="filter-field">
              <label for="th-site-filter">Site</label>
              <p-select
                inputId="th-site-filter"
                [options]="siteOptions()"
                [(ngModel)]="filterSiteCode"
                optionLabel="label"
                optionValue="value"
                placeholder="All Sites"
                [showClear]="true"
                [filter]="true"
                filterPlaceholder="Search sites..."
                [loading]="loadingSites()"
                (ngModelChange)="onFilterChange()"
              />
            </div>
            <div class="filter-field">
              <label for="th-status-filter">Status</label>
              <p-select
                inputId="th-status-filter"
                [options]="statusOptions"
                [(ngModel)]="filterStatus"
                placeholder="All Statuses"
                [showClear]="true"
                (ngModelChange)="onFilterChange()"
              />
            </div>
            <div class="filter-field">
              <label>Date Range</label>
              <app-date-range-picker
                [(ngModel)]="dateRange"
                placeholder="Filter by creation date"
                (rangeSelected)="onFilterChange()"
              />
            </div>
            <div class="filter-field filter-field--action">
              <p-button
                label="Clear"
                severity="secondary"
                icon="pi pi-times"
                size="small"
                (onClick)="clearFilters()"
              />
            </div>
          </div>
        </p-card>

        @if (loading() && tokens().length === 0) {
          <div class="loading-msg"><i class="pi pi-spin pi-spinner"></i> Loading token history...</div>
        }

        @if (error()) {
          <div class="error-msg">
            <i class="pi pi-exclamation-triangle"></i>
            Failed to load token history.
            <button type="button" class="link-btn" (click)="manualRefresh()">Retry</button>
          </div>
        }

        <p-card styleClass="table-card">
          <ng-template pTemplate="header">
            <div class="card-header-row">
              <span>
                Token History
                @if (totalCount() !== null) {
                  <span class="count-hint">— {{ tokens().length }} of {{ totalCount() }} loaded</span>
                }
              </span>
            </div>
          </ng-template>

          <p-table
            [value]="tokens()"
            sortMode="single"
            sortField="createdAt"
            [sortOrder]="-1"
            [paginator]="tokens().length > 20"
            [rows]="20"
            [rowsPerPageOptions]="[20, 50]"
            styleClass="p-datatable-sm p-datatable-striped"
            [tableStyle]="{ 'min-width': '1100px' }"
          >
            <ng-template pTemplate="header">
              <tr>
                <th pSortableColumn="tokenId" style="width:8rem">Token ID <p-sortIcon field="tokenId" /></th>
                <th pSortableColumn="siteCode">Site <p-sortIcon field="siteCode" /></th>
                <th pSortableColumn="effectiveStatus" style="width:7rem">Status <p-sortIcon field="effectiveStatus" /></th>
                <th pSortableColumn="createdAt">Created <p-sortIcon field="createdAt" /></th>
                <th>Revoked</th>
                <th>Used</th>
                <th pSortableColumn="expiresAt">Expiry <p-sortIcon field="expiresAt" /></th>
              </tr>
            </ng-template>

            <ng-template pTemplate="body" let-token>
              <tr>
                <td>
                  <code class="token-id" [pTooltip]="token.tokenId">{{ token.tokenId | slice:0:8 }}...</code>
                </td>
                <td><strong>{{ token.siteCode }}</strong></td>
                <td>
                  <p-tag
                    [value]="token.effectiveStatus"
                    [severity]="statusSeverity(token.effectiveStatus)"
                  />
                </td>
                <td>
                  <div class="actor-cell">
                    <span>{{ token.createdAt | utcDate:'short' }}</span>
                    @if (token.createdByActorDisplay) {
                      <span class="actor-name">{{ token.createdByActorDisplay }}</span>
                    }
                  </div>
                </td>
                <td>
                  @if (token.revokedAt) {
                    <div class="actor-cell">
                      <span>{{ token.revokedAt | utcDate:'short' }}</span>
                      @if (token.revokedByActorDisplay) {
                        <span class="actor-name">{{ token.revokedByActorDisplay }}</span>
                      }
                    </div>
                  } @else { — }
                </td>
                <td>
                  @if (token.usedAt) {
                    <div class="actor-cell">
                      <span>{{ token.usedAt | utcDate:'short' }}</span>
                      @if (token.usedByDeviceId) {
                        <code class="device-id" [pTooltip]="token.usedByDeviceId">{{ token.usedByDeviceId | slice:0:8 }}...</code>
                      }
                    </div>
                  } @else { — }
                </td>
                <td>{{ token.expiresAt | utcDate:'short' }}</td>
              </tr>
            </ng-template>

            <ng-template pTemplate="emptymessage">
              <tr>
                <td colspan="7">
                  <app-empty-state
                    icon="pi-key"
                    title="No tokens found"
                    description="No bootstrap tokens match the current filters."
                  />
                </td>
              </tr>
            </ng-template>
          </p-table>

          @if (hasMore()) {
            <div class="load-more-row">
              <p-button
                [label]="loadingMore() ? 'Loading...' : 'Load More'"
                icon="pi pi-chevron-down"
                severity="secondary"
                [loading]="loadingMore()"
                [disabled]="loadingMore()"
                (onClick)="loadMore()"
              />
            </div>
          }
        </p-card>
      }
    </div>
  `,
  styles: [`
    :host { display: block; padding: 1.5rem; }

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
    .header-actions {
      display: flex;
      align-items: center;
      gap: 0.5rem;
    }
    .entity-selector { min-width: 240px; }

    .filters-card { margin-bottom: 1rem; }
    .filters-row {
      display: flex;
      flex-wrap: wrap;
      gap: 0.75rem 1rem;
      align-items: flex-end;
    }
    .filter-field {
      display: flex;
      flex-direction: column;
      gap: 0.25rem;
      min-width: 180px;
    }
    .filter-field label {
      font-size: 0.78rem;
      font-weight: 600;
      color: var(--p-text-muted-color, #64748b);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    .filter-field--action { align-self: flex-end; }

    .card-header-row {
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 0.75rem 1rem;
      font-weight: 600;
    }
    .count-hint {
      font-size: 0.8rem;
      font-weight: 400;
      color: var(--p-text-muted-color, #64748b);
    }

    .actor-cell {
      display: flex;
      flex-direction: column;
      gap: 0.1rem;
    }
    .actor-name {
      font-size: 0.75rem;
      color: var(--p-text-muted-color, #64748b);
    }

    .token-id, .device-id {
      font-family: monospace;
      font-size: 0.78rem;
      cursor: help;
    }

    .load-more-row {
      display: flex;
      align-items: center;
      gap: 1rem;
      padding: 1rem;
      border-top: 1px solid var(--p-surface-border, #e2e8f0);
    }

    .loading-msg {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      padding: 2rem;
      color: var(--p-text-muted-color, #64748b);
      justify-content: center;
    }
    .error-msg {
      padding: 1rem;
      background: #fef2f2;
      border: 1px solid #fca5a5;
      border-radius: 0.375rem;
      color: #dc2626;
      margin-bottom: 1rem;
      display: flex;
      align-items: center;
      gap: 0.5rem;
    }
    .error-msg .link-btn {
      cursor: pointer;
      text-decoration: underline;
      color: inherit;
      background: none;
      border: none;
      padding: 0;
      font: inherit;
    }

    code { font-family: monospace; font-size: 0.78rem; }
  `],
})
export class TokenHistoryComponent {
  private readonly tokenService = inject(BootstrapTokenService);
  private readonly siteService = inject(SiteService);
  private readonly leState = inject(LegalEntityStateService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly isWriter = computed(() => hasAnyRequiredRole(null, ['FccAdmin', 'FccUser']));

  // ── Legal entity (shared) ──────────────────────────────────────────────
  readonly selectedLegalEntityId = this.leState.selectedId;
  readonly legalEntityOptions = this.leState.options;

  // ── Sites for filter ───────────────────────────────────────────────────
  private readonly sites = signal<Site[]>([]);
  readonly loadingSites = signal(false);
  readonly siteOptions = computed(() =>
    this.sites().map((s) => ({ label: `${s.siteCode} — ${s.siteName}`, value: s.siteCode })),
  );

  // ── Token data ─────────────────────────────────────────────────────────
  readonly tokens = signal<BootstrapTokenHistoryRow[]>([]);
  readonly loading = signal(false);
  readonly loadingMore = signal(false);
  readonly error = signal(false);
  readonly hasMore = signal(false);
  readonly totalCount = signal<number | null>(null);
  private readonly nextCursor = signal<string | null>(null);

  // ── Filters ────────────────────────────────────────────────────────────
  filterSiteCode: string | null = null;
  filterStatus: BootstrapTokenEffectiveStatus | null = null;
  dateRange: DateRange = { from: null, to: null };

  readonly statusOptions = [
    { label: 'Active', value: 'ACTIVE' },
    { label: 'Used', value: 'USED' },
    { label: 'Expired', value: 'EXPIRED' },
    { label: 'Revoked', value: 'REVOKED' },
  ];

  // ── Refresh triggers ──────────────────────────────────────────────────
  private readonly immediateRefresh$ = new Subject<void>();
  private readonly loadMore$ = new Subject<void>();

  constructor() {
    // Load sites when legal entity changes
    effect(() => {
      const entityId = this.leState.selectedId();
      if (entityId) {
        this.loadSites(entityId);
        this.immediateRefresh$.next();
      }
    });

    // First-page load
    this.immediateRefresh$.pipe(
      switchMap(() => {
        const entityId = this.selectedLegalEntityId();
        if (!entityId) return EMPTY;
        this.loading.set(true);
        this.error.set(false);
        this.tokens.set([]);
        this.nextCursor.set(null);
        this.hasMore.set(false);
        this.totalCount.set(null);
        return this.tokenService.getHistory(this.buildParams(entityId)).pipe(
          catchError(() => {
            this.error.set(true);
            this.loading.set(false);
            return EMPTY;
          }),
        );
      }),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe((result) => {
      this.tokens.set(result.data);
      this.hasMore.set(result.meta.hasMore);
      this.nextCursor.set(result.meta.nextCursor);
      this.totalCount.set(result.meta.totalCount);
      this.loading.set(false);
    });

    // Load more pages
    this.loadMore$.pipe(
      exhaustMap(() => {
        const entityId = this.selectedLegalEntityId();
        const cursor = this.nextCursor();
        if (!entityId || !cursor) return EMPTY;
        this.loadingMore.set(true);
        return this.tokenService.getHistory({ ...this.buildParams(entityId), cursor }).pipe(
          catchError(() => {
            this.loadingMore.set(false);
            return EMPTY;
          }),
        );
      }),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe((result) => {
      this.tokens.update((current) => [...current, ...result.data]);
      this.hasMore.set(result.meta.hasMore);
      this.nextCursor.set(result.meta.nextCursor);
      this.loadingMore.set(false);
    });
  }

  onLegalEntityChange(entityId: string | null): void {
    this.leState.select(entityId);
    if (!entityId) {
      this.tokens.set([]);
      this.hasMore.set(false);
      this.totalCount.set(null);
      this.sites.set([]);
    }
  }

  onFilterChange(): void {
    this.immediateRefresh$.next();
  }

  clearFilters(): void {
    this.filterSiteCode = null;
    this.filterStatus = null;
    this.dateRange = { from: null, to: null };
    this.immediateRefresh$.next();
  }

  manualRefresh(): void {
    this.immediateRefresh$.next();
  }

  loadMore(): void {
    this.loadMore$.next();
  }

  navigateToGenerate(): void {
    this.router.navigate(['/agents', 'bootstrap-token']);
  }

  statusSeverity(status: BootstrapTokenEffectiveStatus): TagSeverity {
    return statusSeverity(status);
  }

  private buildParams(entityId: string) {
    return {
      legalEntityId: entityId,
      pageSize: PAGE_SIZE,
      siteCode: this.filterSiteCode ?? undefined,
      status: this.filterStatus ?? undefined,
      from: this.dateRange.from ? this.dateRange.from.toISOString() : undefined,
      to: this.dateRange.to ? this.dateRange.to.toISOString() : undefined,
    };
  }

  private loadSites(entityId: string): void {
    this.loadingSites.set(true);
    this.siteService.getSites({ legalEntityId: entityId, pageSize: 500, isActive: true })
      .pipe(
        catchError(() => {
          this.loadingSites.set(false);
          return EMPTY;
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((result) => {
        this.sites.set(result.data);
        this.loadingSites.set(false);
      });
  }
}
