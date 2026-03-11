import { Component, Input, Output, EventEmitter, OnChanges, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { CardModule } from 'primeng/card';
import { SelectModule } from 'primeng/select';
import { InputTextModule } from 'primeng/inputtext';
import { InputNumberModule } from 'primeng/inputnumber';
import { ToggleSwitchModule } from 'primeng/toggleswitch';
import { ButtonModule } from 'primeng/button';
import { ChipModule } from 'primeng/chip';

import {
  FccConfig,
  FccConnectionProtocol,
  TransactionMode,
  IngestionMode,
} from '../../core/models/site.model';
import { FccVendor } from '../../core/models/transaction.model';

export type FccConfigDraft = Pick<
  FccConfig,
  | 'vendor'
  | 'connectionProtocol'
  | 'hostAddress'
  | 'port'
  | 'transactionMode'
  | 'ingestionMode'
  | 'pullIntervalSeconds'
  | 'heartbeatIntervalSeconds'
  | 'heartbeatTimeoutSeconds'
  | 'enabled'
>;

@Component({
  selector: 'app-fcc-config-form',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    CardModule,
    SelectModule,
    InputTextModule,
    InputNumberModule,
    ToggleSwitchModule,
    ButtonModule,
    ChipModule,
  ],
  template: `
    <p-card styleClass="section-card">
      <ng-template pTemplate="header">
        <div class="section-header">
          <span><i class="pi pi-server"></i> FCC Configuration</span>
          @if (fccId) {
            <code class="fcc-id">{{ fccId }}</code>
          }
        </div>
      </ng-template>

      @if (!draft) {
        <p class="no-config-hint">No FCC configuration has been set for this site yet.</p>
      } @else {
        <div class="form-grid">
          <!-- Enabled toggle -->
          <div class="form-field form-field--toggle">
            <p-toggleswitch
              [(ngModel)]="draft.enabled"
              [disabled]="!editMode"
              (ngModelChange)="onDraftChange()"
            />
            <label>FCC Integration Enabled</label>
          </div>

          <!-- Vendor -->
          <div class="form-field">
            <label>FCC Vendor <span class="required">*</span></label>
            <p-select
              [options]="vendorOptions"
              [(ngModel)]="draft.vendor"
              placeholder="Select vendor"
              [disabled]="!editMode"
              (ngModelChange)="onDraftChange()"
            />
          </div>

          <!-- Protocol -->
          <div class="form-field">
            <label>Connection Protocol <span class="required">*</span></label>
            <p-select
              [options]="protocolOptions"
              [(ngModel)]="draft.connectionProtocol"
              placeholder="Select protocol"
              [disabled]="!editMode"
              (ngModelChange)="onDraftChange()"
            />
          </div>

          <!-- Host -->
          <div class="form-field">
            <label>Host Address <span class="required">*</span></label>
            <input
              pInputText
              [(ngModel)]="draft.hostAddress"
              placeholder="e.g. 192.168.1.100"
              [disabled]="!editMode"
              (ngModelChange)="onDraftChange()"
            />
            @if (editMode && !draft.hostAddress) {
              <small class="validation-error">Host address is required.</small>
            }
          </div>

          <!-- Port -->
          <div class="form-field">
            <label>Port <span class="required">*</span></label>
            <p-inputnumber
              [(ngModel)]="draft.port"
              placeholder="e.g. 8080"
              [min]="1"
              [max]="65535"
              [showButtons]="false"
              [useGrouping]="false"
              [disabled]="!editMode"
              (ngModelChange)="onDraftChange()"
            />
            @if (editMode && (draft.port == null || draft.port < 1 || draft.port > 65535)) {
              <small class="validation-error">Port must be 1–65535.</small>
            }
          </div>

          <!-- Transaction mode -->
          <div class="form-field">
            <label>Transaction Mode</label>
            <p-select
              [options]="transactionModeOptions"
              [(ngModel)]="draft.transactionMode"
              placeholder="Select mode"
              [showClear]="true"
              [disabled]="!editMode"
              (ngModelChange)="onDraftChange()"
            />
          </div>

          <!-- Ingestion mode -->
          <div class="form-field">
            <label>Ingestion Mode</label>
            <p-select
              [options]="ingestionModeOptions"
              [(ngModel)]="draft.ingestionMode"
              placeholder="Select mode"
              [showClear]="true"
              [disabled]="!editMode"
              (ngModelChange)="onDraftChange()"
            />
          </div>

          <!-- Pull interval (only when PULL or HYBRID) -->
          @if (draft.transactionMode === 'PULL' || draft.transactionMode === 'HYBRID') {
            <div class="form-field">
              <label>Pull Interval (seconds)</label>
              <p-inputnumber
                [(ngModel)]="draft.pullIntervalSeconds"
                [min]="5"
                [max]="3600"
                [showButtons]="false"
                [useGrouping]="false"
                [disabled]="!editMode"
                (ngModelChange)="onDraftChange()"
              />
            </div>
          }

          <!-- Heartbeat interval -->
          <div class="form-field">
            <label>Heartbeat Interval (seconds) <span class="required">*</span></label>
            <p-inputnumber
              [(ngModel)]="draft.heartbeatIntervalSeconds"
              [min]="10"
              [max]="3600"
              [showButtons]="false"
              [useGrouping]="false"
              [disabled]="!editMode"
              (ngModelChange)="onDraftChange()"
            />
          </div>

          <!-- Heartbeat timeout -->
          <div class="form-field">
            <label>Heartbeat Timeout (seconds) <span class="required">*</span></label>
            <p-inputnumber
              [(ngModel)]="draft.heartbeatTimeoutSeconds"
              [min]="10"
              [max]="3600"
              [showButtons]="false"
              [useGrouping]="false"
              [disabled]="!editMode"
              (ngModelChange)="onDraftChange()"
            />
          </div>
        </div>
      }
    </p-card>
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
        gap: 0.75rem;
        flex-wrap: wrap;
      }
      .section-header .pi {
        margin-right: 0.4rem;
        color: var(--p-primary-color, #3b82f6);
      }
      .fcc-id {
        font-family: monospace;
        font-size: 0.75rem;
        background: var(--p-surface-100, #f1f5f9);
        padding: 0.15rem 0.4rem;
        border-radius: 4px;
        color: var(--p-text-muted-color, #64748b);
      }
      .form-grid {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
        gap: 1rem 1.25rem;
        align-items: start;
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
      .form-field--toggle {
        flex-direction: row;
        align-items: center;
        gap: 0.5rem;
        grid-column: 1 / -1;
      }
      .form-field--toggle label {
        text-transform: none;
        letter-spacing: 0;
        font-size: 0.9rem;
        font-weight: 500;
        color: var(--p-text-color);
      }
      .required {
        color: var(--p-red-600, #dc2626);
        margin-left: 2px;
      }
      .validation-error {
        color: var(--p-red-600, #dc2626);
        font-size: 0.75rem;
      }
      .no-config-hint {
        color: var(--p-text-muted-color, #64748b);
        font-style: italic;
        margin: 0;
      }
    `,
  ],
})
export class FccConfigFormComponent implements OnChanges {
  @Input() fccConfig: FccConfig | null = null;
  @Input() fccId: string | null = null;
  @Input() editMode = false;
  @Output() configChange = new EventEmitter<FccConfigDraft>();

