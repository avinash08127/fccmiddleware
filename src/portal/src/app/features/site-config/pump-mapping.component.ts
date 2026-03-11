import { Component, Input, Output, EventEmitter } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TableModule } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { CardModule } from 'primeng/card';
import { DialogModule } from 'primeng/dialog';
import { SelectModule } from 'primeng/select';
import { InputNumberModule } from 'primeng/inputnumber';

import { Pump, Product } from '../../core/models/site.model';
import { AddPumpRequest } from '../../core/models/site.model';

export interface NozzleUpdateEvent {
  pumpId: string;
  nozzleNumber: number;
  canonicalProductCode: string;
}

interface NozzleRow {
  pumpId: string;
  pumpNumber: number;
  nozzleNumber: number;
  canonicalProductCode: string;
  editingCode: string;
  editing: boolean;
}

@Component({
  selector: 'app-pump-mapping',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    TableModule,
    ButtonModule,
    CardModule,
    DialogModule,
    SelectModule,
    InputNumberModule,
  ],
  template: `
    <p-card styleClass="section-card">
      <ng-template pTemplate="header">
        <div class="section-header">
          <span><i class="pi pi-sliders-h"></i> Pump & Nozzle Mapping</span>
          @if (editMode) {
            <p-button
              label="Add Pump"
              icon="pi pi-plus"
              size="small"
              (onClick)="openAddPump()"
            />
          }
        </div>
      </ng-template>

      @if (pumps.length === 0) {
        <p class="no-pumps-hint">No pumps configured for this site.</p>
      } @else {
        <p-table
          [value]="nozzleRows"
          [rowGroupMode]="'rowspan'"
          groupRowsBy="pumpNumber"
          styleClass="p-datatable-sm p-datatable-striped"
        >
          <ng-template pTemplate="header">
            <tr>
              <th style="width:6rem">Pump #</th>
              <th style="width:6rem">Nozzle #</th>
              <th>Product Code</th>
              @if (editMode) {
                <th style="width:8rem">Actions</th>
              }
            </tr>
          </ng-template>

          <ng-template pTemplate="body" let-row let-rowIndex="rowIndex">
            <tr>
              <td>
                <div class="pump-cell">
                  <span class="pump-badge">P{{ row.pumpNumber }}</span>
                  @if (editMode) {
                    <p-button
                      icon="pi pi-trash"
                      severity="danger"
                      size="small"
                      [text]="true"
                      pTooltip="Remove pump"
                      (onClick)="onRemovePump(row.pumpId)"
                    />
                  }
                </div>
              </td>
              <td>{{ row.nozzleNumber }}</td>
              <td>
                @if (row.editing) {
                  <div class="inline-edit">
                    <p-select
                      [options]="productOptions"
                      [(ngModel)]="row.editingCode"
                      placeholder="Select product"
                      [style]="{ width: '200px' }"
                    />
                    <p-button
                      icon="pi pi-check"
                      severity="success"
                      size="small"
                      [text]="true"
                      (onClick)="saveNozzleEdit(row)"
                    />
                    <p-button
                      icon="pi pi-times"
                      severity="secondary"
                      size="small"
                      [text]="true"
                      (onClick)="cancelNozzleEdit(row)"
                    />
                  </div>
                } @else {
                  <code class="product-code">{{ row.canonicalProductCode || '—' }}</code>
                }
              </td>
              @if (editMode) {
                <td>
                  @if (!row.editing) {
                    <p-button
                      icon="pi pi-pencil"
                      size="small"
                      [text]="true"
                      pTooltip="Edit product mapping"
                      (onClick)="startNozzleEdit(row)"
                    />
                  }
                </td>
              }
            </tr>
          </ng-template>

          <ng-template pTemplate="emptymessage">
            <tr>
              <td [attr.colspan]="editMode ? 4 : 3" class="empty-row">No nozzles configured.</td>
            </tr>
          </ng-template>
        </p-table>
      }
    </p-card>

    <!-- Add pump dialog -->
    <p-dialog
      [(visible)]="showAddDialog"
      header="Add Pump"
      [modal]="true"
      [style]="{ width: '420px' }"
    >
      <div class="dialog-form">
        <div class="form-field">
          <label>Pump Number <span class="required">*</span></label>
          <p-inputnumber
            [(ngModel)]="newPump.pumpNumber"
            [min]="1"
            [max]="99"
            [showButtons]="false"
            [useGrouping]="false"
            placeholder="Logical pump number"
          />
        </div>

        <div class="form-field">
          <label>FCC Pump Number <span class="required">*</span></label>
          <p-inputnumber
            [(ngModel)]="newPump.fccPumpNumber"
            [min]="1"
            [max]="99"
            [showButtons]="false"
            [useGrouping]="false"
            placeholder="FCC-side pump number"
          />
        </div>

        <div class="nozzle-section">
          <div class="nozzle-section-header">
            <span class="nozzle-title">Nozzles</span>
            <p-button
              label="Add Nozzle"
              icon="pi pi-plus"
              size="small"
              severity="secondary"
              (onClick)="addNozzleToNew()"
            />
          </div>

          @for (nozzle of newPump.nozzles; track $index; let i = $index) {
            <div class="nozzle-row">
              <div class="form-field">
                <label>Nozzle #</label>
                <p-inputnumber
                  [(ngModel)]="nozzle.nozzleNumber"
                  [min]="1"
                  [max]="99"
                  [showButtons]="false"
                  [useGrouping]="false"
                />
              </div>
              <div class="form-field">
                <label>FCC Nozzle #</label>
                <p-inputnumber
                  [(ngModel)]="nozzle.fccNozzleNumber"
                  [min]="1"
                  [max]="99"
                  [showButtons]="false"
                  [useGrouping]="false"
                />
              </div>
              <div class="form-field">
                <label>Product</label>
                <p-select
                  [options]="productOptions"
                  [(ngModel)]="nozzle.canonicalProductCode"
                  placeholder="Select product"
                />
              </div>
              <p-button
                icon="pi pi-trash"
                severity="danger"
                size="small"
                [text]="true"
                (onClick)="removeNozzleFromNew(i)"
              />
            </div>
          }
        </div>
      </div>

      <ng-template pTemplate="footer">
        <p-button label="Cancel" severity="secondary" (onClick)="showAddDialog = false" />
        <p-button
          label="Add Pump"
          icon="pi pi-check"
          [disabled]="!canSubmitNewPump()"
          (onClick)="submitAddPump()"
        />
      </ng-template>
    </p-dialog>
  `,
  styles: [
    `
      .section-header {
        display: flex;
        align-items: center;
        justify-content: space-between;
        padding: 0.75rem 1.25rem;
        font-weight: 700;
        font-size: 0.95rem;
      }
      .section-header .pi {
        margin-right: 0.4rem;
        color: var(--p-primary-color, #3b82f6);
      }
      .pump-badge {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        background: var(--p-primary-100, #dbeafe);
        color: var(--p-primary-700, #1d4ed8);
        font-weight: 700;
        font-size: 0.78rem;
        border-radius: 4px;
        padding: 0.1rem 0.4rem;
        min-width: 2.2rem;
      }
      .pump-cell {
        display: flex;
        align-items: center;
        gap: 0.25rem;
      }
      .product-code {
        font-family: monospace;
        font-size: 0.82rem;
        background: var(--p-surface-100, #f1f5f9);
        padding: 0.1rem 0.4rem;
        border-radius: 4px;
      }
      .inline-edit {
        display: flex;
        align-items: center;
        gap: 0.3rem;
        flex-wrap: wrap;
      }
      .no-pumps-hint {
        color: var(--p-text-muted-color, #64748b);
        font-style: italic;
        margin: 0;
      }
      .empty-row {
        text-align: center;
        color: var(--p-text-muted-color, #94a3b8);
        font-style: italic;
        padding: 1.5rem;
      }
      .dialog-form {
        display: flex;
        flex-direction: column;
        gap: 1rem;
      }
      .form-field {
        display: flex;
        flex-direction: column;
        gap: 0.3rem;
      }
      .form-field label {
        font-size: 0.78rem;
        font-weight: 600;
        color: var(--p-text-muted-color, #64748b);
        text-transform: uppercase;
        letter-spacing: 0.04em;
      }
      .required {
        color: var(--p-red-600, #dc2626);
        margin-left: 2px;
      }
      .nozzle-section {
        border: 1px solid var(--p-surface-300, #e2e8f0);
        border-radius: 6px;
        padding: 0.75rem;
        display: flex;
        flex-direction: column;
        gap: 0.75rem;
      }
      .nozzle-section-header {
        display: flex;
        justify-content: space-between;
        align-items: center;
      }
      .nozzle-title {
        font-size: 0.82rem;
        font-weight: 700;
        text-transform: uppercase;
        letter-spacing: 0.05em;
        color: var(--p-text-muted-color, #64748b);
      }
      .nozzle-row {
        display: grid;
        grid-template-columns: 1fr 1fr 1fr auto;
        gap: 0.5rem;
        align-items: end;
      }
    `,
  ],
})
export class PumpMappingComponent {
  @Input() pumps: Pump[] = [];
  @Input() products: Product[] = [];
  @Input() editMode = false;
  @Output() pumpRemoved = new EventEmitter<string>();
  @Output() pumpAdded = new EventEmitter<AddPumpRequest>();
  @Output() nozzleUpdated = new EventEmitter<NozzleUpdateEvent>();

