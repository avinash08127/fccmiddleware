import { Component, DestroyRef, inject, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TabsModule } from 'primeng/tabs';
import { CardModule } from 'primeng/card';
import { ButtonModule } from 'primeng/button';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { TableModule } from 'primeng/table';
import { DialogModule } from 'primeng/dialog';
import { SelectModule } from 'primeng/select';
import { TooltipModule } from 'primeng/tooltip';
import { ChipModule } from 'primeng/chip';
import { SettingsService } from '../../core/services/settings.service';
import { MasterDataService } from '../../core/services/master-data.service';
import {
  SystemSettings,
  ToleranceDefaults,
  RetentionDefaults,
  LegalEntityOverride,
  AlertThreshold,
  UpdateGlobalDefaultsRequest,
  UpsertLegalEntityOverrideRequest,
  UpdateAlertConfigurationRequest,
} from '../../core/models/settings.model';
import { LegalEntity } from '../../core/models/master-data.model';
import { UtcDatePipe } from '../../shared/pipes/utc-date.pipe';

@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    TabsModule,
    CardModule,
    ButtonModule,
    InputNumberModule,
    InputTextModule,
    TableModule,
    DialogModule,
    SelectModule,
    TooltipModule,
    ChipModule,
    UtcDatePipe,
  ],
  template: `
    <div class="page-container">
      <div class="page-header">
        <h1 class="page-title"><i class="pi pi-cog"></i> System Settings</h1>
        @if (settings()?.updatedAt) {
          <span class="last-updated">
            Last updated {{ settings()!.updatedAt | utcDate: 'medium' }}
            @if (settings()!.updatedBy) {
              by {{ settings()!.updatedBy }}
            }
          </span>
        }
      </div>

      @if (feedbackMessage()) {
        <div
          class="feedback-bar"
          [class.feedback-success]="feedbackSeverity() === 'success'"
          [class.feedback-error]="feedbackSeverity() === 'error'"
        >
          <i
            class="pi"
            [class.pi-check-circle]="feedbackSeverity() === 'success'"
            [class.pi-times-circle]="feedbackSeverity() === 'error'"
          ></i>
          {{ feedbackMessage() }}
        </div>
      }

      @if (loading()) {
        <p-card>
          <div class="loading-placeholder">
            <i class="pi pi-spin pi-spinner" style="font-size: 2rem"></i>
            <span>Loading settings…</span>
          </div>
        </p-card>
      } @else if (settings()) {
        <p-tabs value="0">
          <p-tablist>
            <p-tab value="0">Global Defaults</p-tab>
            <p-tab value="1">Per-Legal-Entity Overrides</p-tab>
            <p-tab value="2">Alert Configuration</p-tab>
            <p-tab value="3">Retention Policies</p-tab>
          </p-tablist>

          <p-tabpanels>
            <!-- ═══ Tab 0: Global Defaults ═══ -->
            <p-tabpanel value="0">
              <p-card header="Reconciliation Tolerance Thresholds" styleClass="settings-card">
                <div class="field-grid">
                  <div class="field">
                    <label for="amtPct">Amount Tolerance (%)</label>
                    <p-inputnumber
                      inputId="amtPct"
                      [(ngModel)]="tolerance.amountTolerancePercent"
                      mode="decimal"
                      [minFractionDigits]="0"
                      [maxFractionDigits]="2"
                      [min]="0"
                      [max]="100"
                      suffix="%"
                    />
                    <small class="field-hint">Variance percentage below which auto-match occurs</small>
                  </div>
                  <div class="field">
                    <label for="amtAbs">Amount Tolerance (Absolute, minor units)</label>
                    <p-inputnumber
                      inputId="amtAbs"
                      [(ngModel)]="tolerance.amountToleranceAbsoluteMinorUnits"
                      [min]="0"
                      [useGrouping]="true"
                    />
                    <small class="field-hint">Absolute variance cap in currency minor units</small>
                  </div>
                  <div class="field">
                    <label for="timeWin">Time Window (minutes)</label>
                    <p-inputnumber
                      inputId="timeWin"
                      [(ngModel)]="tolerance.timeWindowMinutes"
                      [min]="1"
                      [max]="60"
                    />
                    <small class="field-hint">Pump + nozzle + time-window matching window</small>
                  </div>
                  <div class="field">
                    <label for="stalePending">Stale Pending Threshold (days)</label>
                    <p-inputnumber
                      inputId="stalePending"
                      [(ngModel)]="tolerance.stalePendingThresholdDays"
                      [min]="1"
                      [max]="90"
                    />
                    <small class="field-hint">Days before a pending transaction is flagged stale</small>
                  </div>
                </div>
                <div class="actions-row">
                  <p-button
                    label="Save Global Defaults"
                    icon="pi pi-save"
                    [loading]="saving()"
                    (onClick)="saveGlobalDefaults()"
                  />
                </div>
              </p-card>
            </p-tabpanel>

            <!-- ═══ Tab 1: Per-Legal-Entity Overrides ═══ -->
            <p-tabpanel value="1">
              <p-card header="Legal Entity Tolerance Overrides" styleClass="settings-card">
                <div class="override-toolbar">
                  <p-button
                    label="Add Override"
                    icon="pi pi-plus"
                    size="small"
                    (onClick)="openOverrideDialog(null)"
                  />
                </div>
                <p-table
                  [value]="overrides"
                  styleClass="p-datatable-sm p-datatable-striped"
                  [tableStyle]="{ 'min-width': '800px' }"
                >
                  <ng-template pTemplate="header">
                    <tr>
                      <th>Legal Entity</th>
                      <th>Amount Tol. %</th>
                      <th>Amount Tol. Abs.</th>
                      <th>Time Window (min)</th>
                      <th>Stale Pending (days)</th>
                      <th style="width: 8rem">Actions</th>
                    </tr>
                  </ng-template>
                  <ng-template pTemplate="body" let-row>
                    <tr>
                      <td>{{ row.legalEntityName }} ({{ row.legalEntityCode }})</td>
                      <td>{{ row.amountTolerancePercent !== null ? row.amountTolerancePercent + '%' : '—' }}</td>
                      <td>{{ row.amountToleranceAbsoluteMinorUnits ?? '—' }}</td>
                      <td>{{ row.timeWindowMinutes ?? '—' }}</td>
                      <td>{{ row.stalePendingThresholdDays ?? '—' }}</td>
                      <td>
                        <div class="table-actions">
                          <p-button
                            icon="pi pi-pencil"
                            severity="secondary"
                            size="small"
                            [text]="true"
                            pTooltip="Edit"
                            (onClick)="openOverrideDialog(row)"
                          />
                          <p-button
                            icon="pi pi-trash"
                            severity="danger"
                            size="small"
                            [text]="true"
                            pTooltip="Remove"
                            (onClick)="deleteOverride(row.legalEntityId)"
                          />
                        </div>
                      </td>
                    </tr>
                  </ng-template>
                  <ng-template pTemplate="emptymessage">
                    <tr>
                      <td colspan="6" class="empty-message">
                        No legal-entity overrides configured. Global defaults apply to all entities.
                      </td>
                    </tr>
                  </ng-template>
                </p-table>
              </p-card>
            </p-tabpanel>

            <!-- ═══ Tab 2: Alert Configuration ═══ -->
            <p-tabpanel value="2">
              <p-card header="Alert Thresholds" styleClass="settings-card">
                <p-table
                  [value]="alertThresholds"
                  styleClass="p-datatable-sm p-datatable-striped"
                  [tableStyle]="{ 'min-width': '700px' }"
                >
                  <ng-template pTemplate="header">
                    <tr>
                      <th>Alert</th>
                      <th style="width: 10rem">Threshold</th>
                      <th style="width: 5rem">Unit</th>
                      <th style="width: 12rem">Evaluation Window (min)</th>
                    </tr>
                  </ng-template>
                  <ng-template pTemplate="body" let-row let-i="rowIndex">
                    <tr>
                      <td>{{ row.label }}</td>
                      <td>
                        <p-inputnumber
                          [(ngModel)]="alertThresholds[i].threshold"
                          [min]="0"
                          size="small"
                          styleClass="threshold-input"
                        />
                      </td>
                      <td class="unit-cell">{{ row.unit }}</td>
                      <td>
                        <p-inputnumber
                          [(ngModel)]="alertThresholds[i].evaluationWindowMinutes"
                          [min]="1"
                          size="small"
                          styleClass="threshold-input"
                        />
                      </td>
                    </tr>
                  </ng-template>
                </p-table>
              </p-card>

              <p-card header="Notification Settings" styleClass="settings-card">
                <div class="field-grid">
                  <div class="field field--wide">
                    <label for="emailHigh">High-Severity Email Recipients</label>
                    <div class="chip-input-row">
                      <input
                        pInputText
                        id="emailHigh"
                        [(ngModel)]="newEmailHigh"
                        placeholder="Enter email and press Enter"
                        (keydown.enter)="addEmailHigh()"
                      />
                      <p-button icon="pi pi-plus" size="small" (onClick)="addEmailHigh()" />
                    </div>
                    <div class="chip-list">
                      @for (email of emailRecipientsHigh; track email; let i = $index) {
                        <p-chip [label]="email" [removable]="true" (onRemove)="removeEmailHigh(i)" />
                      }
                    </div>
                  </div>
                  <div class="field field--wide">
                    <label for="emailCrit">Critical-Severity Email Recipients</label>
                    <div class="chip-input-row">
                      <input
                        pInputText
                        id="emailCrit"
                        [(ngModel)]="newEmailCritical"
                        placeholder="Enter email and press Enter"
                        (keydown.enter)="addEmailCritical()"
                      />
                      <p-button icon="pi pi-plus" size="small" (onClick)="addEmailCritical()" />
                    </div>
                    <div class="chip-list">
                      @for (email of emailRecipientsCritical; track email; let i = $index) {
                        <p-chip [label]="email" [removable]="true" (onRemove)="removeEmailCritical(i)" />
                      }
                    </div>
                  </div>
                  <div class="field">
                    <label for="renotify">Re-notify Interval (hours)</label>
                    <p-inputnumber
                      inputId="renotify"
                      [(ngModel)]="renotifyIntervalHours"
                      [min]="1"
                      [max]="48"
                    />
                  </div>
                  <div class="field">
                    <label for="autoResolve">Auto-Resolve After N Healthy Checks</label>
                    <p-inputnumber
                      inputId="autoResolve"
                      [(ngModel)]="autoResolveHealthyCount"
                      [min]="1"
                      [max]="20"
                    />
                  </div>
                </div>
                <div class="actions-row">
                  <p-button
                    label="Save Alert Configuration"
                    icon="pi pi-save"
                    [loading]="saving()"
                    (onClick)="saveAlertConfig()"
                  />
                </div>
              </p-card>
            </p-tabpanel>

            <!-- ═══ Tab 3: Retention Policies ═══ -->
            <p-tabpanel value="3">
              <p-card header="Retention Policies" styleClass="settings-card">
                <div class="field-grid">
                  <div class="field">
                    <label for="archiveMonths">Archive Retention (months)</label>
                    <p-inputnumber
                      inputId="archiveMonths"
                      [(ngModel)]="retention.archiveRetentionMonths"
                      [min]="1"
                      [max]="120"
                    />
                    <small class="field-hint">Months before transaction partitions are archived to S3</small>
                  </div>
                  <div class="field">
                    <label for="outboxDays">Outbox Cleanup (days)</label>
                    <p-inputnumber
                      inputId="outboxDays"
                      [(ngModel)]="retention.outboxCleanupDays"
                      [min]="1"
                      [max]="90"
                    />
                    <small class="field-hint">Days before processed outbox messages are purged</small>
                  </div>
                  <div class="field">
                    <label for="rawDays">Raw Payload Retention (days)</label>
                    <p-inputnumber
                      inputId="rawDays"
                      [(ngModel)]="retention.rawPayloadRetentionDays"
                      [min]="1"
                      [max]="365"
                    />
                    <small class="field-hint">Days to retain raw FCC payloads in blob storage</small>
                  </div>
                  <div class="field">
                    <label for="auditDays">Audit Event Retention (days)</label>
                    <p-inputnumber
                      inputId="auditDays"
                      [(ngModel)]="retention.auditEventRetentionDays"
                      [min]="30"
                      [max]="2555"
                    />
                    <small class="field-hint">Days to retain audit events (regulatory minimum may apply)</small>
                  </div>
                  <div class="field">
                    <label for="dlqDays">Dead-Letter Retention (days)</label>
                    <p-inputnumber
                      inputId="dlqDays"
                      [(ngModel)]="retention.deadLetterRetentionDays"
                      [min]="1"
                      [max]="365"
                    />
                    <small class="field-hint">Days to retain resolved dead-letter records</small>
                  </div>
                </div>
                <div class="actions-row">
                  <p-button
                    label="Save Retention Policies"
                    icon="pi pi-save"
                    [loading]="saving()"
                    (onClick)="saveRetentionPolicies()"
                  />
                </div>
              </p-card>
            </p-tabpanel>
          </p-tabpanels>
        </p-tabs>
      }

      <!-- Override add/edit dialog -->
      <p-dialog
        header="{{ editingOverride ? 'Edit Override' : 'Add Override' }}"
        [(visible)]="overrideDialogVisible"
        [modal]="true"
        [style]="{ width: '520px' }"
      >
        <div class="dialog-fields">
          @if (!editingOverride) {
            <div class="field">
              <label for="override-legal-entity">Legal Entity</label>
              <p-select
                inputId="override-legal-entity"
                [options]="availableLegalEntities()"
                [(ngModel)]="overrideForm.legalEntityId"
                optionLabel="label"
                optionValue="value"
                placeholder="Select legal entity"
                styleClass="w-full"
              />
            </div>
          } @else {
            <div class="field">
              <label for="override-legal-entity-readonly">Legal Entity</label>
              <span id="override-legal-entity-readonly" class="readonly-value">{{ editingOverride.legalEntityName }} ({{ editingOverride.legalEntityCode }})</span>
            </div>
          }
          <div class="field">
            <label for="override-amt-pct">Amount Tolerance (%)</label>
            <p-inputnumber
              inputId="override-amt-pct"
              [(ngModel)]="overrideForm.amountTolerancePercent"
              mode="decimal"
              [minFractionDigits]="0"
              [maxFractionDigits]="2"
              [min]="0"
              [max]="100"
              suffix="%"
              placeholder="Leave blank to use global"
            />
          </div>
          <div class="field">
            <label for="override-amt-abs">Amount Tolerance (Absolute)</label>
            <p-inputnumber
              inputId="override-amt-abs"
              [(ngModel)]="overrideForm.amountToleranceAbsoluteMinorUnits"
              [min]="0"
              placeholder="Leave blank to use global"
            />
          </div>
          <div class="field">
            <label for="override-time-window">Time Window (minutes)</label>
            <p-inputnumber
              inputId="override-time-window"
              [(ngModel)]="overrideForm.timeWindowMinutes"
              [min]="1"
              [max]="60"
              placeholder="Leave blank to use global"
            />
          </div>
          <div class="field">
            <label for="override-stale-pending">Stale Pending Threshold (days)</label>
            <p-inputnumber
              inputId="override-stale-pending"
              [(ngModel)]="overrideForm.stalePendingThresholdDays"
              [min]="1"
              [max]="90"
              placeholder="Leave blank to use global"
            />
          </div>
        </div>
        <ng-template pTemplate="footer">
          <p-button label="Cancel" severity="secondary" (onClick)="overrideDialogVisible = false" />
          <p-button
            label="Save"
            icon="pi pi-save"
            [loading]="saving()"
            [disabled]="!overrideForm.legalEntityId"
            (onClick)="saveOverride()"
          />
        </ng-template>
      </p-dialog>
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
      .last-updated {
        font-size: 0.8rem;
        color: var(--p-text-muted-color, #64748b);
      }
      .loading-placeholder {
        display: flex;
        align-items: center;
        justify-content: center;
        gap: 0.75rem;
        padding: 3rem;
        color: var(--p-text-muted-color, #64748b);
      }
      .settings-card {
        margin-bottom: 1rem;
      }
      .field-grid {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(260px, 1fr));
        gap: 1.25rem 1.5rem;
      }
      .field {
        display: flex;
        flex-direction: column;
        gap: 0.3rem;
      }
      .field label {
        font-size: 0.8rem;
        font-weight: 600;
        color: var(--p-text-muted-color, #64748b);
        text-transform: uppercase;
        letter-spacing: 0.04em;
      }
      .field--wide {
        grid-column: 1 / -1;
      }
      .field-hint {
        font-size: 0.75rem;
        color: var(--p-text-muted-color, #94a3b8);
      }
      .actions-row {
        display: flex;
        justify-content: flex-end;
        margin-top: 1.25rem;
        padding-top: 1rem;
        border-top: 1px solid var(--p-surface-200, #e2e8f0);
      }
      .override-toolbar {
        display: flex;
        justify-content: flex-end;
        margin-bottom: 0.75rem;
      }
      .table-actions {
        display: flex;
        gap: 0.25rem;
      }
      .empty-message {
        text-align: center;
        color: var(--p-text-muted-color, #64748b);
        padding: 1.5rem;
      }
      .threshold-input {
        width: 100%;
      }
      .unit-cell {
        color: var(--p-text-muted-color, #64748b);
        font-size: 0.85rem;
      }
      .chip-input-row {
        display: flex;
        gap: 0.5rem;
        align-items: center;
      }
      .chip-input-row input {
        flex: 1;
      }
      .chip-list {
        display: flex;
        flex-wrap: wrap;
        gap: 0.35rem;
        margin-top: 0.35rem;
      }
      .dialog-fields {
        display: flex;
        flex-direction: column;
        gap: 1rem;
      }
      .readonly-value {
        font-weight: 600;
        color: var(--p-text-color, #1e293b);
      }
      .feedback-bar {
        display: flex;
        align-items: center;
        gap: 0.5rem;
        padding: 0.65rem 1rem;
        border-radius: 6px;
        margin-bottom: 0.75rem;
        font-size: 0.875rem;
        border: 1px solid;
      }
      .feedback-success {
        background: var(--p-green-50, #f0fdf4);
        border-color: var(--p-green-300, #86efac);
        color: var(--p-green-800, #166534);
      }
      .feedback-error {
        background: var(--p-red-50, #fef2f2);
        border-color: var(--p-red-300, #fca5a5);
        color: var(--p-red-800, #991b1b);
      }
    `,
  ],
})
export class SettingsComponent implements OnInit {
  private readonly settingsService = inject(SettingsService);
  private readonly masterDataService = inject(MasterDataService);
  private readonly destroyRef = inject(DestroyRef);

  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly settings = signal<SystemSettings | null>(null);
  readonly feedbackMessage = signal<string | null>(null);
  readonly feedbackSeverity = signal<'success' | 'error'>('success');

