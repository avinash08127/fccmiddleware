import { Component, DestroyRef, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { interval, filter, Subject, EMPTY, switchMap, catchError } from 'rxjs';
import { CardModule } from 'primeng/card';
import { ButtonModule } from 'primeng/button';
import { SelectModule } from 'primeng/select';

import { DashboardService } from './dashboard.service';
import { DashboardAlert, DashboardSummary, DashboardQueryParams } from './dashboard.model';
import { MasterDataService } from '../../core/services/master-data.service';
import { LegalEntity } from '../../core/models/master-data.model';

import { TransactionVolumeChartComponent } from './components/transaction-volume-chart/transaction-volume-chart.component';
import { IngestionHealthComponent } from './components/ingestion-health/ingestion-health.component';
import { AgentStatusSummaryComponent } from './components/agent-status-summary/agent-status-summary.component';
import { ReconciliationSummaryComponent } from './components/reconciliation-summary/reconciliation-summary.component';
import { StaleTransactionsComponent } from './components/stale-transactions/stale-transactions.component';
import { ActiveAlertsComponent } from './components/active-alerts/active-alerts.component';

interface SelectOption {
  label: string;
  value: string;
}

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    CardModule,
    ButtonModule,
    SelectModule,
    TransactionVolumeChartComponent,
    IngestionHealthComponent,
    AgentStatusSummaryComponent,
    ReconciliationSummaryComponent,
    StaleTransactionsComponent,
    ActiveAlertsComponent,
  ],
  template: `
    <!-- Dashboard toolbar -->
    <div class="dashboard-toolbar">
      <div class="dashboard-toolbar__left">
        <h1 class="dashboard-title">
          <i class="pi pi-home"></i> Dashboard
        </h1>
        @if (lastRefreshedAt()) {
          <span class="last-refreshed">
            Last updated {{ lastRefreshedAt() | date: 'shortTime' }}
          </span>
        }
      </div>
      <div class="dashboard-toolbar__right">
        <p-select
          [options]="legalEntityOptions()"
          [ngModel]="selectedLegalEntityId()"
          (ngModelChange)="onLegalEntityChange($event)"
          optionLabel="label"
          optionValue="value"
          placeholder="All Legal Entities"
          [showClear]="true"
          styleClass="entity-selector"
        />
        <p-button
          icon="pi pi-refresh"
          [label]="refreshing() ? 'Refreshing…' : 'Refresh'"
          severity="secondary"
          [disabled]="refreshing()"
          (onClick)="loadAll()"
        />
      </div>
    </div>

    <!-- Widget grid -->
    <div class="dashboard-grid">
      <!-- Row 1: Transaction Volume (wide) + Ingestion Health (narrow) -->
      <div class="widget widget--two-thirds">
        <app-transaction-volume-chart
          [data]="summary()?.transactionVolume ?? null"
          [loading]="summaryLoading()"
          [error]="summaryError()"
        />
      </div>
      <div class="widget widget--third">
        <app-ingestion-health
          [data]="summary()?.ingestionHealth ?? null"
          [loading]="summaryLoading()"
          [error]="summaryError()"
        />
      </div>

      <!-- Row 2: Three equal widgets -->
      <div class="widget widget--third">
        <app-agent-status-summary
          [data]="summary()?.agentStatus ?? null"
          [loading]="summaryLoading()"
          [error]="summaryError()"
        />
      </div>
      <div class="widget widget--third">
        <app-reconciliation-summary
          [data]="summary()?.reconciliation ?? null"
          [loading]="summaryLoading()"
          [error]="summaryError()"
        />
      </div>
      <div class="widget widget--third">
        <app-stale-transactions
          [data]="summary()?.staleTransactions ?? null"
          [loading]="summaryLoading()"
          [error]="summaryError()"
        />
      </div>

      <!-- Row 3: Active Alerts (full width) -->
      <div class="widget widget--full">
        <app-active-alerts
          [alerts]="alerts()"
          [loading]="alertsLoading()"
          [error]="alertsError()"
        />
      </div>
    </div>
  `,
  styles: [
    `
      :host {
        display: block;
        padding: 1.5rem;
      }

      .dashboard-toolbar {
        display: flex;
        align-items: center;
        justify-content: space-between;
        margin-bottom: 1.5rem;
        flex-wrap: wrap;
        gap: 1rem;
      }

      .dashboard-toolbar__left {
        display: flex;
        align-items: baseline;
        gap: 1rem;
      }

      .dashboard-toolbar__right {
        display: flex;
        align-items: center;
        gap: 0.75rem;
      }

      .dashboard-title {
        font-size: 1.5rem;
        font-weight: 700;
        margin: 0;
        display: flex;
        align-items: center;
        gap: 0.5rem;
        color: var(--p-text-color, #1e293b);
      }

      .last-refreshed {
        font-size: 0.8rem;
        color: var(--p-text-muted-color, #64748b);
      }

      .entity-selector {
        min-width: 200px;
      }

      .dashboard-grid {
        display: grid;
        grid-template-columns: repeat(12, 1fr);
        gap: 1.25rem;
      }

      .widget {
        min-width: 0; /* prevent overflow in grid */
      }

      .widget--full {
        grid-column: span 12;
      }

      .widget--two-thirds {
        grid-column: span 8;
      }

      .widget--third {
        grid-column: span 4;
      }

      @media (max-width: 1024px) {
        .widget--two-thirds,
        .widget--third {
          grid-column: span 6;
        }
      }

      @media (max-width: 768px) {
        .widget--two-thirds,
        .widget--third {
          grid-column: span 12;
        }

        .dashboard-toolbar {
          flex-direction: column;
          align-items: flex-start;
        }
      }
    `,
  ],
})
export class DashboardComponent {
  private readonly dashboardService = inject(DashboardService);
  private readonly masterDataService = inject(MasterDataService);
  private readonly destroyRef = inject(DestroyRef);