  draft: FccConfigDraft | null = null;

  readonly vendorOptions = [
    { label: 'DOMS', value: FccVendor.DOMS },
    { label: 'RADIX', value: FccVendor.RADIX },
    { label: 'ADVATEC', value: FccVendor.ADVATEC },
    { label: 'PETRONITE', value: FccVendor.PETRONITE },
  ];

  readonly protocolOptions = [
    { label: 'REST', value: FccConnectionProtocol.REST },
    { label: 'TCP', value: FccConnectionProtocol.TCP },
    { label: 'SOAP', value: FccConnectionProtocol.SOAP },
  ];

  readonly transactionModeOptions = [
    { label: 'PULL', value: TransactionMode.PULL },
    { label: 'PUSH', value: TransactionMode.PUSH },
    { label: 'HYBRID', value: TransactionMode.HYBRID },
  ];

  readonly ingestionModeOptions = [
    { label: 'Cloud Direct', value: IngestionMode.CLOUD_DIRECT },
    { label: 'Relay', value: IngestionMode.RELAY },
    { label: 'Buffer Always', value: IngestionMode.BUFFER_ALWAYS },
  ];

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['fccConfig'] && this.fccConfig) {
      this.draft = {
        vendor: this.fccConfig.vendor,
        connectionProtocol: this.fccConfig.connectionProtocol,
        hostAddress: this.fccConfig.hostAddress,
        port: this.fccConfig.port,
        transactionMode: this.fccConfig.transactionMode,
        ingestionMode: this.fccConfig.ingestionMode,
        pullIntervalSeconds: this.fccConfig.pullIntervalSeconds,
        heartbeatIntervalSeconds: this.fccConfig.heartbeatIntervalSeconds,
        heartbeatTimeoutSeconds: this.fccConfig.heartbeatTimeoutSeconds,
        enabled: this.fccConfig.enabled,
      };
    }
  }

  onDraftChange(): void {
    if (this.draft) {
      this.configChange.emit({ ...this.draft });
    }
  }

  isValid(): boolean {
    if (!this.draft) return false;
    return (
      !!this.draft.vendor &&
      !!this.draft.connectionProtocol &&
      !!this.draft.hostAddress?.trim() &&
      this.draft.port != null &&
      this.draft.port >= 1 &&
      this.draft.port <= 65535 &&
      this.draft.heartbeatIntervalSeconds > 0 &&
      this.draft.heartbeatTimeoutSeconds > 0
    );
  }
}