  // ── Global Defaults (Tab 0) ────────────────────────────────────────────────
  tolerance: ToleranceDefaults = {
    amountTolerancePercent: 0,
    amountToleranceAbsoluteMinorUnits: 0,
    timeWindowMinutes: 5,
    stalePendingThresholdDays: 7,
  };

  // ── Overrides (Tab 1) ──────────────────────────────────────────────────────
  overrides: LegalEntityOverride[] = [];
  private legalEntities = signal<LegalEntity[]>([]);
  overrideDialogVisible = false;
  editingOverride: LegalEntityOverride | null = null;
  overrideForm = this.emptyOverrideForm();

  readonly availableLegalEntities = () => {
    const usedIds = new Set(this.overrides.map((o) => o.legalEntityId));
    return this.legalEntities()
      .filter((e) => !usedIds.has(e.id))
      .map((e) => ({ label: `${e.name} (${e.code})`, value: e.id }));
  };

  // ── Alert Configuration (Tab 2) ────────────────────────────────────────────
  alertThresholds: AlertThreshold[] = [];
  emailRecipientsHigh: string[] = [];
  emailRecipientsCritical: string[] = [];
  newEmailHigh = '';
  newEmailCritical = '';
  renotifyIntervalHours = 4;
  autoResolveHealthyCount = 3;

