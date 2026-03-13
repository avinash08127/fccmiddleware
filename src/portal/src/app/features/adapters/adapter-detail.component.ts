import { CommonModule } from '@angular/common';
import { Component, DestroyRef, OnInit, ViewChild, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ButtonModule } from 'primeng/button';
import { CardModule } from 'primeng/card';
import { MessageService } from 'primeng/api';
import { ToastModule } from 'primeng/toast';
import { TableModule } from 'primeng/table';
import { AdapterDetail } from '../../core/models';
import { WRITE_ROLES } from '../../core/auth/auth-state';
import { AdapterService } from '../../core/services/adapter.service';
import { RoleVisibleDirective } from '../../shared/directives/role-visible.directive';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { AdapterConfigFormComponent } from './adapter-config-form.component';

@Component({
  selector: 'app-adapter-detail',
  standalone: true,
  imports: [
    CommonModule,
    ButtonModule,
    CardModule,
    ToastModule,
    TableModule,
    EmptyStateComponent,
    AdapterConfigFormComponent,
    RoleVisibleDirective,
  ],
  providers: [MessageService],
  template: `
    <p-toast />
    <div class="page-container">
      <div class="page-header">
        <p-button
          label="Back to Adapters"
          icon="pi pi-arrow-left"
          severity="secondary"
          size="small"
          (onClick)="goBack()"
        />
      </div>

      @if (loading()) {
        <app-empty-state icon="pi pi-spinner pi-spin" title="Loading…" description="" />
      } @else if (!detail()) {
        <app-empty-state icon="pi pi-exclamation-circle" title="Adapter not found" description="" />
      } @else {
        <div class="detail-title-row">
          <div>
            <h1>{{ detail()!.schema.displayName }}</h1>
            <p class="subtitle">
              {{ detail()!.schema.adapterKey }} · {{ detail()!.schema.vendor }} · v{{ detail()!.schema.adapterVersion }}
            </p>
          </div>

          <div class="actions">
            <p-button
              label="View Audit History"
              icon="pi pi-shield"
              severity="secondary"
              (onClick)="openAuditHistory()"
            />
            <ng-container *appRoleVisible="writeRoles">
              @if (!editMode()) {
                <p-button label="Edit Defaults" icon="pi pi-pencil" (onClick)="enterEditMode()" />
              } @else {
                <p-button
                  label="Cancel"
                  severity="secondary"
                  icon="pi pi-times"
                  [disabled]="saving()"
                  (onClick)="cancelEdit()"
                />
                <p-button
                  label="Save Defaults"
                  icon="pi pi-check"
                  [disabled]="!form?.isValid() || saving()"
                  [loading]="saving()"
                  (onClick)="saveDefaults()"
                />
              }
            </ng-container>
          </div>
        </div>

        <p-card styleClass="section-card">
          <ng-template pTemplate="header">
            <div class="section-header">Overview</div>
          </ng-template>
          <div class="overview-grid">
            <div><strong>Protocols</strong><span>{{ detail()!.schema.supportedProtocols.join(', ') }}</span></div>
            <div><strong>Ingestion</strong><span>{{ detail()!.schema.supportedIngestionMethods.join(', ') }}</span></div>
            <div><strong>Pre-Auth</strong><span>{{ detail()!.schema.supportsPreAuth ? 'Yes' : 'No' }}</span></div>
            <div><strong>Pump Status</strong><span>{{ detail()!.schema.supportsPumpStatus ? 'Yes' : 'No' }}</span></div>
          </div>
        </p-card>

        <p-card styleClass="section-card">
          <ng-template pTemplate="header">
            <div class="section-header">Default Settings</div>
          </ng-template>

          <app-adapter-config-form
            #form
            [schema]="detail()!.schema"
            [values]="draftValues()"
            [secretState]="detail()!.defaultConfig.secretState"
            [mode]="'defaults'"
            [editMode]="editMode()"
            (formChange)="draftValues.set($event)"
          />
        </p-card>

        <p-card styleClass="section-card">
          <ng-template pTemplate="header">
            <div class="section-header">Sites Using This Adapter</div>
          </ng-template>

          <p-table [value]="detail()!.sites" styleClass="p-datatable-sm p-datatable-striped">
            <ng-template pTemplate="header">
              <tr>
                <th>Site</th>
                <th>Override</th>
                <th>Version</th>
                <th>Updated</th>
              </tr>
            </ng-template>
            <ng-template pTemplate="body" let-site>
              <tr class="clickable-row" (click)="openSite(site.siteId)">
                <td>{{ site.siteCode }} · {{ site.siteName }}</td>
                <td>{{ site.hasOverride ? 'Yes' : 'No' }}</td>
                <td>{{ site.overrideVersion ?? '—' }}</td>
                <td>{{ site.overrideUpdatedAt ?? '—' }}</td>
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
        margin-bottom: 1rem;
      }
      .detail-title-row {
        display: flex;
        justify-content: space-between;
        align-items: flex-start;
        gap: 1rem;
        flex-wrap: wrap;
        margin-bottom: 1rem;
      }
      .subtitle {
        margin: 0.25rem 0 0;
        color: var(--p-text-muted-color, #64748b);
      }
      .actions {
        display: flex;
        gap: 0.5rem;
      }
      .section-card {
        margin-bottom: 1rem;
      }
      .section-header {
        padding: 0.75rem 1.25rem;
        font-weight: 700;
      }
      .overview-grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
        gap: 1rem;
      }
      .overview-grid div {
        display: flex;
        flex-direction: column;
        gap: 0.25rem;
      }
      .clickable-row {
        cursor: pointer;
      }
    `,
  ],
})
export class AdapterDetailComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly adapterService = inject(AdapterService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly messageService = inject(MessageService);

  @ViewChild('form') form?: AdapterConfigFormComponent;

  readonly detail = signal<AdapterDetail | null>(null);
  readonly draftValues = signal<Record<string, unknown>>({});
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly editMode = signal(false);
  protected readonly writeRoles = WRITE_ROLES;

  private adapterKey = '';
  private legalEntityId = '';

  ngOnInit(): void {
    this.adapterKey = this.route.snapshot.paramMap.get('adapterKey') ?? '';
    this.legalEntityId = this.route.snapshot.queryParamMap.get('legalEntityId') ?? '';
    if (!this.adapterKey || !this.legalEntityId) {
      this.goBack();
      return;
    }
    this.load();
  }

  goBack(): void {
    this.router.navigate(['/adapters']);
  }

  enterEditMode(): void {
    this.draftValues.set(this.clone(this.detail()?.defaultConfig.values ?? {}));
    this.editMode.set(true);
  }

  cancelEdit(): void {
    this.editMode.set(false);
    this.draftValues.set(this.clone(this.detail()?.defaultConfig.values ?? {}));
  }

  saveDefaults(): void {
    if (!this.form?.isValid()) return;
    const reason = window.prompt('Enter a reason for this adapter default change:');
    if (!reason?.trim()) return;

    this.saving.set(true);
    this.adapterService
      .updateAdapterDefaults(this.adapterKey, {
        legalEntityId: this.legalEntityId,
        reason: reason.trim(),
        values: this.draftValues(),
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.saving.set(false);
          this.editMode.set(false);
          this.messageService.add({
            severity: 'success',
            summary: 'Defaults saved',
            detail: 'Adapter defaults updated successfully.',
          });
          this.load();
        },
        error: () => {
          this.saving.set(false);
          this.messageService.add({
            severity: 'error',
            summary: 'Save failed',
            detail: 'Could not update adapter defaults.',
          });
        },
      });
  }

  openSite(siteId: string): void {
    this.router.navigate(['/adapters', this.adapterKey, 'sites', siteId], {
      queryParams: { legalEntityId: this.legalEntityId },
    });
  }

  openAuditHistory(): void {
    this.router.navigate(['/audit/list'], {
      queryParams: {
        legalEntityId: this.legalEntityId,
        adapterKey: this.adapterKey,
        eventTypes: ['AdapterDefaultConfigUpdated', 'SiteAdapterOverrideSet', 'SiteAdapterOverrideCleared', 'SiteAdapterOverrideResetToDefault'],
        from: this.defaultAuditFrom(),
        to: new Date().toISOString(),
      },
    });
  }

  private load(): void {
    this.loading.set(true);
    this.adapterService
      .getAdapterDetail(this.adapterKey, this.legalEntityId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (detail) => {
          this.detail.set(detail);
          this.draftValues.set(this.clone(detail.defaultConfig.values));
          this.loading.set(false);
        },
        error: () => {
          this.detail.set(null);
          this.loading.set(false);
        },
      });
  }

  private clone<T>(value: T): T {
    return JSON.parse(JSON.stringify(value)) as T;
  }

  private defaultAuditFrom(): string {
    const from = new Date();
    from.setDate(from.getDate() - 7);
    return from.toISOString();
  }
}
