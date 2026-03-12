import { Component, Input, Output, EventEmitter } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { PanelModule } from 'primeng/panel';
import { SelectModule } from 'primeng/select';
import { ButtonModule } from 'primeng/button';

import {
  DateRangePickerComponent,
  DateRange,
} from '../../shared/components/date-range-picker/date-range-picker.component';

export interface ReconciliationFilters {
  siteCode: string;
  dateRange: DateRange;
}

export const EMPTY_RECON_FILTERS: ReconciliationFilters = {
  siteCode: '',
  dateRange: { from: null, to: null },
};

@Component({
  selector: 'app-reconciliation-filters',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    PanelModule,
    SelectModule,
    ButtonModule,
    DateRangePickerComponent,
  ],
  template: `
    <p-panel header="Filters" [toggleable]="true" styleClass="filters-panel">
      <div class="filters-grid">
        <div class="filter-field">
          <label for="recon-filter-site">Site</label>
          <p-select
            inputId="recon-filter-site"
            [options]="siteOptions"
            [(ngModel)]="filters.siteCode"
            placeholder="All Sites"
            [showClear]="true"
            (ngModelChange)="emit()"
          />
        </div>

        <div class="filter-field filter-field--wide">
          <label for="recon-filter-date-range">Created At (range)</label>
          <app-date-range-picker
            id="recon-filter-date-range"
            placeholder="Select date range"
            [(ngModel)]="filters.dateRange"
            (rangeSelected)="onDateRangeSelected($event)"
          />
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
      .filter-field--action {
        align-self: end;
      }
    `,
  ],
})
export class ReconciliationFiltersComponent {
  @Input() siteOptions: { label: string; value: string }[] = [];
  @Output() filtersChange = new EventEmitter<ReconciliationFilters>();

  filters: ReconciliationFilters = { ...EMPTY_RECON_FILTERS, dateRange: { from: null, to: null } };

  emit(): void {
    this.filtersChange.emit({ ...this.filters });
  }

  onDateRangeSelected(range: DateRange): void {
    this.filters.dateRange = range;
    this.emit();
  }

  clear(): void {
    this.filters = { ...EMPTY_RECON_FILTERS, dateRange: { from: null, to: null } };
    this.emit();
  }
}
