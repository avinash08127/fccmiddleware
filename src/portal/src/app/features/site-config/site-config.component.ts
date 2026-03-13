import { Component, DestroyRef, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subject, EMPTY } from 'rxjs';
import { switchMap, catchError } from 'rxjs/operators';
import { TableModule, TableLazyLoadEvent } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { CardModule } from 'primeng/card';
import { SelectModule } from 'primeng/select';
import { ToggleSwitchModule } from 'primeng/toggleswitch';
import { PanelModule } from 'primeng/panel';
import { ToastModule } from 'primeng/toast';
import { FormsModule } from '@angular/forms';
import { MessageService } from 'primeng/api';

import { SiteService, SiteQueryParams } from '../../core/services/site.service';
import { MasterDataService } from '../../core/services/master-data.service';
import { LoggingService } from '../../core/services/logging.service';
import {
  Site,
  SiteOperatingModel,
  ConnectivityMode,
} from '../../core/models/site.model';
import { LegalEntity } from '../../core/models/master-data.model';
import { StatusBadgeComponent } from '../../shared/components/status-badge/status-badge.component';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { StatusLabelPipe } from '../../shared/pipes/status-label.pipe';

interface LoadRequest {
  entityId: string;
  cursor: string | undefined;
  pageSize: number;
  operatingModel: SiteOperatingModel | null;
  connectivityMode: ConnectivityMode | null;
  isActive: boolean | null;
}

type PrimeSeverity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

