import {
  Component,
  DestroyRef,
  OnInit,
  inject,
  signal,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CardModule } from 'primeng/card';
import { SelectModule } from 'primeng/select';
import { InputTextModule } from 'primeng/inputtext';
import { InputNumberModule } from 'primeng/inputnumber';
import { ToggleSwitchModule } from 'primeng/toggleswitch';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';

import { SiteService } from '../../core/services/site.service';
import {
  SiteDetail,
  SiteOperatingModel,
  ConnectivityMode,
  FiscalizationMode,
  Product,
  Pump,
  ToleranceConfig,
  UpdateSiteRequest,
  AddPumpRequest,
  UpdateNozzleRequest,
} from '../../core/models/site.model';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { StatusBadgeComponent } from '../../shared/components/status-badge/status-badge.component';
import { UtcDatePipe } from '../../shared/pipes/utc-date.pipe';
import { RoleVisibleDirective } from '../../shared/directives/role-visible.directive';
import { PumpMappingComponent, NozzleUpdateEvent } from './pump-mapping.component';

@Component({
  selector: 'app-site-detail',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    ButtonModule,
    CardModule,
    SelectModule,
    InputTextModule,
    InputNumberModule,
    ToggleSwitchModule,
    ToastModule,
    EmptyStateComponent,
    StatusBadgeComponent,
    UtcDatePipe,
    RoleVisibleDirective,
    PumpMappingComponent,
  ],
  providers: [MessageService],
  template: `
    <p-toast />

    <div class="page-container">
      <!-- Back button -->
      <div class="page-header">
        <p-button
          label="Back to Sites"
          icon="pi pi-arrow-left"
          severity="secondary"
          size="small"
          (onClick)="goBack()"
        />
      </div>

      @if (loading()) {
        <app-empty-state icon="pi-spin pi-spinner" title="Loading…" description="" />
      } @else if (!site()) {
        <app-empty-state
          icon="pi-exclamation-circle"
          title="Site not found"
          description="This site could not be loaded."
        />
      } @else {
        <!-- Title row -->
        <div class="detail-title-row">
          <div class="title-left">
            <h1 class="detail-title">
              <i class="pi pi-building"></i>
              {{ site()!.siteName }}
              <code class="site-code-badge">{{ site()!.siteCode }}</code>
            </h1>
            <app-status-badge
              [label]="site()!.isActive ? 'Active' : 'Inactive'"
              [severity]="site()!.isActive ? 'success' : 'secondary'"
            />
          </div>

          <!-- Edit / Save / Cancel actions — FccAdmin and FccUser only -->
          <ng-container *appRoleVisible="['FccAdmin', 'FccUser']">
            <div class="action-buttons">
              @if (!editMode()) {
                <p-button
                  label="Edit"
                  icon="pi pi-pencil"
                  (onClick)="enterEditMode()"
                />
              } @else {
                <p-button
                  label="Cancel"
                  severity="secondary"
                  icon="pi pi-times"
                  [disabled]="saving()"
                  (onClick)="cancelEdit()"
                />
                <p-button
                  label="Save Changes"
                  icon="pi pi-check"
                  [disabled]="!canSave()"
                  [loading]="saving()"
                  (onClick)="saveAll()"
                />
              }
            </div>
          </ng-container>
        </div>

        <!-- ── Site info section ───────────────────────────────────────────── -->
        <p-card styleClass="section-card">
          <ng-template pTemplate="header">
            <div class="section-header">
              <span><i class="pi pi-info-circle"></i> Site Information</span>
            </div>
          </ng-template>

          <div class="field-grid">
            <!-- Read-only fields -->
            <div class="field">
              <span class="field-label">Site Code</span>
              <code class="field-value code">{{ site()!.siteCode }}</code>
            </div>
            <div class="field">
              <span class="field-label">Site Name</span>
              <span class="field-value">{{ site()!.siteName }}</span>
            </div>
            <div class="field">
              <span class="field-label">Timezone</span>
              <span class="field-value">{{ site()!.timezone ?? '—' }}</span>
            </div>
            <div class="field">
              <span class="field-label">Operator</span>
              <span class="field-value">{{ site()!.operatorName ?? '—' }}</span>
            </div>
            <div class="field">
              <span class="field-label">Last Updated</span>
              <span class="field-value">{{ site()!.updatedAt ? (site()!.updatedAt! | utcDate: 'medium') : '—' }}</span>
            </div>

            <!-- Editable fields -->
            <div class="field">
              <span class="field-label">Operating Model</span>
              @if (editMode()) {
                <p-select
                  [options]="operatingModelOptions"
                  [(ngModel)]="draftOperatingModel"
                  placeholder="Select model"
                />
              } @else {
                <span class="field-value">{{ site()!.operatingModel }}</span>
              }
            </div>

            <div class="field">
              <span class="field-label">Connectivity Mode</span>
              @if (editMode()) {
                <p-select
                  [options]="connectivityModeOptions"
                  [(ngModel)]="draftConnectivityMode"
                  placeholder="Select mode"
                  [showClear]="true"
                />
              } @else {
                <span class="field-value">{{ site()!.connectivityMode ?? '—' }}</span>
              }
            </div>

            <div class="field">
              <span class="field-label">Uses Pre-Auth</span>
              @if (editMode()) {
                <p-toggleswitch
                  inputId="site-uses-preauth"
                  [(ngModel)]="draftSiteUsesPreAuth"
                />
              } @else {
                <span class="field-value">{{ site()!.siteUsesPreAuth ? 'Yes' : 'No' }}</span>
              }
            </div>
          </div>
        </p-card>

        <!-- ── Adapter configuration section ──────────────────────────────── -->
        <p-card styleClass="section-card">
          <ng-template pTemplate="header">
            <div class="section-header">
              <span><i class="pi pi-sliders-h"></i> Adapter Configuration</span>
            </div>
          </ng-template>

          @if (!site()!.fcc) {
            <app-empty-state
              icon="pi pi-sliders-h"
              title="No adapter is configured"
              description="This site does not currently have an active adapter binding."
            />
          } @else {
            <div class="field-grid">
              <div class="field">
                <span class="field-label">Adapter</span>
                <span class="field-value">{{ site()!.fcc!.vendor ?? '—' }}</span>
              </div>
              <div class="field">
                <span class="field-label">Connection Protocol</span>
                <span class="field-value">{{ site()!.fcc!.connectionProtocol ?? '—' }}</span>
              </div>
              <div class="field">
                <span class="field-label">Host Address</span>
                <span class="field-value">{{ site()!.fcc!.hostAddress ?? '—' }}</span>
              </div>
              <div class="field">
                <span class="field-label">Port</span>
                <span class="field-value">{{ site()!.fcc!.port ?? '—' }}</span>
              </div>
            </div>

            <div class="adapter-config-actions">
              <p-button
                label="Open Adapter Config"
                icon="pi pi-external-link"
                severity="secondary"
                (onClick)="openAdapterConfig()"
              />
            </div>

            <small class="adapter-config-hint">
              Adapter defaults, site overrides, secrets, and audit history are managed from the
              Adapters area.
            </small>
          }
        </p-card>

        <!-- ── Pump mapping section ───────────────────────────────────────── -->
        <app-pump-mapping
          [pumps]="pumps()"
          [products]="products()"
          [editMode]="editMode()"
          [mutating]="pumpMutating()"
          (pumpAdded)="onPumpAdded($event)"
          (pumpRemoved)="onPumpRemoved($event)"
          (nozzleUpdated)="onNozzleUpdated($event)"
        />

        <!-- ── Tolerance configuration ────────────────────────────────────── -->
        <p-card styleClass="section-card">
          <ng-template pTemplate="header">
            <div class="section-header">
              <span><i class="pi pi-sliders-v"></i> Reconciliation Tolerances</span>
            </div>
          </ng-template>

          <div class="form-grid">
            <div class="form-field">
              <label for="tolerance-amt-pct">Amount Tolerance (%)</label>
              @if (editMode()) {
                <p-inputnumber
                  id="tolerance-amt-pct"
                  [(ngModel)]="draftTolerance.amountTolerancePct"
                  [min]="0"
                  [max]="100"
                  [maxFractionDigits]="2"
                  [showButtons]="false"
                  [useGrouping]="false"
                />
              } @else {
                <span class="field-value">
                  {{ site()!.tolerance?.amountTolerancePct ?? 0 | number: '1.2-2' }}%
                </span>
              }
            </div>

            <div class="form-field">
              <label for="tolerance-amt-abs">Amount Tolerance Absolute (minor units)</label>
              @if (editMode()) {
                <p-inputnumber
                  id="tolerance-amt-abs"
                  [(ngModel)]="draftTolerance.amountToleranceAbsoluteMinorUnits"
                  [min]="0"
                  [showButtons]="false"
                  [useGrouping]="false"
                />
              } @else {
                <span class="field-value">
                  {{ site()!.tolerance?.amountToleranceAbsoluteMinorUnits ?? 0 }}
                </span>
              }
            </div>

            <div class="form-field">
              <label for="tolerance-time-win">Time Window (minutes)</label>
              @if (editMode()) {
                <p-inputnumber
                  id="tolerance-time-win"
                  [(ngModel)]="draftTolerance.timeWindowMinutes"
                  [min]="1"
                  [max]="1440"
                  [showButtons]="false"
                  [useGrouping]="false"
                />
              } @else {
                <span class="field-value">
                  {{ site()!.tolerance?.timeWindowMinutes ?? '—' }}
                </span>
              }
            </div>
          </div>
        </p-card>

        <!-- ── Fiscalization section ───────────────────────────────────────── -->
        <p-card styleClass="section-card">
          <ng-template pTemplate="header">
            <div class="section-header">
              <span><i class="pi pi-receipt"></i> Fiscalization</span>
            </div>
          </ng-template>

          <div class="form-grid">
            <div class="form-field">
              <label for="fiscal-mode">Fiscalization Mode</label>
              @if (editMode()) {
                <p-select
                  id="fiscal-mode"
                  [options]="fiscalizationModeOptions"
                  [(ngModel)]="draftFiscalization.mode"
                  placeholder="Select mode"
                />
              } @else {
                <span class="field-value">{{ site()!.fiscalization?.mode ?? '—' }}</span>
              }
            </div>

            @if (editMode() ? (draftFiscalization.mode !== 'NONE' && draftFiscalization.mode) : (site()!.fiscalization?.mode !== 'NONE' && site()!.fiscalization?.mode)) {
              <div class="form-field">
                <label for="fiscal-tax-endpoint">Tax Authority Endpoint</label>
                @if (editMode()) {
                  <input
                    id="fiscal-tax-endpoint"
                    pInputText
                    [(ngModel)]="draftFiscalization.taxAuthorityEndpoint"
                    placeholder="https://…"
                  />
                } @else {
                  <span class="field-value">
                    {{ site()!.fiscalization?.taxAuthorityEndpoint ?? '—' }}
                  </span>
                }
              </div>
            }

            <div class="form-field form-field--toggle">
              @if (editMode()) {
                <p-toggleswitch
                  id="fiscal-require-tax-id"
                  [(ngModel)]="draftFiscalization.requireCustomerTaxId"
                />
              } @else {
                <p-toggleswitch
                  id="fiscal-require-tax-id"
                  [ngModel]="site()!.fiscalization?.requireCustomerTaxId ?? false"
                  [disabled]="true"
                />
              }
              <label for="fiscal-require-tax-id">Require Customer Tax ID</label>
            </div>

            <div class="form-field form-field--toggle">
              @if (editMode()) {
                <p-toggleswitch
                  id="fiscal-receipt-required"
                  [(ngModel)]="draftFiscalization.fiscalReceiptRequired"
                />
              } @else {
                <p-toggleswitch
                  id="fiscal-receipt-required"
                  [ngModel]="site()!.fiscalization?.fiscalReceiptRequired ?? false"
                  [disabled]="true"
                />
              }
              <label for="fiscal-receipt-required">Fiscal Receipt Required</label>
              @if (!editMode() && site()!.fiscalization?.mode === 'FCC_DIRECT') {
                <small class="fcc-direct-hint">Always enforced for FCC_DIRECT mode</small>
              }
            </div>
          </div>
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
        margin-bottom: 1.25rem;
      }
      .detail-title-row {
        display: flex;
        align-items: flex-start;
        justify-content: space-between;
        flex-wrap: wrap;
        gap: 1rem;
        margin-bottom: 1.25rem;
      }
      .title-left {
        display: flex;
        align-items: center;
        gap: 0.75rem;
        flex-wrap: wrap;
      }
      .detail-title {
        font-size: 1.4rem;
        font-weight: 700;
        margin: 0;
        display: flex;
        align-items: center;
        gap: 0.5rem;
        color: var(--p-text-color, #1e293b);
      }
      .site-code-badge {
        font-family: monospace;
        font-size: 0.9rem;
        background: var(--p-surface-100, #f1f5f9);
        padding: 0.1rem 0.45rem;
        border-radius: 4px;
        color: var(--p-text-muted-color, #475569);
      }
      .action-buttons {
        display: flex;
        gap: 0.5rem;
        flex-wrap: wrap;
      }
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
      /* section-card margin handled via gap in column layout */
      :host ::ng-deep .section-card {
        margin-bottom: 1rem;
      }
      .field-grid {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
        gap: 0.75rem 1.25rem;
      }
      .field {
        display: flex;
        flex-direction: column;
        gap: 0.25rem;
      }
      .field-label {
        font-size: 0.72rem;
        font-weight: 600;
        text-transform: uppercase;
        letter-spacing: 0.04em;
        color: var(--p-text-muted-color, #64748b);
      }
      .field-value {
        font-size: 0.9rem;
        color: var(--p-text-color, #1e293b);
      }
      .field-value.code {
        font-family: monospace;
        font-size: 0.82rem;
        background: var(--p-surface-100, #f1f5f9);
        padding: 0.1rem 0.35rem;
        border-radius: 4px;
      }
      .adapter-config-actions {
        margin-top: 1rem;
      }
      .adapter-config-hint {
        display: block;
        margin-top: 0.75rem;
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
      }
      .form-field--toggle label {
        text-transform: none;
        letter-spacing: 0;
        font-size: 0.9rem;
        font-weight: 500;
        color: var(--p-text-color);
      }
      .fcc-direct-hint {
        font-size: 0.75rem;
        color: var(--p-orange-600, #ea580c);
        font-style: italic;
      }
    `,
  ],
})
export class SiteDetailComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly siteService = inject(SiteService);
  private readonly messageService = inject(MessageService);
  private readonly destroyRef = inject(DestroyRef);

  // ── State ─────────────────────────────────────────────────────────────────
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly pumpMutating = signal(false); // F10-07: loading state for pump/nozzle mutations
  readonly site = signal<SiteDetail | null>(null);
  readonly pumps = signal<Pump[]>([]);
  readonly products = signal<Product[]>([]);
  readonly editMode = signal(false);

  // ── Edit drafts ───────────────────────────────────────────────────────────
  draftOperatingModel: SiteOperatingModel | null = null;
  draftConnectivityMode: ConnectivityMode | null = null;
  draftSiteUsesPreAuth = false;
  draftTolerance: ToleranceConfig = {
    amountTolerancePct: 0,
    amountToleranceAbsoluteMinorUnits: 0,
    timeWindowMinutes: 60,
  };
  draftFiscalization: {
    mode: FiscalizationMode | null;
    taxAuthorityEndpoint: string | null;
    requireCustomerTaxId: boolean;
    fiscalReceiptRequired: boolean;
  } = {
    mode: null,
    taxAuthorityEndpoint: null,
    requireCustomerTaxId: false,
    fiscalReceiptRequired: false,
  };

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

  readonly fiscalizationModeOptions = [
    { label: 'FCC Direct', value: FiscalizationMode.FCC_DIRECT },
    { label: 'External Integration', value: FiscalizationMode.EXTERNAL_INTEGRATION },
    { label: 'None', value: FiscalizationMode.NONE },
  ];

  ngOnInit(): void {
    const id = this.route.snapshot.paramMap.get('id');
    if (!id) {
      this.router.navigate(['/sites/list']);
      return;
    }
    this.loadSite(id);
  }

  goBack(): void {
    this.router.navigate(['/sites/list']);
  }

  openAdapterConfig(): void {
    const s = this.site();
    const adapterKey = s?.fcc?.vendor;
    if (!s || !adapterKey) {
      return;
    }

    this.router.navigate(['/adapters', adapterKey, 'sites', s.id], {
      queryParams: { legalEntityId: s.legalEntityId },
    });
  }

  enterEditMode(): void {
    const s = this.site();
    if (!s) return;
    this.draftOperatingModel = s.operatingModel;
    this.draftConnectivityMode = s.connectivityMode;
    this.draftSiteUsesPreAuth = s.siteUsesPreAuth;
    this.draftTolerance = s.tolerance
      ? { ...s.tolerance }
      : { amountTolerancePct: 0, amountToleranceAbsoluteMinorUnits: 0, timeWindowMinutes: 60 };
    this.draftFiscalization = {
      mode: s.fiscalization?.mode ?? null,
      taxAuthorityEndpoint: s.fiscalization?.taxAuthorityEndpoint ?? null,
      requireCustomerTaxId: s.fiscalization?.requireCustomerTaxId ?? false,
      fiscalReceiptRequired: s.fiscalization?.fiscalReceiptRequired ?? false,
    };
    this.editMode.set(true);
  }

  cancelEdit(): void {
    this.editMode.set(false);
  }

  canSave(): boolean {
    return !this.saving() && !!this.draftOperatingModel;
  }

  saveAll(): void {
    const s = this.site();
    if (!s || !this.canSave()) return;

    this.saving.set(true);

    const siteUpdate: UpdateSiteRequest = {
      operatingModel: this.draftOperatingModel ?? undefined,
      connectivityMode: this.draftConnectivityMode ?? undefined,
      siteUsesPreAuth: this.draftSiteUsesPreAuth,
      tolerance: { ...this.draftTolerance },
      fiscalization: {
        mode: this.draftFiscalization.mode ?? undefined,
        taxAuthorityEndpoint: this.draftFiscalization.taxAuthorityEndpoint,
        requireCustomerTaxId: this.draftFiscalization.requireCustomerTaxId,
        fiscalReceiptRequired: this.draftFiscalization.fiscalReceiptRequired,
      },
    };

    this.siteService.updateSite(s.id, siteUpdate).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (updatedSite) => this.onSaveSuccess(updatedSite),
      error: () => this.onSaveError(),
    });
  }

  onPumpAdded(req: AddPumpRequest): void {
    const s = this.site();
    if (!s || this.pumpMutating()) return;
    this.pumpMutating.set(true);
    this.siteService
      .addPump(s.id, req)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (pump) => {
          this.pumps.update((list) => [...list, pump]);
          this.pumpMutating.set(false);
          this.messageService.add({
            severity: 'success',
            summary: 'Pump added',
            detail: `Pump ${pump.pumpNumber} has been added.`,
            life: 3000,
          });
        },
        error: () => {
          this.pumpMutating.set(false);
          this.messageService.add({
            severity: 'error',
            summary: 'Failed to add pump',
            detail: 'Could not add the pump. Please try again.',
            life: 4000,
          });
        },
      });
  }

  onPumpRemoved(pumpId: string): void {
    const s = this.site();
    if (!s || this.pumpMutating()) return;
    this.pumpMutating.set(true);
    this.siteService
      .removePump(s.id, pumpId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.pumps.update((list) => list.filter((p) => p.id !== pumpId));
          this.pumpMutating.set(false);
          this.messageService.add({
            severity: 'success',
            summary: 'Pump removed',
            detail: 'Pump has been removed.',
            life: 3000,
          });
        },
        error: () => {
          this.pumpMutating.set(false);
          this.messageService.add({
            severity: 'error',
            summary: 'Failed to remove pump',
            detail: 'Could not remove the pump. Please try again.',
            life: 4000,
          });
        },
      });
  }

  onNozzleUpdated(event: NozzleUpdateEvent): void {
    const s = this.site();
    if (!s || this.pumpMutating()) return;
    this.pumpMutating.set(true);
    const req: UpdateNozzleRequest = { canonicalProductCode: event.canonicalProductCode };
    this.siteService
      .updateNozzle(s.id, event.pumpId, event.nozzleNumber, req)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.pumpMutating.set(false);
          this.messageService.add({
            severity: 'success',
            summary: 'Nozzle updated',
            detail: `Nozzle ${event.nozzleNumber} product mapping saved.`,
            life: 3000,
          });
        },
        error: () => {
          this.pumpMutating.set(false);
          this.messageService.add({
            severity: 'error',
            summary: 'Failed to update nozzle',
            detail: 'Could not update nozzle mapping. Please try again.',
            life: 4000,
          });
        },
      });
  }

  // ── Private ───────────────────────────────────────────────────────────────

  private loadSite(id: string): void {
    this.loading.set(true);
    this.siteService
      .getSiteDetail(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (detail) => {
          this.site.set(detail);
          this.pumps.set(detail.pumps ?? []);
          this.loading.set(false);
          this.loadProducts(detail.legalEntityId);
        },
        error: () => {
          this.site.set(null);
          this.loading.set(false);
          this.messageService.add({
            severity: 'error',
            summary: 'Load failed',
            detail: 'Could not load site configuration.',
            life: 5000,
          });
        },
      });
  }

  private loadProducts(legalEntityId: string): void {
    this.siteService
      .getProducts(legalEntityId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ next: (p) => this.products.set(p), error: () => this.products.set([]) });
  }

  private onSaveSuccess(updatedSite: SiteDetail): void {
    this.site.set(updatedSite);
    this.editMode.set(false);
    this.saving.set(false);
    this.messageService.add({
      severity: 'success',
      summary: 'Changes saved',
      detail: 'Site configuration has been updated. Config version incremented.',
      life: 4000,
    });
  }

  private onSaveError(): void {
    this.saving.set(false);
    this.messageService.add({
      severity: 'error',
      summary: 'Save failed',
      detail: 'Could not save site configuration. Please try again.',
      life: 5000,
    });
  }
}
