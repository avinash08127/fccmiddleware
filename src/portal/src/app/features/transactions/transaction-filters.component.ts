import { Component, Input, Output, EventEmitter } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { PanelModule } from 'primeng/panel';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { InputNumberModule } from 'primeng/inputnumber';
import { ToggleSwitchModule } from 'primeng/toggleswitch';
import { ButtonModule } from 'primeng/button';

import {
  DateRangePickerComponent,
  DateRange,
} from '../../shared/components/date-range-picker/date-range-picker.component';
import { TransactionStatus, FccVendor, IngestionSource } from '../../core/models/transaction.model';

export interface TransactionFilters {
  fccTransactionId: string;
  odooOrderId: string;
  siteCode: string;
  status: TransactionStatus | null;
  fccVendor: FccVendor | null;
  ingestionSource: IngestionSource | null;
  pumpNumber: number | null;
  dateRange: DateRange;
  isStale: boolean;
}

export const EMPTY_FILTERS: TransactionFilters = {
  fccTransactionId: '',
  odooOrderId: '',
  siteCode: '',
  status: null,
  fccVendor: null,
  ingestionSource: null,
  pumpNumber: null,
  dateRange: { from: null, to: null },
  isStale: false,
};

@Component({
  selector: 'app-transaction-filters',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    PanelModule,
    InputTextModule,
    SelectModule,
    InputNumberModule,
    ToggleSwitchModule,
    ButtonModule,
    DateRangePickerComponent,
  ],
  template: `
    <p-panel header="Filters" [toggleable]="true" styleClass="filters-panel">
      <div class="filters-grid">
        <div class="filter-field">
          <label>Transaction ID</label>
          <input
            pInputText
            [(ngModel)]="filters.fccTransactionId"
            placeholder="FCC Transaction ID"
            (ngModelChange)="emit()"
          />
        </div>

        <div class="filter-field">
          <label>Odoo Order ID</label>
          <input
            pInputText
            [(ngModel)]="filters.odooOrderId"
            placeholder="Odoo Order ID"
            (ngModelChange)="emit()"
          />
        </div>

        <div class="filter-field">
          <label>Site</label>
          <p-select
            [options]="siteOptions"
            [(ngModel)]="filters.siteCode"
            placeholder="All Sites"
            [showClear]="true"
            (ngModelChange)="emit()"
          />
        </div>

        <div class="filter-field">
          <label>Status</label>
          <p-select
            [options]="statusOptions"
            [(ngModel)]="filters.status"
            placeholder="All Statuses"
            [showClear]="true"
            (ngModelChange)="emit()"
          />
        </div>

        <div class="filter-field">
          <label>FCC Vendor</label>
          <p-select
            [options]="vendorOptions"
            [(ngModel)]="filters.fccVendor"
            placeholder="All Vendors"
            [showClear]="true"
            (ngModelChange)="emit()"
          />
        </div>

        <div class="filter-field">
          <label>Ingestion Source</label>
          <p-select
            [options]="ingestionSourceOptions"
            [(ngModel)]="filters.ingestionSource"
            placeholder="All Sources"
            [showClear]="true"
            (ngModelChange)="emit()"
          />
        </div>

        <div class="filter-field">
          <label>Pump Number</label>
          <p-inputnumber
            [(ngModel)]="filters.pumpNumber"
            placeholder="Any pump"
            [showButtons]="false"
            [useGrouping]="false"
            (ngModelChange)="emit()"
          />
        </div>

        <div class="filter-field filter-field--wide">
          <label>Started At (range)</label>
          <app-date-range-picker
            placeholder="Select date range"
            [(ngModel)]="filters.dateRange"
            (rangeSelected)="onDateRangeSelected($event)"
          />
        </div>

        <div class="filter-field filter-field--toggle">
          <p-toggleswitch [(ngModel)]="filters.isStale" (ngModelChange)="emit()" />
          <label>Stale only</label>
        </div>

        <div class="filter-field filter-field--action">
          <p-button
            label="Clear"
            severity="secondary"
            icon="pi pi-times"
            size="small"
            (onClick)="clear()"
          />
        </div>
      </div>
    </p-panel>
  `,
  styles: [
    `
      .filters-grid {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(190px, 1fr));
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
      .filter-field--action {
        align-self: end;
      }
    `,
  ],
})
export class TransactionFiltersComponent {
  @Input() siteOptions: { label: string; value: string }[] = [];
  @Output() filtersChange = new EventEmitter<TransactionFilters>();

  filters: TransactionFilters = { ...EMPTY_FILTERS, dateRange: { from: null, to: null } };

  readonly statusOptions = [
    { label: 'Pending', value: TransactionStatus.PENDING },
    { label: 'Synced', value: TransactionStatus.SYNCED },
    { label: 'Synced to Odoo', value: TransactionStatus.SYNCED_TO_ODOO },
    { label: 'Stale Pending', value: TransactionStatus.STALE_PENDING },
    { label: 'Duplicate', value: TransactionStatus.DUPLICATE },
    { label: 'Archived', value: TransactionStatus.ARCHIVED },
  ];

  readonly vendorOptions = [
    { label: 'DOMS', value: FccVendor.DOMS },
    { label: 'RADIX', value: FccVendor.RADIX },
    { label: 'ADVATEC', value: FccVendor.ADVATEC },
    { label: 'PETRONITE', value: FccVendor.PETRONITE },
  ];

  readonly ingestionSourceOptions = [
    { label: 'FCC Push', value: IngestionSource.FCC_PUSH },
    { label: 'Edge Upload', value: IngestionSource.EDGE_UPLOAD },
    { label: 'Cloud Pull', value: IngestionSource.CLOUD_PULL },
  ];

  emit(): void {
    this.filtersChange.emit({ ...this.filters });
  }

  onDateRangeSelected(range: DateRange): void {
    this.filters.dateRange = range;
    this.emit();
  }

  clear(): void {
    this.filters = { ...EMPTY_FILTERS, dateRange: { from: null, to: null } };
    this.emit();
  }
}
