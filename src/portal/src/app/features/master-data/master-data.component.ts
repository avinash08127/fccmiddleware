import { Component, inject, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { CardModule } from 'primeng/card';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { ButtonModule } from 'primeng/button';
import { TooltipModule } from 'primeng/tooltip';
import { MasterDataService } from '../../core/services/master-data.service';
import { MasterDataSyncStatus, MasterDataEntityType } from '../../core/models/master-data.model';
import { UtcDatePipe } from '../../shared/pipes/utc-date.pipe';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';

type TagSeverity = 'success' | 'warn' | 'danger' | 'secondary';

function entityLabel(type: MasterDataEntityType): string {
  switch (type) {
    case 'legal_entities': return 'Legal Entities';
    case 'sites':          return 'Sites';
    case 'pumps':          return 'Pumps';
    case 'products':       return 'Products';
    case 'operators':      return 'Operators';
    default:               return type;
  }
}

@Component({
  selector: 'app-master-data',
  standalone: true,
  imports: [
    CommonModule,
    CardModule,
    TableModule,
    TagModule,
    ButtonModule,
    TooltipModule,
    UtcDatePipe,
    EmptyStateComponent,
  ],
  template: `
    <div class="page-container">
      <div class="page-header">
        <h1 class="page-title"><i class="pi pi-database"></i> Master Data Status</h1>
        <p-button
          icon="pi pi-refresh"
          severity="secondary"
          [text]="true"
          pTooltip="Refresh"
          [loading]="loading()"
          (onClick)="load()"
        />
      </div>

      @if (error()) {
        <div class="error-bar">
          <i class="pi pi-times-circle"></i>
          Failed to load sync status. Please try again.
        </div>
      }

      @if (!loading() && !error() && statuses().length === 0) {
        <app-empty-state
          icon="pi-database"
          title="No sync data"
          description="No master data sync records found for your scope."
        />
      } @else {
        <p-card styleClass="table-card">
          <p-table
            [value]="statuses()"
            [loading]="loading()"
            styleClass="p-datatable-sm p-datatable-striped"
          >
            <ng-template pTemplate="header">
              <tr>
                <th>Entity Type</th>
                <th style="width: 11rem">Last Sync (UTC)</th>
                <th style="width: 8rem; text-align: right">Active</th>
                <th style="width: 8rem; text-align: right">Deactivated</th>
                <th style="width: 8rem; text-align: right">Errors</th>
                <th style="width: 9rem">Status</th>
                <th style="width: 10rem">Stale Threshold</th>
              </tr>
            </ng-template>

            <ng-template pTemplate="body" let-row>
              <tr>
                <td class="entity-name">{{ formatEntityType(row.entityType) }}</td>
                <td>
                  @if (row.lastSyncAtUtc) {
                    {{ row.lastSyncAtUtc | utcDate: 'short' }}
                  } @else {
                    <span class="never">Never</span>
                  }
                </td>
                <td style="text-align: right">{{ row.totalActiveCount | number }}</td>
                <td style="text-align: right">
                  <span [class.deactivated-count]="row.deactivatedCount > 0">
                    {{ row.deactivatedCount | number }}
                  </span>
                </td>
                <td style="text-align: right">
                  <span [class.error-count]="row.errorCount > 0">
                    {{ row.errorCount | number }}
                  </span>
                </td>
                <td>
                  <p-tag
                    [value]="row.isStale ? 'Stale' : 'OK'"
                    [severity]="staleSeverity(row.isStale)"
                  />
                </td>
                <td class="threshold-cell">
                  <i class="pi pi-clock threshold-icon"></i>
                  {{ row.staleThresholdHours }}h
                </td>
              </tr>
            </ng-template>

            <ng-template pTemplate="emptymessage">
              <tr>
                <td colspan="7">
                  <app-empty-state
                    icon="pi-database"
                    title="No data"
                    description="No sync status records available."
                  />
                </td>
              </tr>
            </ng-template>
          </p-table>
        </p-card>
      }
    </div>
  `,
  styles: [`
    :host {
      display: block;
      padding: 1.5rem;
    }
    .page-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      margin-bottom: 1.25rem;
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
    .error-bar {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      padding: 0.65rem 1rem;
      border-radius: 6px;
      margin-bottom: 0.75rem;
      font-size: 0.875rem;
      background: var(--p-red-50, #fef2f2);
      border: 1px solid var(--p-red-300, #fca5a5);
      color: var(--p-red-800, #991b1b);
    }
    .table-card {
      margin-top: 0;
    }
    .entity-name {
      font-weight: 600;
      color: var(--p-text-color, #1e293b);
    }
    .never {
      color: var(--p-text-muted-color, #94a3b8);
      font-style: italic;
    }
    .deactivated-count {
      color: var(--p-orange-600, #ea580c);
      font-weight: 600;
    }
    .error-count {
      color: var(--p-red-600, #dc2626);
      font-weight: 600;
    }
    .threshold-cell {
      color: var(--p-text-muted-color, #64748b);
      font-size: 0.875rem;
    }
    .threshold-icon {
      margin-right: 0.25rem;
      font-size: 0.8rem;
    }
  `],
})
export class MasterDataComponent implements OnInit {
  private readonly masterDataService = inject(MasterDataService);

  readonly statuses = signal<MasterDataSyncStatus[]>([]);
  readonly loading = signal(false);
  readonly error = signal(false);

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(false);
    this.masterDataService.getSyncStatus().subscribe({
      next: (data) => {
        this.statuses.set(data);
        this.loading.set(false);
      },
      error: () => {
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }

  formatEntityType(type: MasterDataEntityType): string {
    return entityLabel(type);
  }

  staleSeverity(isStale: boolean): TagSeverity {
    return isStale ? 'warn' : 'success';
  }
}