  /** Auto-refresh interval — 60 s. Configurable via environment if needed. */
  private readonly REFRESH_INTERVAL_MS = 60_000;

  // BUG-F01-5: Subjects allow switchMap to cancel previous in-flight requests when filter changes.
  private readonly summaryTrigger$ = new Subject<void>();
  private readonly alertsTrigger$ = new Subject<void>();

  // ── Legal entity filter ────────────────────────────────────────────────────
  private readonly legalEntities = signal<LegalEntity[]>([]);
  readonly selectedLegalEntityId = signal<string | null>(null);

  readonly legalEntityOptions = computed<SelectOption[]>(() =>
    this.legalEntities().map((e) => ({ label: e.name, value: e.id })),
  );

  // ── Summary data ───────────────────────────────────────────────────────────
  readonly summary = signal<DashboardSummary | null>(null);
  readonly summaryLoading = signal(true);
  readonly summaryError = signal<string | null>(null);

  // ── Alerts ─────────────────────────────────────────────────────────────────
  readonly alerts = signal<DashboardAlert[]>([]);
  readonly alertsLoading = signal(true);
  readonly alertsError = signal<string | null>(null);

  // ── Refresh state ──────────────────────────────────────────────────────────
  readonly refreshing = computed(() => this.summaryLoading() || this.alertsLoading());
  readonly lastRefreshedAt = signal<Date | null>(null);

  constructor() {
    // Load legal entities for the filter selector
    this.masterDataService
      .getLegalEntities()
      .pipe(takeUntilDestroyed())
      .subscribe({
        next: (entities) => this.legalEntities.set(entities),
        error: () => {}, // Non-critical — selector simply stays empty
      });

    // BUG-F01-5: switchMap cancels the previous in-flight request whenever the trigger fires,
    // preventing stale responses from overwriting newer data when the filter changes rapidly.
    this.summaryTrigger$
      .pipe(
        switchMap(() =>
          this.dashboardService.getSummary(this.getParams()).pipe(
            catchError(() => {
              this.summaryError.set('Failed to load dashboard data. Please refresh.');
              this.summaryLoading.set(false);
              return EMPTY;
            }),
          ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((data) => {
        this.summary.set(data);
        this.summaryLoading.set(false);
        this.lastRefreshedAt.set(new Date());
      });

    this.alertsTrigger$
      .pipe(
        switchMap(() =>
          this.dashboardService.getAlerts(this.getParams()).pipe(
            catchError(() => {
              this.alertsError.set('Failed to load alerts.');
              this.alertsLoading.set(false);
              return EMPTY;
            }),
          ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((data) => {
        this.alerts.set(data.alerts);
        this.alertsLoading.set(false);
      });

    // Initial data load
    this.loadAll();

    // Auto-refresh — pauses when the browser tab is backgrounded (BUG-F01-6 fix)
    interval(this.REFRESH_INTERVAL_MS)
      .pipe(
        filter(() => typeof document === 'undefined' || document.visibilityState === 'visible'),
        takeUntilDestroyed(),
      )
      .subscribe(() => this.loadAll());
  }

  loadAll(): void {
    this.summaryLoading.set(true);
    this.summaryError.set(null);
    this.alertsLoading.set(true);
    this.alertsError.set(null);
    this.summaryTrigger$.next();
    this.alertsTrigger$.next();
  }

  onLegalEntityChange(entityId: string | null): void {
    this.selectedLegalEntityId.set(entityId);
    this.loadAll();
  }

  private getParams(): DashboardQueryParams | undefined {
    const id = this.selectedLegalEntityId();
    return id ? { legalEntityId: id } : undefined;
  }
}