@Component({
  selector: 'app-site-config',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    TableModule,
    ButtonModule,
    CardModule,
    SelectModule,
    ToggleSwitchModule,
    PanelModule,
    ToastModule,
    StatusBadgeComponent,
    EmptyStateComponent,
    StatusLabelPipe,
  ],
  providers: [MessageService],
  template: `
    <p-toast />
    <div class="page-container">
      <!-- Header -->
      <div class="page-header">
        <h1 class="page-title"><i class="pi pi-cog"></i> Site & FCC Configuration</h1>
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
          description="Choose a legal entity above to browse site configurations."
        />
      } @else {
        <!-- Filters -->
        <p-panel header="Filters" [toggleable]="true" styleClass="filters-panel">
          <div class="filters-grid">
            <div class="filter-field">
              <label for="site-filter-operating-model">Operating Model</label>
              <p-select
                inputId="site-filter-operating-model"
                [options]="operatingModelOptions"
                [(ngModel)]="filterOperatingModel"
                placeholder="All models"
                [showClear]="true"
                (ngModelChange)="onFilterChange()"
              />
            </div>

            <div class="filter-field">
              <label for="site-filter-connectivity-mode">Connectivity Mode</label>
              <p-select
                inputId="site-filter-connectivity-mode"
                [options]="connectivityModeOptions"
                [(ngModel)]="filterConnectivityMode"
                placeholder="All modes"
                [showClear]="true"
                (ngModelChange)="onFilterChange()"
              />
            </div>

            <div class="filter-field filter-field--toggle">
              <p-toggleswitch inputId="site-filter-active-only" [(ngModel)]="filterActiveOnly" (ngModelChange)="onFilterChange()" />
              <label for="site-filter-active-only">Active sites only</label>
            </div>
          </div>
        </p-panel>

        <!-- Table -->
        <p-card styleClass="table-card">
          <p-table
            [value]="sites()"
            [lazy]="true"
            [loading]="loading()"
            [paginator]="true"
            [rows]="pageSize"
            [first]="tableFirst()"
            [totalRecords]="totalRecords()"
            [rowsPerPageOptions]="[20, 50, 100]"
            (onLazyLoad)="onLazyLoad($event)"
            styleClass="p-datatable-sm p-datatable-striped p-datatable-hoverable-rows"
            [tableStyle]="{ 'min-width': '900px' }"
          >
            <ng-template pTemplate="header">
              <tr>
                <th style="width:8rem">Site Code</th>
                <th>Site Name</th>
                <th>Legal Entity</th>
                <th style="width:7rem">Model</th>
                <th style="width:9rem">Connectivity</th>
                <th style="width:9rem">Ingestion</th>
                <th style="width:7rem">FCC Vendor</th>
                <th style="width:5rem">Status</th>
              </tr>
            </ng-template>

            <ng-template pTemplate="body" let-site>
              <tr class="clickable-row" tabindex="0" (click)="onRowClick(site)" (keydown.enter)="onRowClick(site)">
                <td><code class="site-code">{{ site.siteCode }}</code></td>
                <td>{{ site.siteName }}</td>
                <td>{{ legalEntityName(site.legalEntityId) }}</td>
                <td>
                  <app-status-badge
                    [label]="site.operatingModel"
                    [severity]="modelSeverity(site.operatingModel)"
                  />
                </td>
                <td>
                  @if (site.connectivityMode) {
                    <app-status-badge
                      [label]="site.connectivityMode | statusLabel"
                      [severity]="site.connectivityMode === 'CONNECTED' ? 'success' : 'warn'"
                    />
                  } @else {
                    <span class="null-value">—</span>
                  }
                </td>
                <td>
                  @if (site.ingestionMode) {
                    <span class="label-chip">{{ site.ingestionMode | statusLabel }}</span>
                  } @else {
                    <span class="null-value">—</span>
                  }
                </td>
                <td>
                  @if (site.fccVendor) {
                    <span class="label-chip">{{ site.fccVendor }}</span>
                  } @else {
                    <span class="null-value">—</span>
                  }
                </td>
                <td>
                  <app-status-badge
                    [label]="site.isActive ? 'Active' : 'Inactive'"
                    [severity]="site.isActive ? 'success' : 'secondary'"
                  />
                </td>
              </tr>
            </ng-template>

            <ng-template pTemplate="emptymessage">
              <tr>
                <td colspan="8">
                  <app-empty-state
                    icon="pi-filter-slash"
                    title="No sites found"
                    description="No sites match the current filters."
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
      .filters-panel {
        margin-bottom: 1rem;
      }
      .filters-grid {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
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
      .filter-field--toggle {
        flex-direction: row;
        align-items: center;
        gap: 0.5rem;
      }
      .filter-field--toggle label {
        text-transform: none;
        letter-spacing: 0;
        font-size: 0.875rem;
        font-weight: 500;
        color: var(--p-text-color);
      }
      .table-card {
        margin-top: 0;
      }
      .clickable-row {
        cursor: pointer;
      }
      .site-code {
        font-family: monospace;
        font-size: 0.78rem;
        background: var(--p-surface-100, #f1f5f9);
        padding: 0.1rem 0.35rem;
        border-radius: 4px;
      }
      .label-chip {
        font-size: 0.78rem;
        font-weight: 600;
        color: var(--p-text-muted-color, #475569);
        background: var(--p-surface-100, #f1f5f9);
        padding: 0.15rem 0.45rem;
        border-radius: 4px;
      }
      .null-value {
        color: var(--p-text-muted-color, #94a3b8);
      }
    `,
  ],
})
export class SiteConfigComponent {
  private readonly siteService = inject(SiteService);
  private readonly masterDataService = inject(MasterDataService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly messageService = inject(MessageService);
  private readonly logger = inject(LoggingService);

  readonly pageSize = 20;

  // ── Legal entity ──────────────────────────────────────────────────────────
  private readonly legalEntities = signal<LegalEntity[]>([]);
  readonly selectedLegalEntityId = signal<string | null>(null);
  readonly legalEntityOptions = computed(() =>
    this.legalEntities().map((e) => ({ label: `${e.name} (${e.code})`, value: e.id })),
  );

  // ── Table state ───────────────────────────────────────────────────────────
  readonly sites = signal<Site[]>([]);
  readonly totalRecords = signal(0);
  readonly loading = signal(false);
  readonly tableFirst = signal(0);

  // ── Cursor stack ──────────────────────────────────────────────────────────
  private cursors: (string | null)[] = [null];
  private currentPage = 0;

  // ── Filters ───────────────────────────────────────────────────────────────
  filterOperatingModel: SiteOperatingModel | null = null;
  filterConnectivityMode: ConnectivityMode | null = null;
  filterActiveOnly = true;

  // ── Load trigger ──────────────────────────────────────────────────────────
  private readonly load$ = new Subject<LoadRequest>();

  readonly operatingModelOptions = [
    { label: 'COCO', value: SiteOperatingModel.COCO },
    { label: 'CODO', value: SiteOperatingModel.CODO },
    { label: 'DODO', value: SiteOperatingModel.DODO },
    { label: 'DOCO', value: SiteOperatingModel.DOCO },
  ];

  readonly connectivityModeOptions = [
    { label: 'Connected', value: ConnectivityMode.CONNECTED },
    { label: 'Disconnected', value: ConnectivityMode.DISCONNECTED },
  ];

  constructor() {
    // F09-06: Error handler added — a failed legal-entity load leaves the page unusable,
    // so we must surface the failure rather than silently leaving the dropdown empty.
    this.masterDataService
      .getLegalEntities()
      .pipe(takeUntilDestroyed())
      .subscribe({
        next: (entities) => this.legalEntities.set(entities),
        error: () => {
          this.logger.error('SiteConfigComponent', 'Failed to load legal entities');
          this.messageService.add({
            severity: 'error',
            summary: 'Load failed',
            detail: 'Could not load legal entities. Please refresh the page.',
            life: 0,
          });
        },
      });

    this.load$
      .pipe(
        switchMap((req) => {
          this.loading.set(true);
          const params: SiteQueryParams = {
            legalEntityId: req.entityId,
            pageSize: req.pageSize,
          };
          if (req.cursor) params.cursor = req.cursor;
          if (req.operatingModel) params.operatingModel = req.operatingModel;
          if (req.connectivityMode) params.connectivityMode = req.connectivityMode;
          if (req.isActive != null) params.isActive = req.isActive;
          return this.siteService.getSites(params).pipe(
            // F09-05: Surface load failures to the user instead of silently showing an empty table.
            catchError(() => {
              this.sites.set([]);
              this.totalRecords.set(0);
              this.loading.set(false);
              this.logger.error('SiteConfigComponent', 'Failed to load sites');
              this.messageService.add({
                severity: 'error',
                summary: 'Load failed',
                detail: 'Could not load sites. Please try again.',
                life: 5000,
              });
              return EMPTY;
            }),
          );
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((result) => {
        this.sites.set(result.data);
        if (result.meta.nextCursor != null) {
          this.cursors[this.currentPage + 1] = result.meta.nextCursor;
        }
        this.totalRecords.set(
          result.meta.totalCount ??
            (result.meta.hasMore
              ? (this.currentPage + 2) * this.pageSize
              : this.currentPage * this.pageSize + result.data.length),
        );
        this.loading.set(false);
      });
  }

  onLegalEntityChange(entityId: string | null): void {
    this.selectedLegalEntityId.set(entityId);
    if (!entityId) return;
    this.resetAndLoad();
  }

  onFilterChange(): void {
    this.resetAndLoad();
  }

  onLazyLoad(event: TableLazyLoadEvent): void {
    const entityId = this.selectedLegalEntityId();
    if (!entityId) return;
    const page = Math.floor((event.first ?? 0) / (event.rows ?? this.pageSize));
    const rows = event.rows ?? this.pageSize;
    this.currentPage = page;
    const cursor = page < this.cursors.length ? (this.cursors[page] ?? undefined) : undefined;
    this.triggerLoad(cursor, rows);
  }

  onRowClick(site: Site): void {
    this.router.navigate(['/sites', site.id]);
  }

  legalEntityName(id: string): string {
    const entity = this.legalEntities().find((e) => e.id === id);
    return entity ? `${entity.name} (${entity.code})` : id;
  }

  modelSeverity(model: SiteOperatingModel): PrimeSeverity {
    switch (model) {
      case SiteOperatingModel.COCO:
        return 'info';
      case SiteOperatingModel.CODO:
        return 'success';
      case SiteOperatingModel.DODO:
        return 'warn';
      case SiteOperatingModel.DOCO:
        return 'secondary';
      default:
        return 'secondary';
    }
  }

  private resetAndLoad(): void {
    this.cursors = [null];
    this.currentPage = 0;
    this.tableFirst.set(0);
    this.sites.set([]);
    this.totalRecords.set(0);
    this.triggerLoad(undefined);
  }

  private triggerLoad(cursor: string | undefined, rows = this.pageSize): void {
    const entityId = this.selectedLegalEntityId();
    if (!entityId) return;
    this.load$.next({
      entityId,
      cursor,
      pageSize: rows,
      operatingModel: this.filterOperatingModel,
      connectivityMode: this.filterConnectivityMode,
      isActive: this.filterActiveOnly ? true : null,
    });
  }
}
