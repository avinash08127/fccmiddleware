import { CommonModule } from '@angular/common';
import { Component, DestroyRef, OnInit, ViewChild, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ButtonModule } from 'primeng/button';
import { CardModule } from 'primeng/card';
import { MessageService } from 'primeng/api';
import { ToastModule } from 'primeng/toast';
import { WRITE_ROLES } from '../../core/auth/auth-state';
import { AdapterService } from '../../core/services/adapter.service';
import { SiteAdapterConfig } from '../../core/models';
import { RoleVisibleDirective } from '../../shared/directives/role-visible.directive';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { AdapterConfigFormComponent } from './adapter-config-form.component';

@Component({
  selector: 'app-site-adapter-config',
  standalone: true,
  imports: [
    CommonModule,
    ButtonModule,
    CardModule,
    ToastModule,
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
          label="Back to Adapter"
          icon="pi pi-arrow-left"
          severity="secondary"
          size="small"
          (onClick)="goBack()"
        />
      </div>

      @if (loading()) {
        <app-empty-state icon="pi pi-spinner pi-spin" title="Loading…" description="" />
      } @else if (!config()) {
        <app-empty-state icon="pi pi-exclamation-circle" title="Site adapter config not found" description="" />
      } @else {
        <div class="detail-title-row">
          <div>
            <h1>{{ config()!.siteCode }} · {{ config()!.siteName }}</h1>
            <p class="subtitle">
              {{ config()!.adapterKey }} · override version {{ config()!.overrideVersion ?? '0' }}
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
                <p-button label="Edit Site Config" icon="pi pi-pencil" (onClick)="enterEditMode()" />
                <p-button
                  label="Reset Override"
                  severity="secondary"
                  icon="pi pi-refresh"
                  [disabled]="!config()!.overrideVersion"
                  (onClick)="resetOverride()"
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
                  label="Save Site Config"
                  icon="pi pi-check"
                  [disabled]="!form?.isValid() || saving()"
                  [loading]="saving()"
                  (onClick)="save()"
                />
              }
            </ng-container>
          </div>
        </div>

        <p-card styleClass="section-card">
          <ng-template pTemplate="header">
            <div class="section-header">Effective Site Config</div>
          </ng-template>

          <app-adapter-config-form
            #form
            [schema]="config()!.schema"
            [values]="draftValues()"
            [secretState]="config()!.secretState"
            [fieldSources]="config()!.fieldSources"
            [mode]="'site'"
            [editMode]="editMode()"
            (formChange)="draftValues.set($event)"
          />
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
        flex-wrap: wrap;
      }
      .section-card {
        margin-bottom: 1rem;
      }
      .section-header {
        padding: 0.75rem 1.25rem;
        font-weight: 700;
      }
    `,
  ],
})
export class SiteAdapterConfigComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly adapterService = inject(AdapterService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly messageService = inject(MessageService);

  @ViewChild('form') form?: AdapterConfigFormComponent;

  readonly config = signal<SiteAdapterConfig | null>(null);
  readonly draftValues = signal<Record<string, unknown>>({});
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly editMode = signal(false);
  protected readonly writeRoles = WRITE_ROLES;

  private siteId = '';
  private adapterKey = '';
  private legalEntityId = '';

  ngOnInit(): void {
    this.siteId = this.route.snapshot.paramMap.get('siteId') ?? '';
    this.adapterKey = this.route.snapshot.paramMap.get('adapterKey') ?? '';
    this.legalEntityId = this.route.snapshot.queryParamMap.get('legalEntityId') ?? '';
    if (!this.siteId) {
      this.goBack();
      return;
    }
    this.load();
  }

  goBack(): void {
    if (this.adapterKey) {
      this.router.navigate(['/adapters', this.adapterKey], {
        queryParams: this.legalEntityId ? { legalEntityId: this.legalEntityId } : undefined,
      });
      return;
    }

    this.router.navigate(['/adapters']);
  }

  enterEditMode(): void {
    this.draftValues.set(this.clone(this.config()?.effectiveValues ?? {}));
    this.editMode.set(true);
  }

  cancelEdit(): void {
    this.editMode.set(false);
    this.draftValues.set(this.clone(this.config()?.effectiveValues ?? {}));
  }

  save(): void {
    if (!this.form?.isValid()) return;
    const reason = window.prompt('Enter a reason for this site adapter change:');
    if (!reason?.trim()) return;

    this.saving.set(true);
    this.adapterService
      .updateSiteAdapterConfig(this.siteId, {
        reason: reason.trim(),
        effectiveValues: this.draftValues(),
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (config) => {
          this.config.set(config);
          this.draftValues.set(this.clone(config.effectiveValues));
          this.editMode.set(false);
          this.saving.set(false);
          this.messageService.add({
            severity: 'success',
            summary: 'Site config saved',
            detail: 'The site adapter configuration has been updated.',
          });
        },
        error: () => {
          this.saving.set(false);
          this.messageService.add({
            severity: 'error',
            summary: 'Save failed',
            detail: 'Could not update the site adapter configuration.',
          });
        },
      });
  }

  resetOverride(): void {
    if (!this.config()?.overrideVersion) return;
    const confirmed = window.confirm(
      'Reset this site override back to inherited adapter defaults?',
    );
    if (!confirmed) return;

    const reason = window.prompt('Enter a reason for resetting this site override:');
    if (!reason?.trim()) return;

    this.adapterService
      .resetSiteAdapterConfig(this.siteId, { reason: reason.trim() })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (config) => {
          this.config.set(config);
          this.draftValues.set(this.clone(config.effectiveValues));
          this.editMode.set(false);
          this.messageService.add({
            severity: 'success',
            summary: 'Override reset',
            detail: 'The site now inherits adapter defaults again.',
          });
        },
        error: () => {
          this.messageService.add({
            severity: 'error',
            summary: 'Reset failed',
            detail: 'Could not reset the site override.',
          });
        },
      });
  }

  openAuditHistory(): void {
    const current = this.config();
    if (!current) return;

    this.router.navigate(['/audit/list'], {
      queryParams: {
        legalEntityId: current.legalEntityId,
        siteCode: current.siteCode,
        adapterKey: current.adapterKey,
        eventTypes: ['SiteAdapterOverrideSet', 'SiteAdapterOverrideCleared', 'SiteAdapterOverrideResetToDefault'],
        from: this.defaultAuditFrom(),
        to: new Date().toISOString(),
      },
    });
  }

  private load(): void {
    this.loading.set(true);
    this.adapterService
      .getSiteAdapterConfig(this.siteId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (config) => {
          this.config.set(config);
          this.draftValues.set(this.clone(config.effectiveValues));
          this.loading.set(false);
        },
        error: () => {
          this.config.set(null);
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