  // ── Retention (Tab 3) ──────────────────────────────────────────────────────
  retention: RetentionDefaults = {
    archiveRetentionMonths: 24,
    outboxCleanupDays: 7,
    rawPayloadRetentionDays: 90,
    auditEventRetentionDays: 2555,
    deadLetterRetentionDays: 30,
  };

  ngOnInit(): void {
    this.loadSettings();
    this.masterDataService
      .getLegalEntities()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ next: (entities) => this.legalEntities.set(entities) });
  }

  // ── Save actions ───────────────────────────────────────────────────────────

  saveGlobalDefaults(): void {
    const req: UpdateGlobalDefaultsRequest = {
      tolerance: { ...this.tolerance },
      retention: { ...this.retention },
    };
    this.saving.set(true);
    this.settingsService
      .updateGlobalDefaults(req)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (s) => this.handleSaveSuccess(s, 'Global defaults saved.'),
        error: () => this.handleSaveError('Failed to save global defaults.'),
      });
  }

  saveRetentionPolicies(): void {
    const req: UpdateGlobalDefaultsRequest = {
      tolerance: { ...this.tolerance },
      retention: { ...this.retention },
    };
    this.saving.set(true);
    this.settingsService
      .updateGlobalDefaults(req)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (s) => this.handleSaveSuccess(s, 'Retention policies saved.'),
        error: () => this.handleSaveError('Failed to save retention policies.'),
      });
  }

  saveAlertConfig(): void {
    const req: UpdateAlertConfigurationRequest = {
      thresholds: this.alertThresholds.map((t) => ({
        alertKey: t.alertKey,
        threshold: t.threshold,
        evaluationWindowMinutes: t.evaluationWindowMinutes,
      })),
      emailRecipientsHigh: [...this.emailRecipientsHigh],
      emailRecipientsCritical: [...this.emailRecipientsCritical],
      renotifyIntervalHours: this.renotifyIntervalHours,
      autoResolveHealthyCount: this.autoResolveHealthyCount,
    };
    this.saving.set(true);
    this.settingsService
      .updateAlertConfiguration(req)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (s) => this.handleSaveSuccess(s, 'Alert configuration saved.'),
        error: () => this.handleSaveError('Failed to save alert configuration.'),
      });
  }

  // ── Override dialog ────────────────────────────────────────────────────────

  openOverrideDialog(existing: LegalEntityOverride | null): void {
    this.editingOverride = existing;
    if (existing) {
      this.overrideForm = {
        legalEntityId: existing.legalEntityId,
        amountTolerancePercent: existing.amountTolerancePercent,
        amountToleranceAbsoluteMinorUnits: existing.amountToleranceAbsoluteMinorUnits,
        timeWindowMinutes: existing.timeWindowMinutes,
        stalePendingThresholdDays: existing.stalePendingThresholdDays,
      };
    } else {
      this.overrideForm = this.emptyOverrideForm();
    }
    this.overrideDialogVisible = true;
  }

  saveOverride(): void {
    if (!this.overrideForm.legalEntityId) return;
    const req: UpsertLegalEntityOverrideRequest = { ...this.overrideForm };
    this.saving.set(true);
    this.settingsService
      .upsertLegalEntityOverride(req)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (s) => {
          this.overrideDialogVisible = false;
          this.handleSaveSuccess(s, 'Override saved.');
        },
        error: () => this.handleSaveError('Failed to save override.'),
      });
  }

  deleteOverride(legalEntityId: string): void {
    this.saving.set(true);
    this.settingsService
      .deleteLegalEntityOverride(legalEntityId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (s) => this.handleSaveSuccess(s, 'Override removed.'),
        error: () => this.handleSaveError('Failed to remove override.'),
      });
  }

  // ── Email chip helpers ─────────────────────────────────────────────────────

  addEmailHigh(): void {
    const email = this.newEmailHigh.trim();
    if (email && !this.emailRecipientsHigh.includes(email)) {
      this.emailRecipientsHigh = [...this.emailRecipientsHigh, email];
    }
    this.newEmailHigh = '';
  }

  removeEmailHigh(index: number): void {
    this.emailRecipientsHigh = this.emailRecipientsHigh.filter((_, i) => i !== index);
  }

  addEmailCritical(): void {
    const email = this.newEmailCritical.trim();
    if (email && !this.emailRecipientsCritical.includes(email)) {
      this.emailRecipientsCritical = [...this.emailRecipientsCritical, email];
    }
    this.newEmailCritical = '';
  }

  removeEmailCritical(index: number): void {
    this.emailRecipientsCritical = this.emailRecipientsCritical.filter((_, i) => i !== index);
  }

  // ── Private ────────────────────────────────────────────────────────────────

  private loadSettings(): void {
    this.loading.set(true);
    this.settingsService
      .getSettings()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (s) => {
          this.applySettings(s);
          this.loading.set(false);
        },
        error: () => {
          this.loading.set(false);
          this.setFeedback('error', 'Failed to load settings.');
        },
      });
  }

  private applySettings(s: SystemSettings): void {
    this.settings.set(s);

    // Global defaults
    this.tolerance = { ...s.globalDefaults.tolerance };
    this.retention = { ...s.globalDefaults.retention };

    // Overrides
    this.overrides = [...s.legalEntityOverrides];

    // Alerts
    this.alertThresholds = s.alerts.thresholds.map((t) => ({ ...t }));
    this.emailRecipientsHigh = [...s.alerts.emailRecipientsHigh];
    this.emailRecipientsCritical = [...s.alerts.emailRecipientsCritical];
    this.renotifyIntervalHours = s.alerts.renotifyIntervalHours;
    this.autoResolveHealthyCount = s.alerts.autoResolveHealthyCount;
  }

  private handleSaveSuccess(s: SystemSettings, message: string): void {
    this.saving.set(false);
    this.applySettings(s);
    this.setFeedback('success', message);
  }

  private handleSaveError(message: string): void {
    this.saving.set(false);
    this.setFeedback('error', message);
  }

  private setFeedback(severity: 'success' | 'error', message: string): void {
    this.feedbackSeverity.set(severity);
    this.feedbackMessage.set(message);
    setTimeout(() => this.feedbackMessage.set(null), 5000);
  }

  private emptyOverrideForm(): UpsertLegalEntityOverrideRequest {
    return {
      legalEntityId: '',
      amountTolerancePercent: null,
      amountToleranceAbsoluteMinorUnits: null,
      timeWindowMinutes: null,
      stalePendingThresholdDays: null,
    };
  }
}