  showAddDialog = false;

  newPump: AddPumpRequest = {
    pumpNumber: 1,
    fccPumpNumber: 1,
    nozzles: [],
  };

  get nozzleRows(): NozzleRow[] {
    const rows: NozzleRow[] = [];
    for (const pump of this.pumps) {
      for (const nozzle of pump.nozzles) {
        rows.push({
          pumpId: pump.id,
          pumpNumber: pump.pumpNumber,
          nozzleNumber: nozzle.nozzleNumber,
          canonicalProductCode: nozzle.canonicalProductCode,
          editingCode: nozzle.canonicalProductCode,
          editing: false,
        });
      }
    }
    return rows;
  }

  get productOptions(): { label: string; value: string }[] {
    return this.products.map((p) => ({
      label: `${p.displayName} (${p.canonicalCode})`,
      value: p.canonicalCode,
    }));
  }

  openAddPump(): void {
    this.newPump = { pumpNumber: 1, fccPumpNumber: 1, nozzles: [] };
    this.showAddDialog = true;
  }

  addNozzleToNew(): void {
    const nextNozzle = this.newPump.nozzles.length + 1;
    this.newPump.nozzles = [
      ...this.newPump.nozzles,
      { nozzleNumber: nextNozzle, fccNozzleNumber: nextNozzle, canonicalProductCode: '' },
    ];
  }

