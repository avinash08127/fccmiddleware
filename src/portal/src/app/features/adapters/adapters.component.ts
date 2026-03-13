import { CommonModule } from '@angular/common';
import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CardModule } from 'primeng/card';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { TableModule } from 'primeng/table';
import { SelectModule } from 'primeng/select';
import { MasterDataService } from '../../core/services/master-data.service';
import { AdapterService } from '../../core/services/adapter.service';
import { AdapterSummary, LegalEntity } from '../../core/models';

@Component({
  selector: 'app-adapters',
  standalone: true,
  imports: [CommonModule, FormsModule, TableModule, CardModule, SelectModule, EmptyStateComponent],
  template: `
    <div class="page-container">
      <div class="page-header">
        <h1 class="page-title"><i class="pi pi-sliders-h"></i> Adapters</h1>
        <p-select
          [options]="legalEntityOptions()"
          [ngModel]="selectedLegalEntityId()"
          (ngModelChange)="onLegalEntityChange($event)"
          optionLabel="label"
          optionValue="value"
          placeholder="Select Legal Entity"
          styleClass="entity-selector"
        />
      </div>

      @if (!selectedLegalEntityId()) {
        <app-empty-state
          icon="pi pi-building"
          title="Select a Legal Entity"
          description="Choose a legal entity to view adapter defaults."
        />
      } @else {
        <p-card styleClass="table-card">
          <p-table
            [value]="adapters()"
            [loading]="loading()"
            styleClass="p-datatable-sm p-datatable-striped p-datatable-hoverable-rows"
          >
            <ng-template pTemplate="header">
              <tr>
                <th>Adapter</th>
                <th>Vendor</th>
                <th>Protocols</th>
                <th>Modes</th>
                <th>Sites</th>
                <th>Default Version</th>
                <th>Updated</th>
              </tr>
            </ng-template>

            <ng-template pTemplate="body" let-adapter>
              <tr class="clickable-row" (click)="openDetail(adapter)">
                <td>
                  <div class="name-cell">
                    <strong>{{ adapter.displayName }}</strong>
                    <small>{{ adapter.adapterKey }}</small>
                  </div>
                </td>
                <td>{{ adapter.vendor }}</td>
                <td>{{ adapter.supportedProtocols.join(', ') }}</td>
                <td>{{ adapter.supportedIngestionMethods.join(', ') }}</td>
                <td>{{ adapter.activeSiteCount }}</td>
                <td>{{ adapter.defaultConfigVersion || '—' }}</td>
                <td>{{ adapter.defaultUpdatedAt ?? '—' }}</td>
              </tr>
            </ng-template>

            <ng-template pTemplate="emptymessage">
              <tr>
                <td colspan="7">
                  <app-empty-state
                    icon="pi pi-sliders-h"
                    title="No adapters found"
                    description="No adapter registrations are available for this legal entity."
                  />
                </td>
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
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 1rem;
        margin-bottom: 1.25rem;
        flex-wrap: wrap;
      }
      .clickable-row {
        cursor: pointer;
      }
      .name-cell {
        display: flex;
        flex-direction: column;
        gap: 0.15rem;
      }
      .name-cell small {
        color: var(--p-text-muted-color, #64748b);
      }
    `,
  ],
})
export class AdaptersComponent implements OnInit {
  private readonly masterDataService = inject(MasterDataService);
  private readonly adapterService = inject(AdapterService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly loading = signal(false);
  readonly legalEntities = signal<LegalEntity[]>([]);
  readonly selectedLegalEntityId = signal<string | null>(null);
  readonly adapters = signal<AdapterSummary[]>([]);

  readonly legalEntityOptions = computed(() =>
    this.legalEntities().map((item) => ({
      label: `${item.name} (${item.code})`,
      value: item.id,
    })),
  );

  ngOnInit(): void {
    this.masterDataService
      .getLegalEntities()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((entities) => {
        this.legalEntities.set(entities);
        if (entities.length === 1) {
          this.onLegalEntityChange(entities[0].id);
        }
      });
  }

  onLegalEntityChange(legalEntityId: string | null): void {
    this.selectedLegalEntityId.set(legalEntityId);
    this.adapters.set([]);
    if (!legalEntityId) return;

    this.loading.set(true);
    this.adapterService
      .getAdapters(legalEntityId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (adapters) => {
          this.adapters.set(adapters);
          this.loading.set(false);
        },
        error: () => this.loading.set(false),
      });
  }

  openDetail(adapter: AdapterSummary): void {
    const legalEntityId = this.selectedLegalEntityId();
    if (!legalEntityId) return;
    this.router.navigate(['/adapters', adapter.adapterKey], {
      queryParams: { legalEntityId },
    });
  }
}
