import { Component, Input, Output, EventEmitter } from '@angular/core';
import { CommonModule } from '@angular/common';
import { TableModule, TablePageEvent } from 'primeng/table';
import { SkeletonModule } from 'primeng/skeleton';

export interface DataTableColumn {
  field: string;
  header: string;
  sortable?: boolean;
  width?: string;
}

export interface PageState {
  page: number;
  rows: number;
  first: number;
}

@Component({
  selector: 'app-data-table',
  standalone: true,
  imports: [CommonModule, TableModule, SkeletonModule],
  template: `
    <p-table
      [value]="data"
      [columns]="columns"
      [lazy]="true"
      [paginator]="true"
      [rows]="pageState.rows"
      [first]="pageState.first"
      [totalRecords]="totalRecords"
      [rowsPerPageOptions]="[20, 50, 100]"
      [loading]="loading"
      (onPage)="onPageChange($event)"
      styleClass="p-datatable-sm p-datatable-striped"
    >
      <ng-template pTemplate="header" let-columns>
        <tr>
          @for (col of columns; track col.field) {
            <th [pSortableColumn]="col.sortable ? col.field : undefined" [style.width]="col.width">
              {{ col.header }}
              @if (col.sortable) { <p-sortIcon [field]="col.field" /> }
            </th>
          }
        </tr>
      </ng-template>

      <ng-template pTemplate="body" let-row let-columns="columns">
        <tr>
          @for (col of columns; track col.field) {
            <td>{{ row[col.field] }}</td>
          }
        </tr>
      </ng-template>

      <ng-template pTemplate="emptymessage">
        <tr>
          <td [attr.colspan]="columns.length" class="text-center p-4">
            No records found.
          </td>
        </tr>
      </ng-template>
    </p-table>
  `,
})
export class DataTableComponent {
  @Input() data: unknown[] = [];
  @Input() columns: DataTableColumn[] = [];
  @Input() totalRecords = 0;
  @Input() loading = false;
  @Input() pageState: PageState = { page: 0, rows: 20, first: 0 };

  @Output() pageChanged = new EventEmitter<PageState>();

  onPageChange(event: TablePageEvent): void {
    this.pageChanged.emit({
      page: (event.first ?? 0) / (event.rows ?? 20),
      rows: event.rows ?? 20,
      first: event.first ?? 0,
    });
  }
}