  removeNozzleFromNew(index: number): void {
    this.newPump.nozzles = this.newPump.nozzles.filter((_, i) => i !== index);
  }

  canSubmitNewPump(): boolean {
    return (
      this.newPump.pumpNumber >= 1 &&
      this.newPump.fccPumpNumber >= 1 &&
      this.newPump.nozzles.length > 0 &&
      this.newPump.nozzles.every((n) => n.nozzleNumber >= 1 && !!n.canonicalProductCode)
    );
  }

  submitAddPump(): void {
    if (!this.canSubmitNewPump()) return;
    this.pumpAdded.emit({ ...this.newPump });
    this.showAddDialog = false;
  }

  startNozzleEdit(row: NozzleRow): void {
    row.editing = true;
    row.editingCode = row.canonicalProductCode;
  }

  saveNozzleEdit(row: NozzleRow): void {
    row.editing = false;
    row.canonicalProductCode = row.editingCode;
    this.nozzleUpdated.emit({
      pumpId: row.pumpId,
      nozzleNumber: row.nozzleNumber,
      canonicalProductCode: row.editingCode,
    });
  }

  cancelNozzleEdit(row: NozzleRow): void {
    row.editing = false;
    row.editingCode = row.canonicalProductCode;
  }

  onRemovePump(pumpId: string): void {
    this.pumpRemoved.emit(pumpId);
  }
}
